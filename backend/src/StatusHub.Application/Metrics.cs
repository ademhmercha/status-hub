namespace StatusHub.Application;

/// <summary>Business metrics (CloudWatch EMF in AWS, no-op locally). Names are the contract with the dashboard and alarms.</summary>
public interface IStatusMetrics
{
    /// <summary>A counter (unit Count).</summary>
    void Count(string name, double value = 1);

    /// <summary>A duration in milliseconds.</summary>
    void Milliseconds(string name, double value);

    /// <summary>A duration in seconds.</summary>
    void Seconds(string name, double value);
}

public sealed class NullStatusMetrics : IStatusMetrics
{
    public void Count(string name, double value = 1) { }
    public void Milliseconds(string name, double value) { }
    public void Seconds(string name, double value) { }
}

public static class MetricNames
{
    // Scheduler
    public const string ChecksScheduled = "ChecksScheduled";
    // Checker
    public const string ChecksExecuted = "ChecksExecuted";
    public const string ChecksOnTime = "ChecksOnTime";
    public const string ChecksLate = "ChecksLate";
    public const string ChecksFailed = "ChecksFailed";
    public const string CheckLatencyMs = "CheckLatencyMs";
    public const string CheckDelaySeconds = "CheckDelaySeconds";
    // Evaluator
    public const string ResultsProcessed = "ResultsProcessed";
    public const string IncidentsOpened = "IncidentsOpened";
    public const string IncidentsResolved = "IncidentsResolved";
    // Notifier
    public const string AlertsSent = "AlertsSent";
    public const string AlertsFailed = "AlertsFailed";

    /// <summary>SLO: a check is on time when it ran within this delay of its scheduled minute.</summary>
    public static readonly TimeSpan OnTimeThreshold = TimeSpan.FromSeconds(60);
}
