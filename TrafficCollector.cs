using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace TrafficMonitor;

internal sealed class TrafficCollector : IDisposable
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<int, CachedProcessInfo> _processes = new();
    private readonly ConcurrentDictionary<int, string> _services = new();
    private Dictionary<string, string?> _addressesToAdapters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RateWindow> _rates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AdapterCounter> _adapterEtwTotals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AdapterCounter> _adapterBaselines = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _dataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Monita",
        "TrafficMonitor");
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Monita",
        "TrafficMonitor",
        "settings.json");
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _serviceRefreshTask;
    private readonly Task _adapterRefreshTask;
    private Dictionary<string, UsageRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private BundleSettings _settings = new();
    private TraceEventSession? _session;
    private Task? _traceTask;
    private DateOnly _loadedDate = DateOnly.FromDateTime(DateTime.Now);
    private bool _disposed;

    public TrafficCollector()
    {
        Directory.CreateDirectory(_dataDirectory);
        LoadDate(_loadedDate);
        LoadSettings();
        CaptureAdapterBaselines();
        _serviceRefreshTask = Task.Run(() => RefreshServiceMapLoop(_cancellation.Token));
        _adapterRefreshTask = Task.Run(() => RefreshAdaptersLoop(_cancellation.Token));
    }

    public string DataDirectory => _dataDirectory;

    public string Status { get; private set; } = "Starting network trace...";

    public BundleSettings Settings => new()
    {
        MonthlyLimitGB = _settings.MonthlyLimitGB,
        ResetDay = _settings.ResetDay,
        AppLimitsGB = new Dictionary<string, double>(_settings.AppLimitsGB, StringComparer.OrdinalIgnoreCase),
        StartWithWindows = _settings.StartWithWindows
    };

    public void Start()
    {
        if (_traceTask is not null)
        {
            return;
        }

        _traceTask = Task.Run(RunTraceSession);
    }

    public IReadOnlyList<UsageRecord> GetRecords(DateOnly date)
    {
        lock (_gate)
        {
            var records = date == _loadedDate ? _records : ReadDate(date);

            return records.Values
                .OrderByDescending(record => record.SentBytes + record.ReceivedBytes)
                .Select(record => new UsageRecord
                {
                    Application = record.Application,
                    ImagePath = record.ImagePath,
                    ProcessId = record.ProcessId,
                    Services = record.Services,
                    Adapter = record.Adapter,
                    SentBytes = record.SentBytes,
                    ReceivedBytes = record.ReceivedBytes,
                    Connections = record.Connections
                        .OrderByDescending(connection => connection.LastSeenUtc)
                        .Select(CloneConnection)
                        .ToList()
                })
                .Select(record =>
                {
                    if (_rates.TryGetValue(GetRecordKey(record.ImagePath, record.Adapter), out var rates))
                    {
                        (record.SentBytesPerSecond, record.ReceivedBytesPerSecond) = rates.GetRates(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    }

                    return record;
                })
                .ToArray();
        }
    }

    public IReadOnlyList<DateOnly> GetAvailableDates()
    {
        return Directory.EnumerateFiles(_dataDirectory, "usage-*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Select(name => name.StartsWith("usage-", StringComparison.Ordinal)
                && DateOnly.TryParseExact(name.Substring(6), "yyyy-MM-dd", out var date)
                    ? (DateOnly?)date
                    : null)
            .Where(date => date.HasValue)
            .Select(date => date!.Value)
            .OrderByDescending(date => date)
            .ToArray();
    }

    public void Flush()
    {
        lock (_gate)
        {
            SaveCurrentDate();
        }
    }

    public void SetBundleSettings(double monthlyLimitGB, int resetDay)
    {
        if (monthlyLimitGB < 0 || double.IsNaN(monthlyLimitGB) || double.IsInfinity(monthlyLimitGB))
        {
            throw new ArgumentOutOfRangeException(nameof(monthlyLimitGB));
        }

        if (resetDay is < 1 or > 28)
        {
            throw new ArgumentOutOfRangeException(nameof(resetDay));
        }

        lock (_gate)
        {
            _settings.MonthlyLimitGB = monthlyLimitGB;
            _settings.ResetDay = resetDay;
            SaveSettings();
        }
    }

    public void SetAppLimit(string imagePath, double? limitGB)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !Path.IsPathFullyQualified(imagePath))
        {
            throw new ArgumentException("Choose an app with a known executable path.", nameof(imagePath));
        }

        if (limitGB.HasValue
            && (limitGB.Value < 0 || double.IsNaN(limitGB.Value) || double.IsInfinity(limitGB.Value)))
        {
            throw new ArgumentOutOfRangeException(nameof(limitGB));
        }

        lock (_gate)
        {
            _settings.AppLimitsGB.Remove(imagePath);
            if (limitGB is > 0)
            {
                _settings.AppLimitsGB[imagePath] = limitGB.Value;
            }

            SaveSettings();
        }
    }

    public void SetStartWithWindows(bool enabled)
    {
        lock (_gate)
        {
            _settings.StartWithWindows = enabled;
            SaveSettings();
        }
    }

    public double GetAppUsageForCycle(string imagePath)
    {
        lock (_gate)
        {
            var (start, end) = GetCurrentCycle();
            return GetTotals(start, end).AppBytes(imagePath) / (1024d * 1024 * 1024);
        }
    }

    public IReadOnlyDictionary<string, double> GetAppLimits()
    {
        lock (_gate)
        {
            return new Dictionary<string, double>(_settings.AppLimitsGB, StringComparer.OrdinalIgnoreCase);
        }
    }

    public (DateOnly Start, DateOnly End) GetCurrentCycle()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var thisMonthReset = new DateOnly(today.Year, today.Month, _settings.ResetDay);
        var start = today >= thisMonthReset ? thisMonthReset : thisMonthReset.AddMonths(-1);
        return (start, start.AddMonths(1));
    }

    public BundleUsage GetBundleUsage()
    {
        lock (_gate)
        {
            var (start, end) = GetCurrentCycle();
            return new BundleUsage(start, end, GetTotals(start, end).TotalBytes, _settings.MonthlyLimitGB);
        }
    }

    public UsageTotals GetTotals(DateOnly start, DateOnly end)
    {
        lock (_gate)
        {
            long sent = 0;
            long received = 0;
            var appTotals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            for (var date = start; date < end; date = date.AddDays(1))
            {
                var records = date == _loadedDate ? _records : ReadDate(date);
                foreach (var record in records.Values)
                {
                    sent += record.SentBytes;
                    received += record.ReceivedBytes;
                    appTotals.TryGetValue(record.ImagePath, out var current);
                    appTotals[record.ImagePath] = current + record.SentBytes + record.ReceivedBytes;
                }
            }

            return new UsageTotals(sent, received, appTotals);
        }
    }

    public IReadOnlyList<DailyUsage> GetDailyUsage(DateOnly start, DateOnly end)
    {
        lock (_gate)
        {
            var usage = new List<DailyUsage>();
            for (var date = start; date < end; date = date.AddDays(1))
            {
                var records = date == _loadedDate ? _records : ReadDate(date);
                usage.Add(new DailyUsage(
                    date,
                    records.Values.Sum(record => record.SentBytes),
                    records.Values.Sum(record => record.ReceivedBytes)));
            }

            return usage;
        }
    }

    public IReadOnlyList<AdapterDiagnostics> GetAdapterDiagnostics()
    {
        lock (_gate)
        {
            var result = new List<AdapterDiagnostics>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    var statistics = adapter.GetIPStatistics();
                    var baseline = _adapterBaselines.GetValueOrDefault(adapter.Name);
                    var etw = _adapterEtwTotals.GetValueOrDefault(adapter.Name);
                    result.Add(new AdapterDiagnostics(
                        adapter.Name,
                        Math.Max(0, statistics.BytesSent - (baseline?.SentBytes ?? 0)),
                        Math.Max(0, statistics.BytesReceived - (baseline?.ReceivedBytes ?? 0)),
                        etw?.SentBytes ?? 0,
                        etw?.ReceivedBytes ?? 0));
                }
                catch (NetworkInformationException)
                {
                }
            }

            if (_adapterEtwTotals.TryGetValue("Unresolved", out var unresolved))
            {
                result.Add(new AdapterDiagnostics(
                    "Unresolved",
                    0,
                    0,
                    unresolved.SentBytes,
                    unresolved.ReceivedBytes));
            }

            return result.OrderByDescending(item => item.OsSentBytes + item.OsReceivedBytes).ToArray();
        }
    }

    public IReadOnlyList<UsageRecord> GetSystemDiagnostics(DateOnly date)
    {
        return GetRecords(date)
            .Where(record => record.ProcessId is 0 or 4
                || string.Equals(record.Application, "System", StringComparison.OrdinalIgnoreCase)
                || record.Adapter == "Unresolved")
            .ToArray();
    }

    public void ExportCsv(DateOnly start, DateOnly end, string filePath)
    {
        var rows = new List<string>
        {
            "Date,Application,Executable,PID,Adapter,SentBytes,ReceivedBytes,TotalBytes"
        };
        for (var date = start; date < end; date = date.AddDays(1))
        {
            foreach (var record in GetRecords(date))
            {
                rows.Add(string.Join(",",
                    date.ToString("yyyy-MM-dd"),
                    Csv(record.Application),
                    Csv(record.ImagePath),
                    record.ProcessId.ToString(),
                    Csv(record.Adapter),
                    record.SentBytes.ToString(),
                    record.ReceivedBytes.ToString(),
                    record.TotalBytes.ToString()));
            }
        }

        File.WriteAllText(filePath, string.Join(Environment.NewLine, rows), new UTF8Encoding(true));
    }

    private void RunTraceSession()
    {
        try
        {
            using var session = new TraceEventSession($"Monita-{Guid.NewGuid():N}");
            _session = session;
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            var kernel = session.Source.Kernel;
            kernel.TcpIpSend += data => Record(data.ProcessID, data.size, data.saddr, data.sport,
                data.daddr, data.dport, "TCP", true);
            kernel.TcpIpRecv += data => Record(data.ProcessID, data.size, data.daddr, data.dport,
                data.saddr, data.sport, "TCP", false);
            kernel.UdpIpSend += data => Record(data.ProcessID, data.size, data.saddr, data.sport,
                data.daddr, data.dport, "UDP", true);
            kernel.UdpIpRecv += data => Record(data.ProcessID, data.size, data.daddr, data.dport,
                data.saddr, data.sport, "UDP", false);
            kernel.TcpIpSendIPV6 += data => Record(data.ProcessID, data.size, data.saddr, data.sport,
                data.daddr, data.dport, "TCP", true);
            kernel.TcpIpRecvIPV6 += data => Record(data.ProcessID, data.size, data.daddr, data.dport,
                data.saddr, data.sport, "TCP", false);
            kernel.UdpIpSendIPV6 += data => Record(data.ProcessID, data.size, data.saddr, data.sport,
                data.daddr, data.dport, "UDP", true);
            kernel.UdpIpRecvIPV6 += data => Record(data.ProcessID, data.size, data.daddr, data.dport,
                data.saddr, data.sport, "UDP", false);

            Status = "Monitoring TCP/UDP traffic";
            session.Source.Process();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or ArgumentException)
        {
            Status = $"Network trace failed: {exception.Message}";
        }
        finally
        {
            _session = null;
        }
    }

    private void Record(int processId, int byteCount, IPAddress localAddress, int localPort,
        IPAddress remoteAddress, int remotePort, string protocol, bool isSent)
    {
        if (byteCount <= 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var cachedProcess = _processes.GetOrAdd(processId, _ => ResolveProcess(processId));
        if (cachedProcess.ExpiresAtUtc <= now)
        {
            cachedProcess = _processes.AddOrUpdate(
                processId,
                _ => ResolveProcess(processId),
                (_, existing) => existing.ExpiresAtUtc <= now ? ResolveProcess(processId) : existing);
        }

        var process = cachedProcess.Info;
        var addressesToAdapters = Volatile.Read(ref _addressesToAdapters);
        var adapter = ResolveAdapter(localAddress, addressesToAdapters);
        var services = _services.TryGetValue(processId, out var serviceNames)
            ? serviceNames
            : "";
        var key = GetRecordKey(process.ImagePath, adapter);
        var remote = remoteAddress.ToString();
        var local = localAddress.ToString();
        var timestamp = DateTime.UtcNow;

        lock (_gate)
        {
            if (_loadedDate != DateOnly.FromDateTime(DateTime.Now))
            {
                SaveCurrentDate();
                LoadDate(DateOnly.FromDateTime(DateTime.Now));
            }

            if (!_records.TryGetValue(key, out var record))
            {
                record = new UsageRecord
                {
                    Application = process.Name,
                    ImagePath = process.ImagePath,
                    ProcessId = processId,
                    Services = services,
                    Adapter = adapter
                };
                _records.Add(key, record);
            }
            else
            {
                record.ProcessId = processId;
                record.Services = services;
            }

            if (record.Connections is null)
            {
                record.Connections = new List<ConnectionUsage>();
            }

            var connection = record.Connections.FirstOrDefault(item =>
                item.Protocol == protocol
                && item.LocalPort == localPort
                && item.RemotePort == remotePort
                && item.LocalAddress == local
                && item.RemoteAddress == remote);
            if (connection is null)
            {
                connection = new ConnectionUsage
                {
                    Protocol = protocol,
                    LocalAddress = local,
                    LocalPort = localPort,
                    RemoteAddress = remote,
                    RemotePort = remotePort,
                    LastSeenUtc = timestamp
                };
                record.Connections.Add(connection);
                if (record.Connections.Count > 100)
                {
                    record.Connections.Remove(record.Connections.MinBy(item => item.LastSeenUtc)!);
                }
            }

            connection.LastSeenUtc = timestamp;
            if (isSent)
            {
                record.SentBytes += byteCount;
                connection.SentBytes += byteCount;
            }
            else
            {
                record.ReceivedBytes += byteCount;
                connection.ReceivedBytes += byteCount;
            }

            var rateKey = GetRecordKey(process.ImagePath, adapter);
            if (!_rates.TryGetValue(rateKey, out var rateWindow))
            {
                rateWindow = new RateWindow();
                _rates.Add(rateKey, rateWindow);
            }

            rateWindow.Add(timestamp, byteCount, isSent);

            if (!_adapterEtwTotals.TryGetValue(adapter, out var adapterCounter))
            {
                adapterCounter = new AdapterCounter();
                _adapterEtwTotals.Add(adapter, adapterCounter);
            }

            adapterCounter.Add(byteCount, isSent);
        }
    }

    private static CachedProcessInfo ResolveProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var name = process.ProcessName;
            string path;
            try
            {
                path = process.MainModule?.FileName ?? name;
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                or InvalidOperationException
                or NotSupportedException)
            {
                path = name;
            }

            return new CachedProcessInfo(new ProcessInfo(name, path), DateTime.UtcNow.AddSeconds(10));
        }
        catch (ArgumentException)
        {
            return new CachedProcessInfo(
                new ProcessInfo($"PID {processId}", $"PID:{processId}"),
                DateTime.UtcNow.AddSeconds(2));
        }
    }

    private static string ResolveAdapter(object? address, IReadOnlyDictionary<string, string?> addresses)
    {
        var ipAddress = ResolveAddress(address);
        if (ipAddress is null)
        {
            return "Unresolved";
        }

        foreach (var addressKey in GetAddressKeys(ipAddress))
        {
            if (addresses.TryGetValue(addressKey, out var adapterName))
            {
                return adapterName ?? "Unresolved";
            }
        }

        return "Unresolved";
    }

    private static IPAddress? ResolveAddress(object? address)
    {
        if (address is IPAddress ipAddress)
        {
            return NormalizeAddress(ipAddress);
        }

        if (address is string text && IPAddress.TryParse(text, out var parsed))
        {
            return NormalizeAddress(parsed);
        }

        if (address is int intAddress)
        {
            return new IPAddress(BitConverter.GetBytes(intAddress));
        }

        if (address is uint uintAddress)
        {
            return new IPAddress(BitConverter.GetBytes(uintAddress));
        }

        return null;
    }

    private static IPAddress NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static IEnumerable<string> GetAddressKeys(IPAddress address)
    {
        var normalizedAddress = NormalizeAddress(address);
        yield return normalizedAddress.ToString();

        if (normalizedAddress.AddressFamily == AddressFamily.InterNetworkV6
            && normalizedAddress.ScopeId != 0)
        {
            yield return new IPAddress(normalizedAddress.GetAddressBytes()).ToString();
        }
    }

    private static void AddAdapterAddress(
        Dictionary<string, string?> addresses,
        string address,
        string adapterName)
    {
        if (!addresses.TryGetValue(address, out var existingAdapter))
        {
            addresses.Add(address, adapterName);
        }
        else if (existingAdapter is not null
            && !string.Equals(existingAdapter, adapterName, StringComparison.OrdinalIgnoreCase))
        {
            addresses[address] = null;
        }
    }

    private async Task RefreshServiceMapLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var servicesByPid = new Dictionary<int, List<string>>();
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, ProcessId FROM Win32_Service WHERE ProcessId > 0");
                using var results = searcher.Get();
                foreach (ManagementObject item in results)
                {
                    var pid = Convert.ToInt32(item["ProcessId"]);
                    var name = Convert.ToString(item["Name"]);
                    if (pid <= 0 || string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    if (!servicesByPid.TryGetValue(pid, out var names))
                    {
                        names = new List<string>();
                        servicesByPid.Add(pid, names);
                    }

                    names.Add(name);
                }

                _services.Clear();
                foreach (var pair in servicesByPid)
                {
                    _services[pair.Key] = string.Join(", ", pair.Value.OrderBy(name => name));
                }
            }
            catch (ManagementException exception)
            {
                Status = $"Service lookup failed: {exception.Message}";
            }

            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshAdaptersLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var addresses = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                    {
                        foreach (var addressKey in GetAddressKeys(address.Address))
                        {
                            AddAdapterAddress(addresses, addressKey, adapter.Name);
                        }
                    }
                }
                catch (NetworkInformationException)
                {
                    // Skip adapters that disappear while the list is being refreshed.
                }
            }

            Volatile.Write(ref _addressesToAdapters, addresses);

            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        }
    }

    private void LoadDate(DateOnly date)
    {
        _loadedDate = date;
        _records = ReadDate(date);
    }

    private Dictionary<string, UsageRecord> ReadDate(DateOnly date)
    {
        var path = GetDataPath(date);
        if (!File.Exists(path))
        {
            return new Dictionary<string, UsageRecord>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var content = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(content))
            {
                return new Dictionary<string, UsageRecord>(StringComparer.OrdinalIgnoreCase);
            }

            var records = JsonSerializer.Deserialize<Dictionary<string, UsageRecord>>(content)
                ?? new Dictionary<string, UsageRecord>(StringComparer.OrdinalIgnoreCase);

            foreach (var record in records.Values)
            {
                record.Connections ??= new List<ConnectionUsage>();
            }

            return records;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            QuarantineCorruptedFile(path);
            return new Dictionary<string, UsageRecord>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void LoadSettings()
    {
        if (File.Exists(_settingsPath))
        {
            try
            {
                var content = File.ReadAllText(_settingsPath);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    _settings = JsonSerializer.Deserialize<BundleSettings>(content) ?? new BundleSettings();
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                QuarantineCorruptedFile(_settingsPath);
                _settings = new BundleSettings();
            }
        }

        _settings.AppLimitsGB = new Dictionary<string, double>(
            _settings.AppLimitsGB ?? new Dictionary<string, double>(),
            StringComparer.OrdinalIgnoreCase);
        if (_settings.ResetDay is < 1 or > 28
            || _settings.MonthlyLimitGB < 0
            || double.IsNaN(_settings.MonthlyLimitGB)
            || double.IsInfinity(_settings.MonthlyLimitGB)
            || _settings.AppLimitsGB.Any(pair => !Path.IsPathFullyQualified(pair.Key)
                || pair.Value <= 0
                || double.IsNaN(pair.Value)
                || double.IsInfinity(pair.Value)))
        {
            _settings = new BundleSettings();
        }
    }

    private void SaveSettings()
    {
        AtomicWriteJson(_settingsPath, _settings);
    }

    private void SaveCurrentDate()
    {
        var path = GetDataPath(_loadedDate);
        AtomicWriteJson(path, _records);
    }

    private static void AtomicWriteJson<T>(string destinationPath, T data)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{destinationPath}.tmp";
        var json = JsonSerializer.Serialize(data);
        using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, Encoding.UTF8))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, destinationPath, overwrite: true);
    }

    private static void QuarantineCorruptedFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var corruptPath = $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
                File.Move(path, corruptPath, overwrite: true);
            }
        }
        catch
        {
        }
    }

    private string GetDataPath(DateOnly date) =>
        Path.Combine(_dataDirectory, $"usage-{date:yyyy-MM-dd}.json");

    private void CaptureAdapterBaselines()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var statistics = adapter.GetIPStatistics();
                _adapterBaselines[adapter.Name] = new AdapterCounter(statistics.BytesSent, statistics.BytesReceived);
            }
            catch (NetworkInformationException)
            {
            }
        }
    }

    private static string GetRecordKey(string imagePath, string adapter) => $"{imagePath}|{adapter}";

    private static ConnectionUsage CloneConnection(ConnectionUsage connection) => new()
    {
        Protocol = connection.Protocol,
        LocalAddress = connection.LocalAddress,
        LocalPort = connection.LocalPort,
        RemoteAddress = connection.RemoteAddress,
        RemotePort = connection.RemotePort,
        SentBytes = connection.SentBytes,
        ReceivedBytes = connection.ReceivedBytes,
        LastSeenUtc = connection.LastSeenUtc
    };

    private static string Csv(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        _session?.Stop();
        WaitForShutdown(_traceTask);
        WaitForShutdown(_serviceRefreshTask);
        WaitForShutdown(_adapterRefreshTask);
        Flush();
        _cancellation.Dispose();
    }

    private static void WaitForShutdown(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            task.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException exception) when (exception.InnerExceptions.All(
            inner => inner is OperationCanceledException))
        {
        }
    }

    private sealed record ProcessInfo(string Name, string ImagePath);
    private sealed record CachedProcessInfo(ProcessInfo Info, DateTime ExpiresAtUtc);

    private sealed class AdapterCounter
    {
        public AdapterCounter()
        {
        }

        public AdapterCounter(long sentBytes, long receivedBytes)
        {
            SentBytes = sentBytes;
            ReceivedBytes = receivedBytes;
        }

        public long SentBytes { get; private set; }
        public long ReceivedBytes { get; private set; }

        public void Add(long byteCount, bool isSent)
        {
            if (isSent)
            {
                SentBytes += byteCount;
            }
            else
            {
                ReceivedBytes += byteCount;
            }
        }
    }

    private sealed class RateWindow
    {
        private readonly Queue<RateBucket> _buckets = new();

        public void Add(DateTime timestampUtc, long bytes, bool isSent)
        {
            var second = new DateTimeOffset(timestampUtc).ToUnixTimeSeconds();
            if (_buckets.Count == 0 || _buckets.Last().Second != second)
            {
                _buckets.Enqueue(new RateBucket(second));
            }

            var bucket = _buckets.Last();
            bucket.Add(bytes, isSent);
            Prune(second);
        }

        public (double Sent, double Received) GetRates(long currentSecond)
        {
            Prune(currentSecond);
            if (_buckets.Count == 0)
            {
                return (0, 0);
            }

            var seconds = Math.Clamp(currentSecond - _buckets.Peek().Second + 1, 1, 5);
            var sent = _buckets.Sum(bucket => bucket.SentBytes);
            var received = _buckets.Sum(bucket => bucket.ReceivedBytes);
            return (sent / (double)seconds, received / (double)seconds);
        }

        private void Prune(long currentSecond)
        {
            while (_buckets.Count > 0 && _buckets.Peek().Second < currentSecond - 4)
            {
                _buckets.Dequeue();
            }
        }
    }

    private sealed class RateBucket(long second)
    {
        public long Second { get; } = second;
        public long SentBytes { get; private set; }
        public long ReceivedBytes { get; private set; }

        public void Add(long bytes, bool isSent)
        {
            if (isSent)
            {
                SentBytes += bytes;
            }
            else
            {
                ReceivedBytes += bytes;
            }
        }
    }
}
