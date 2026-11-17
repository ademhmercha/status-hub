namespace StatusHub.Domain;

/// <summary>Scheduler -> Checker. Carries the monitor config so the Checker needs no database access.
/// Idempotency key = MonitorId + Minute.</summary>
public sealed record CheckMessage(
    string AccountId,
    string MonitorId,
    DateTimeOffset Minute,
    string Url,
    string Method,
    int ExpectedStatus,
    string? Keyword,
    int TimeoutSeconds)
{
    public string IdempotencyKey => $"{MonitorId}#{Minute.UtcDateTime:yyyyMMddHHmm}";
}

/// <summary>Checker -> Evaluator.</summary>
public sealed record CheckResultMessage(
    string AccountId,
    string MonitorId,
    DateTimeOffset Minute,
    DateTimeOffset CheckedAt,
    bool Success,
    int? StatusCode,
    int LatencyMs,
    ErrorKind Error,
    string? ErrorMessage);

public enum IncidentEventType { Opened, Resolved }

/// <summary>Evaluator -> Notifier (IncidentOpened / IncidentResolved).</summary>
public sealed record IncidentEvent(
    string AccountId,
    string MonitorId,
    string MonitorName,
    string MonitorUrl,
    string IncidentId,
    IncidentEventType Type,
    DateTimeOffset At,
    string? Cause,
    TimeSpan? Duration);
