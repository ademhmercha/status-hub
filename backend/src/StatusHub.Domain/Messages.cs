namespace StatusHub.Domain;

/// <summary>Scheduler -> Checker : "check this monitor for this minute". Idempotency key = MonitorId + Minute.</summary>
public sealed record CheckMessage(string MonitorId, DateTimeOffset Minute)
{
    public string IdempotencyKey => $"{MonitorId}#{Minute:yyyyMMddHHmm}";
}

/// <summary>Checker -> Evaluator.</summary>
public sealed record CheckResultMessage(string MonitorId, DateTimeOffset Minute, bool Success, int? StatusCode, int LatencyMs, string? Error);

/// <summary>Evaluator -> Notifier.</summary>
public sealed record IncidentEvent(string MonitorId, string IncidentId, IncidentEventType Type, DateTimeOffset At, string? Cause);

public enum IncidentEventType { Opened, Resolved }
