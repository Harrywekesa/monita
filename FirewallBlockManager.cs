using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Diagnostics;
using System.Text;

namespace TrafficMonitor;

internal sealed class FirewallBlockManager
{
    private const string RulePrefix = "Monita.TempBlock.";
    private const string CleanupTaskPrefix = "MonitaFirewallCleanup.";
    private readonly string _blocksPath;
    private readonly List<FirewallBlock> _blocks;
    private readonly ProcessController _processController;

    public FirewallBlockManager(string dataDirectory, ProcessController processController)
    {
        _blocksPath = Path.Combine(dataDirectory, "network-blocks.json");
        _processController = processController;
        if (File.Exists(_blocksPath))
        {
            try
            {
                var content = File.ReadAllText(_blocksPath);
                _blocks = !string.IsNullOrWhiteSpace(content)
                    ? JsonSerializer.Deserialize<List<FirewallBlock>>(content) ?? new()
                    : new();
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                try
                {
                    File.Move(_blocksPath, $"{_blocksPath}.corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
                }
                catch
                {
                }
                _blocks = new();
            }
        }
        else
        {
            _blocks = new();
        }
    }

    public IReadOnlyList<FirewallBlock> GetActiveBlocks()
    {
        RemoveExpiredBlocks();
        return _blocks.ToArray();
    }

    public FirewallBlock Block(UsageRecord record, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        if (_blocks.Any(block => string.Equals(block.ImagePath, record.ImagePath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Network access is already blocked for this app.");
        }

        var imagePath = _processController.GetVerifiedExecutablePath(record);
        var id = Guid.NewGuid().ToString("N");
        var expiresAt = DateTime.Now.Add(duration);
        AddRules(id, imagePath, record.Application);
        try
        {
            CreateExpiryTask(id, expiresAt);
        }
        catch
        {
            RemoveRules(id);
            throw;
        }

        var block = new FirewallBlock(id, record.Application, imagePath, DateTime.Now, expiresAt);
        _blocks.Add(block);
        try
        {
            Save();
        }
        catch
        {
            _blocks.Remove(block);
            RemoveRules(id);
            DeleteExpiryTask(id);
            throw;
        }

        return block;
    }

    public void Unblock(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
        {
            throw new ArgumentException("Invalid temporary block identifier.", nameof(id));
        }

        RemoveRules(id);
        DeleteExpiryTask(id);
        _blocks.RemoveAll(block => string.Equals(block.Id, id, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public static void RemoveBlockFromExpiryTask(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
        {
            throw new ArgumentException("Invalid temporary block identifier.", nameof(id));
        }

        RemoveRules(id);
        DeleteExpiryTask(id);
    }

    private void RemoveExpiredBlocks()
    {
        var expired = _blocks.Where(block => block.ExpiresAt <= DateTime.Now).ToArray();
        foreach (var block in expired)
        {
            Unblock(block.Id);
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_blocksPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{_blocksPath}.tmp";
        var json = JsonSerializer.Serialize(_blocks);
        using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, Encoding.UTF8))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, _blocksPath, overwrite: true);
    }

    private static void AddRules(string id, string imagePath, string application)
    {
        var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
            ?? throw new InvalidOperationException("Windows Firewall policy is unavailable.");
        var ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule")
            ?? throw new InvalidOperationException("Windows Firewall rules are unavailable.");
        dynamic policy = Activator.CreateInstance(policyType)
            ?? throw new InvalidOperationException("Could not access Windows Firewall.");
        dynamic rules = policy.Rules;
        try
        {
            AddRule(rules, ruleType, $"{RulePrefix}{id}.Outbound", imagePath, application, 2);
            try
            {
                AddRule(rules, ruleType, $"{RulePrefix}{id}.Inbound", imagePath, application, 1);
            }
            catch
            {
                RemoveRules(id);
                throw;
            }
        }
        finally
        {
            ReleaseComObject(rules);
            ReleaseComObject(policy);
        }
    }

    private static void AddRule(dynamic rules, Type ruleType, string name, string imagePath,
        string application, int direction)
    {
        dynamic rule = Activator.CreateInstance(ruleType)
            ?? throw new InvalidOperationException("Could not create a Windows Firewall rule.");
        try
        {
            rule.Name = name;
            rule.Description = $"Temporary network pause for {application}.";
            rule.ApplicationName = imagePath;
            rule.Action = 0;
            rule.Direction = direction;
            rule.Enabled = true;
            rule.InterfaceTypes = "All";
            rule.Profiles = 0x7fffffff;
            rule.Protocol = 256;
            rules.Add(rule);
        }
        finally
        {
            ReleaseComObject(rule);
        }
    }

    private static void RemoveRules(string id)
    {
        var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
        if (policyType is null)
        {
            return;
        }

        dynamic? policy = null;
        dynamic? rules = null;
        try
        {
            policy = Activator.CreateInstance(policyType);
            if (policy is null)
            {
                return;
            }

            rules = policy.Rules;
            RemoveRuleIfPresent(rules, $"{RulePrefix}{id}.Outbound");
            RemoveRuleIfPresent(rules, $"{RulePrefix}{id}.Inbound");
        }
        finally
        {
            ReleaseComObject(rules);
            ReleaseComObject(policy);
        }
    }

    private static void RemoveRuleIfPresent(dynamic rules, string name)
    {
        try
        {
            rules.Remove(name);
        }
        catch (COMException exception) when (exception.ErrorCode is unchecked((int)0x80070002)
            or unchecked((int)0x80070490))
        {
        }
    }

    private static void CreateExpiryTask(string id, DateTime expiresAt)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the app executable path.");
        var taskCommand = $"\"{executable}\" --expire-firewall-block {id}";
        RunSchtasks(new[]
        {
            "/Create", "/SC", "ONCE", "/SD", expiresAt.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
            "/ST", expiresAt.ToString("HH:mm", CultureInfo.InvariantCulture),
            "/TN", $"{CleanupTaskPrefix}{id}", "/TR", taskCommand, "/RL", "HIGHEST", "/F"
        });
    }

    private static void DeleteExpiryTask(string id)
    {
        var taskName = $"{CleanupTaskPrefix}{id}";
        var query = RunSchtasks(new[] { "/Query", "/TN", taskName }, allowFailure: true);
        if (query.ExitCode == 0)
        {
            RunSchtasks(new[] { "/Delete", "/TN", taskName, "/F" });
        }
    }

    private static ProcessResult RunSchtasks(IEnumerable<string> arguments, bool allowFailure = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start Task Scheduler command.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var result = new ProcessResult(process.ExitCode, outputTask.GetAwaiter().GetResult(),
            errorTask.GetAwaiter().GetResult());
        if (!allowFailure && result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
        }

        return result;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}

internal sealed record FirewallBlock(
    string Id,
    string Application,
    string ImagePath,
    DateTime StartedAt,
    DateTime ExpiresAt)
{
    public TimeSpan TimeRemaining => ExpiresAt - DateTime.Now;
}
