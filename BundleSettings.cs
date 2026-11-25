namespace TrafficMonitor;

internal sealed class BundleSettings
{
    public double MonthlyLimitGB { get; set; }
    public int ResetDay { get; set; } = 1;
    public Dictionary<string, double> AppLimitsGB { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public bool StartWithWindows { get; set; }
}

internal sealed record BundleUsage(DateOnly PeriodStart, DateOnly PeriodEnd, long Bytes, double MonthlyLimitGB);
internal sealed record AdapterDiagnostics(
    string Name,
    long OsSentBytes,
    long OsReceivedBytes,
    long EtwSentBytes,
    long EtwReceivedBytes);
