namespace StatusHub.Domain;

public enum MonitorStatus { Unknown, Up, Down }

public enum ErrorKind { None, Dns, Timeout, Tls, Connection, UnexpectedStatus, KeywordMissing, Other }

public enum AlertType { Email, Webhook }

public sealed record Account(string UserId, string Email, string Slug, DateTimeOffset CreatedAt);

public sealed record UptimeMonitor(
    string AccountId,
    string MonitorId,
    string Name,
    string Url,
    string Method,
    int IntervalMinutes,
    int ExpectedStatus,
    string? Keyword,
    int TimeoutSeconds,
    bool Active,
    DateTimeOffset CreatedAt,
    MonitorStatus Status = MonitorStatus.Unknown,
    int ConsecutiveFailures = 0,
    int ConsecutiveSuccesses = 0,
    string? OpenIncidentId = null,
    DateTimeOffset? LastCheckedAt = null)
{
    public const int MinInterval = 1;
    public const int MaxInterval = 5;
    public const int MaxPerAccount = 20;

    /// <summary>Stable (non-randomized) offset used to spread monitors of the same interval across minutes.</summary>
    public int ScheduleOffset
    {
        get
        {
            var sum = 0;
            foreach (var c in MonitorId) sum = (sum * 31 + c) & 0x7fffffff;
            return sum % IntervalMinutes;
        }
    }

    /// <summary>Partition value of the "due" index: monitors with the same interval and offset are due on the same minutes.</summary>
    public string DueKey => DueKeyFor(IntervalMinutes, ScheduleOffset);

    public static string DueKeyFor(int interval, int offset) => $"DUE#{interval}#{offset}";

    /// <summary>All due partitions for a given minute (one per interval 1..5).</summary>
    public static IEnumerable<string> DueKeysAt(DateTimeOffset minute)
    {
        var epochMinute = minute.ToUnixTimeSeconds() / 60;
        for (var i = MinInterval; i <= MaxInterval; i++)
            yield return DueKeyFor(i, (int)(epochMinute % i));
    }
}

public sealed record CheckResult(
    string MonitorId,
    DateTimeOffset Timestamp,
    bool Success,
    int? StatusCode,
    int LatencyMs,
    ErrorKind Error,
    string? ErrorMessage);

public sealed record Incident(
    string AccountId,
    string MonitorId,
    string IncidentId,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ResolvedAt,
    string Cause)
{
    public bool IsOpen => ResolvedAt is null;
    public TimeSpan? Duration => ResolvedAt - OpenedAt;

    public static string NewId(DateTimeOffset at) => $"{at:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
}

public sealed record AlertSubscription(
    string AccountId,
    string SubscriptionId,
    AlertType Type,
    string Target,
    string? Secret,
    bool Active);

/// <summary>Per monitor, per UTC day. Percentiles come from a fixed-bucket latency histogram so they can be updated atomically.</summary>
public sealed record DailyAggregate(
    string MonitorId,
    DateOnly Date,
    int Total,
    int Successes,
    long SumLatencyMs,
    int[] Histogram)
{
    public static readonly int[] BucketUpperBoundsMs = [50, 100, 200, 300, 500, 750, 1000, 1500, 2000, 3000, 5000, 10000, int.MaxValue];
    public static int BucketCount => BucketUpperBoundsMs.Length;

    public static int BucketFor(int latencyMs)
    {
        for (var i = 0; i < BucketUpperBoundsMs.Length; i++)
            if (latencyMs <= BucketUpperBoundsMs[i]) return i;
        return BucketUpperBoundsMs.Length - 1;
    }

    public double Availability => Total == 0 ? 100.0 : 100.0 * Successes / Total;

    /// <summary>Upper bound of the bucket containing percentile p (0-1). 0 when empty.</summary>
    public int Percentile(double p)
    {
        var count = Histogram.Sum();
        if (count == 0) return 0;
        var target = (long)Math.Ceiling(p * count);
        long acc = 0;
        for (var i = 0; i < Histogram.Length; i++)
        {
            acc += Histogram[i];
            if (acc >= target) return i == Histogram.Length - 1 ? BucketUpperBoundsMs[^2] : BucketUpperBoundsMs[i];
        }
        return BucketUpperBoundsMs[^2];
    }

    public int P50 => Percentile(0.5);
    public int P95 => Percentile(0.95);

    public static DailyAggregate Empty(string monitorId, DateOnly date) =>
        new(monitorId, date, 0, 0, 0, new int[BucketCount]);
}
