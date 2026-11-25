using System.Diagnostics;

namespace TrafficMonitor;

internal static class StartupTaskManager
{
    private const string TaskName = "MonitaTrafficMonitor";

    public static void SetEnabled(bool enabled)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the app executable path.");

        if (enabled)
        {
            RunSchtasks(new[]
            {
                "/Create", "/SC", "ONLOGON", "/TN", TaskName,
                "/TR", $"\"{executable}\" --start-hidden", "/RL", "HIGHEST", "/F"
            });
        }
        else
        {
            var query = RunSchtasks(new[] { "/Query", "/TN", TaskName }, allowFailure: true);
            if (query.ExitCode == 0)
            {
                RunSchtasks(new[] { "/Delete", "/TN", TaskName, "/F" });
            }
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
                string.IsNullOrWhiteSpace(result.Error)
                    ? result.Output
                    : result.Error);
        }

        return result;
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
