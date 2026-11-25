using System.Text.Json.Serialization;

namespace TrafficMonitor;

internal sealed class UsageRecord
{
    public string Application { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public int ProcessId { get; set; }
    public string Services { get; set; } = "";
    public string Adapter { get; set; } = "";
    public long SentBytes { get; set; }
    public long ReceivedBytes { get; set; }
    public List<ConnectionUsage> Connections { get; set; } = new();
    [JsonIgnore]
    public double SentBytesPerSecond { get; set; }
    [JsonIgnore]
    public double ReceivedBytesPerSecond { get; set; }

    [JsonIgnore]
    public long TotalBytes => SentBytes + ReceivedBytes;
}

internal sealed class ConnectionUsage
{
    public string Protocol { get; set; } = "";
    public string LocalAddress { get; set; } = "";
    public int LocalPort { get; set; }
    public string RemoteAddress { get; set; } = "";
    public int RemotePort { get; set; }
    public long SentBytes { get; set; }
    public long ReceivedBytes { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

internal sealed record DailyUsage(DateOnly Date, long SentBytes, long ReceivedBytes)
{
    public long TotalBytes => SentBytes + ReceivedBytes;
}

internal sealed record UsageTotals(long SentBytes, long ReceivedBytes, IReadOnlyDictionary<string, long> AppTotals)
{
    public long TotalBytes => SentBytes + ReceivedBytes;

    public long AppBytes(string imagePath) =>
        AppTotals.TryGetValue(imagePath, out var bytes) ? bytes : 0;
}
