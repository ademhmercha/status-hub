using System.Diagnostics;
using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StatusHub.Domain;

namespace StatusHub.Application;

public sealed class SchedulerStage(
    IMonitorRepository monitors,
    IIdempotencyStore idempotency,
    IMessagePublisher<CheckMessage> checks,
    IStatusMetrics metrics,
    ILogger<SchedulerStage> log) : IStage<SchedulerTick>
{
    public static readonly TimeSpan KeyTtl = TimeSpan.FromHours(1);

    public async Task HandleAsync(SchedulerTick tick, CancellationToken ct)
    {
        var due = await monitors.GetDueAsync(tick.Minute, ct);
        var failures = new List<Exception>();
        var published = 0;
        foreach (var chunk in due.Chunk(25))
        {
            var results = await Task.WhenAll(chunk.Select(m => ScheduleAsync(m, tick.Minute, ct)));
            published += results.Count(r => r.Published);
            failures.AddRange(results.Where(r => r.Error is not null).Select(r => r.Error!));
        }
        // Denominator of the SLO: every check that must run this minute (published now, or already published by an earlier invocation).
        metrics.Count(MetricNames.ChecksScheduled, due.Count - failures.Count);
        log.LogInformation("Scheduled {Published} of {Due} due monitors for {Minute}", published, due.Count, tick.Minute);
        if (failures.Count > 0) throw new AggregateException("Some checks could not be scheduled.", failures);
    }

    private async Task<(bool Published, Exception? Error)> ScheduleAsync(UptimeMonitor m, DateTimeOffset minute, CancellationToken ct)
    {
        var msg = new CheckMessage(m.AccountId, m.MonitorId, minute, m.Url, m.Method, m.ExpectedStatus, m.Keyword, m.TimeoutSeconds);
        // Idempotent: the same monitor + minute is published at most once, even if the Scheduler is invoked twice.
        if (!await idempotency.TryAcquireAsync(msg.IdempotencyKey, KeyTtl, ct)) return (false, null);
        try
        {
            await checks.PublishAsync(msg, ct);
            return (true, null);
        }
        catch (Exception ex)
        {
            await idempotency.ReleaseAsync(msg.IdempotencyKey, CancellationToken.None); // allow a retry
            log.LogError(ex, "Failed to publish check for {MonitorId}", m.MonitorId);
            return (false, ex);
        }
    }
}

public sealed record CheckerOptions(bool AllowPrivateTargets = false, int MaxBodyBytes = 1_000_000);

public sealed class CheckerStage(
    HttpClient http,
    IMessagePublisher<CheckResultMessage> results,
    TimeProvider clock,
    CheckerOptions options,
    IStatusMetrics metrics,
    ILogger<CheckerStage> log) : IStage<CheckMessage>
{
    public async Task HandleAsync(CheckMessage message, CancellationToken ct)
    {
        var result = await CheckAsync(message, ct);

        var delay = result.CheckedAt - message.Minute;
        metrics.Count(MetricNames.ChecksExecuted);
        metrics.Count(delay <= MetricNames.OnTimeThreshold ? MetricNames.ChecksOnTime : MetricNames.ChecksLate);
        metrics.Seconds(MetricNames.CheckDelaySeconds, Math.Max(0, delay.TotalSeconds));
        metrics.Milliseconds(MetricNames.CheckLatencyMs, result.LatencyMs);
        if (!result.Success) metrics.Count(MetricNames.ChecksFailed);
        log.LogInformation("Check {CorrelationId} {Outcome} in {LatencyMs} ms (delay {DelaySeconds:F1} s, error {Error})",
            message.IdempotencyKey, result.Success ? "up" : "down", result.LatencyMs, delay.TotalSeconds, result.Error);

        await results.PublishAsync(result, ct);
    }

    public async Task<CheckResultMessage> CheckAsync(CheckMessage m, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        int Elapsed() => (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        CheckResultMessage Result(bool ok, int? status, ErrorKind kind, string? msg) =>
            new(m.AccountId, m.MonitorId, m.Minute, clock.GetUtcNow(), ok, status, Elapsed(), kind, msg);

        if (!UrlGuard.TryValidate(m.Url, options.AllowPrivateTargets, out var guardError))
            return Result(false, null, ErrorKind.Other, guardError);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, m.TimeoutSeconds)));
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(m.Method), m.Url);
            request.Headers.UserAgent.ParseAdd("StatusHub-Checker/1.0");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            if (status != m.ExpectedStatus)
                return Result(false, status, ErrorKind.UnexpectedStatus, $"Expected {m.ExpectedStatus}, got {status}.");

            if (!string.IsNullOrEmpty(m.Keyword))
            {
                var body = await ReadBodyAsync(response, timeout.Token);
                if (!body.Contains(m.Keyword, StringComparison.OrdinalIgnoreCase))
                    return Result(false, status, ErrorKind.KeywordMissing, $"Keyword '{m.Keyword}' not found.");
            }
            return Result(true, status, ErrorKind.None, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result(false, null, ErrorKind.Timeout, $"Timed out after {m.TimeoutSeconds}s.");
        }
        catch (HttpRequestException ex)
        {
            return Result(false, null, Classify(ex), ex.Message);
        }
    }

    public static ErrorKind Classify(HttpRequestException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is AuthenticationException) return ErrorKind.Tls;
            if (e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData or System.Net.Sockets.SocketError.TryAgain })
                return ErrorKind.Dns;
        }
        return ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => ErrorKind.Dns,
            HttpRequestError.SecureConnectionError => ErrorKind.Tls,
            HttpRequestError.ConnectionError => ErrorKind.Connection,
            _ => ErrorKind.Connection,
        };
    }

    private async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while (buffer.Length < options.MaxBodyBytes && (read = await stream.ReadAsync(chunk, ct)) > 0)
            buffer.Write(chunk, 0, read);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}

