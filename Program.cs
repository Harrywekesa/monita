namespace TrafficMonitor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && string.Equals(args[0], "--expire-firewall-block", StringComparison.Ordinal))
        {
            try
            {
                FirewallBlockManager.RemoveBlockFromExpiryTask(args[1]);
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, "Monita network-block cleanup failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.ExitCode = 1;
            }

            return;
        }

        using var instanceMutex = new Mutex(initiallyOwned: true,
            name: "Local\\MonitaTrafficMonitorSingleInstance", out bool createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message, "Monita - Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        Application.Run(new MainForm(args.Length == 1
            && string.Equals(args[0], "--start-hidden", StringComparison.Ordinal)));
    }
}
