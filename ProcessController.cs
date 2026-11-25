using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Management;
using Microsoft.Win32.SafeHandles;

namespace TrafficMonitor;

internal sealed class ProcessController : IDisposable
{
    private const uint ProcessSuspendResume = 0x0800;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private readonly Dictionary<int, SuspendedProcess> _suspended = new();
    private readonly string _windowsDirectory = Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));

    public bool CanControl(UsageRecord? record, DateOnly selectedDate)
    {
        if (record is null
            || selectedDate != DateOnly.FromDateTime(DateTime.Now)
            || record.ProcessId <= 0
            || !string.IsNullOrWhiteSpace(record.Services)
            || !IsAllowedProcess(record))
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(record.ProcessId);
            var actualPath = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(actualPath)
                && PathsEqual(actualPath, record.ImagePath)
                && IsAllowedProcess(record, process.ProcessName, actualPath);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or Win32Exception
            or NotSupportedException)
        {
            return false;
        }
    }

    public bool IsSuspended(int processId) => _suspended.ContainsKey(processId);

    public string GetVerifiedExecutablePath(UsageRecord record)
    {
        if (!CanControl(record, DateOnly.FromDateTime(DateTime.Now)))
        {
            throw new InvalidOperationException("Only a current-day, non-system app without hosted services can be managed.");
        }

        using var process = Process.GetProcessById(record.ProcessId);
        var imagePath = process.MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(imagePath)
            || !PathsEqual(imagePath, record.ImagePath)
            || IsHostingWindowsService(record.ProcessId))
        {
            throw new InvalidOperationException("The selected process is protected or no longer matches this row.");
        }

        return imagePath;
    }

    public void Pause(UsageRecord record)
    {
        var (process, handle) = OpenVerifiedProcess(record);
        process.Dispose();

        try
        {
            ThrowOnNtStatus(NtSuspendProcess(handle), "pause");
            _suspended.Add(record.ProcessId, new SuspendedProcess(handle, record.ImagePath));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Resume(UsageRecord record)
    {
        if (!_suspended.TryGetValue(record.ProcessId, out var suspended))
        {
            throw new InvalidOperationException("This app did not pause the selected process.");
        }

        if (!string.Equals(suspended.ImagePath, record.ImagePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The selected process no longer matches the paused process.");
        }

        ThrowOnNtStatus(NtResumeProcess(suspended.Handle), "resume");
        _suspended.Remove(record.ProcessId);
        suspended.Handle.Dispose();
    }

    public void End(UsageRecord record)
    {
        var (process, handle) = OpenVerifiedProcess(record);
        try
        {
            using (handle)
            {
                if (_suspended.TryGetValue(record.ProcessId, out var suspended))
                {
                    ThrowOnNtStatus(NtResumeProcess(suspended.Handle), "resume before closing");
                    _suspended.Remove(record.ProcessId);
                    suspended.Handle.Dispose();
                }

                process.Kill();
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private (Process Process, SafeProcessHandle Handle) OpenVerifiedProcess(UsageRecord record)
    {
        if (!CanControl(record, DateOnly.FromDateTime(DateTime.Now)))
        {
            throw new InvalidOperationException("Only a current-day, non-system app without hosted services can be controlled.");
        }

        Process? process = null;
        SafeProcessHandle? handle = null;
        try
        {
            process = Process.GetProcessById(record.ProcessId);
            var actualPath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(actualPath)
                || !PathsEqual(actualPath, record.ImagePath)
                || !IsAllowedProcess(record, process.ProcessName, actualPath))
            {
                throw new InvalidOperationException("The process has exited or no longer matches this row.");
            }

            if (IsHostingWindowsService(record.ProcessId))
            {
                throw new InvalidOperationException("This process currently hosts a Windows service and cannot be managed here.");
            }

            handle = OpenProcess(
                ProcessSuspendResume | ProcessQueryLimitedInformation,
                false,
                record.ProcessId);
            if (handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the selected process.");
            }

            var verifiedHandle = handle;
            handle = null;
            var verifiedProcess = process;
            process = null;
            return (verifiedProcess, verifiedHandle);
        }
        finally
        {
            process?.Dispose();
            handle?.Dispose();
        }
    }

    private bool IsAllowedProcess(UsageRecord record)
    {
        return IsAllowedProcess(record, record.Application, record.ImagePath);
    }

    private bool IsAllowedProcess(UsageRecord record, string processName, string imagePath)
    {
        if (record.ProcessId is 0 or 4
            || record.ProcessId == Environment.ProcessId
            || ProtectedNames.Contains(Path.GetFileNameWithoutExtension(processName)))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(imagePath)
            || !Path.IsPathFullyQualified(imagePath)
            || imagePath.StartsWith("PID:", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(imagePath))
        {
            return false;
        }

        var normalizedPath = Path.GetFullPath(imagePath);
        return !normalizedPath.StartsWith(_windowsDirectory + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalizedPath, _windowsDirectory, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalizedPath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsHostingWindowsService(int processId)
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT Name FROM Win32_Service WHERE ProcessId = {processId}");
        using var services = searcher.Get();
        return services.Count > 0;
    }

    private static void ThrowOnNtStatus(int status, string action)
    {
        if (status != 0)
        {
            throw new InvalidOperationException(
                $"Could not {action} the process (Windows status 0x{unchecked((uint)status):X8}).");
        }
    }

    public void Dispose()
    {
        List<string> failures = new();
        foreach (var suspended in _suspended.Values)
        {
            try
            {
                var status = NtResumeProcess(suspended.Handle);
                if (status != 0)
                {
                    failures.Add($"Could not resume a paused app (Windows status 0x{unchecked((uint)status):X8}).");
                }
            }
            catch (Exception exception)
            {
                failures.Add($"Could not resume a paused app: {exception.Message}");
            }
            finally
            {
                suspended.Handle.Dispose();
            }
        }

        _suspended.Clear();
        if (failures.Count > 0)
        {
            throw new AggregateException(failures.Select(message => new InvalidOperationException(message)));
        }
    }

    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "services",
        "lsass", "winlogon", "svchost", "dwm", "fontdrvhost"
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(SafeProcessHandle processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(SafeProcessHandle processHandle);

    private sealed record SuspendedProcess(SafeProcessHandle Handle, string ImagePath);
}