public sealed class EvaluatorStage(
    IMonitorRepository monitors,
    IResultRepository results,
    IAggregateRepository aggregates,
    IIncidentRepository incidents,
    IIdempotencyStore idempotency,
    IMessagePublisher<IncidentEvent> events,
    IStatusMetrics metrics,
    ILogger<EvaluatorStage> log) : IStage<CheckResultMessage>
{
    public async Task HandleAsync(CheckResultMessage r, CancellationToken ct)
    {
        // SQS is at-least-once: a duplicate result for the same check must not be counted twice.
        var key = $"eval#{r.MonitorId}#{r.Minute.UtcDateTime:yyyyMMddHHmm}";
        if (!await idempotency.TryAcquireAsync(key, SchedulerStage.KeyTtl, ct)) return;

        try
        {
            var monitor = await monitors.GetAsync(r.AccountId, r.MonitorId, ct);
            if (monitor is null) return; // deleted meanwhile
            metrics.Count(MetricNames.ResultsProcessed);

            await results.AddAsync(new CheckResult(r.MonitorId, r.CheckedAt, r.Success, r.StatusCode, r.LatencyMs, r.Error, r.ErrorMessage), ct);
            await aggregates.RecordAsync(r.MonitorId, DateOnly.FromDateTime(r.CheckedAt.UtcDateTime), r.Success, r.LatencyMs, ct);

            var (next, transition) = IncidentPolicy.Apply(monitor, r.Success, r.CheckedAt);
            switch (transition)
            {
                case Transition.Opened:
                    var cause = DescribeCause(r);
                    var incident = new Incident(r.AccountId, r.MonitorId, Incident.NewId(r.CheckedAt), r.CheckedAt, null, cause);
                    await incidents.OpenAsync(incident, ct);
                    next = next with { OpenIncidentId = incident.IncidentId };
                    await monitors.UpdateAsync(next, ct);
                    await events.PublishAsync(Event(next, incident.IncidentId, IncidentEventType.Opened, r.CheckedAt, cause, null), ct);
                    metrics.Count(MetricNames.IncidentsOpened);
                    log.LogInformation("Incident opened for {MonitorId}: {Cause}", r.MonitorId, cause);
                    break;
                case Transition.Resolved:
                    var open = await incidents.GetAsync(r.MonitorId, monitor.OpenIncidentId!, ct);
                    await incidents.ResolveAsync(r.MonitorId, monitor.OpenIncidentId!, r.CheckedAt, ct);
                    await monitors.UpdateAsync(next, ct);
                    await events.PublishAsync(Event(next, monitor.OpenIncidentId!, IncidentEventType.Resolved, r.CheckedAt, open?.Cause, open is null ? null : r.CheckedAt - open.OpenedAt), ct);
                    metrics.Count(MetricNames.IncidentsResolved);
                    log.LogInformation("Incident resolved for {MonitorId}", r.MonitorId);
                    break;
                default:
                    await monitors.UpdateAsync(next, ct);
                    break;
            }
        }
        catch
        {
            await idempotency.ReleaseAsync(key, CancellationToken.None); // let the queue redeliver
            throw;
        }
    }

    private static IncidentEvent Event(UptimeMonitor m, string incidentId, IncidentEventType type, DateTimeOffset at, string? cause, TimeSpan? duration) =>
        new(m.AccountId, m.MonitorId, m.Name, m.Url, incidentId, type, at, cause, duration);

    public static string DescribeCause(CheckResultMessage r) => r.Error switch
    {
        ErrorKind.UnexpectedStatus => $"Unexpected status code {r.StatusCode}",
        ErrorKind.KeywordMissing => "Expected keyword missing",
        ErrorKind.Timeout => "Timeout",
        ErrorKind.Dns => "DNS resolution failed",
        ErrorKind.Tls => "TLS error",
        ErrorKind.Connection => "Connection failed",
        _ => r.ErrorMessage ?? "Unknown error",
    };
}

public static class RetryPolicy
{
    /// <summary>Runs the operation, waiting the given growing delays between attempts. Throws the last error.</summary>
    public static async Task RunAsync(Func<CancellationToken, Task> operation, IReadOnlyList<TimeSpan> delays, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await operation(ct);
                return;
            }
            catch (Exception) when (attempt < delays.Count && !ct.IsCancellationRequested)
            {
                if (delays[attempt] > TimeSpan.Zero) await Task.Delay(delays[attempt], ct);
            }
        }
    }
}

public sealed record NotifierOptions(IReadOnlyList<TimeSpan> RetryDelays)
{
    public static NotifierOptions Default { get; } = new([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)]);
}

/// <summary>Delivers one event to one subscription (email or signed webhook), with retries of growing delay.</summary>
public sealed class AlertDispatcher(IEmailSender email, IWebhookSender webhook, NotifierOptions options)
{
    public Task DispatchAsync(AlertSubscription sub, IncidentEvent e, CancellationToken ct) =>
        RetryPolicy.RunAsync(token => sub.Type == AlertType.Email
            ? email.SendAsync(sub.Target, Subject(e), Body(e), token)
            : webhook.SendAsync(sub.Target, sub.Secret, Json(e), token), options.RetryDelays, ct);

    public static string Subject(IncidentEvent e) => e.Type == IncidentEventType.Opened
        ? $"[StatusHub] {e.MonitorName} est en panne"
        : $"[StatusHub] {e.MonitorName} est retabli";

    public static string Body(IncidentEvent e)
    {
        var sb = new StringBuilder();
        sb.AppendLine(e.Type == IncidentEventType.Opened ? $"Incident ouvert sur {e.MonitorName}" : $"Incident resolu sur {e.MonitorName}");
        sb.AppendLine($"URL : {e.MonitorUrl}");
        sb.AppendLine($"Date : {e.At:u}");
        if (e.Cause is not null) sb.AppendLine($"Cause : {e.Cause}");
        if (e.Duration is { } d) sb.AppendLine($"Duree : {(int)d.TotalMinutes} min {d.Seconds} s");
        return sb.ToString();
    }

    public static string EventName(IncidentEvent e) => e.IncidentId == "test" ? "test"
        : e.Type == IncidentEventType.Opened ? "incident.opened" : "incident.resolved";

    public static string Json(IncidentEvent e) => JsonSerializer.Serialize(
        new WebhookPayload(EventName(e), e.MonitorId, e.MonitorName, e.MonitorUrl, e.IncidentId, e.At, e.Cause, e.Duration?.TotalSeconds),
        StatusHubJson.Default.WebhookPayload);
}

public sealed class NotifierStage(IAlertRepository alerts, AlertDispatcher dispatcher, IStatusMetrics metrics, ILogger<NotifierStage> log) : IStage<IncidentEvent>
{
    public async Task HandleAsync(IncidentEvent e, CancellationToken ct)
    {
        var subs = (await alerts.ListAsync(e.AccountId, ct)).Where(s => s.Active).ToList();
        var failed = 0;
        foreach (var sub in subs)
        {
            try
            {
                await dispatcher.DispatchAsync(sub, e, ct);
                metrics.Count(MetricNames.AlertsSent);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failed++;
                metrics.Count(MetricNames.AlertsFailed);
                log.LogError(ex, "Alert delivery failed for subscription {SubscriptionId} after retries", sub.SubscriptionId);
            }
        }
        // If nothing could be delivered, fail so the queue retries and eventually dead-letters the message.
        if (subs.Count > 0 && failed == subs.Count)
            throw new InvalidOperationException($"All {failed} alert deliveries failed for incident {e.IncidentId}.");
    }
}
