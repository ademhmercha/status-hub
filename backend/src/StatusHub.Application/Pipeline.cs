using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StatusHub.Domain;

namespace StatusHub.Application;

/// <summary>A pipeline stage. The same class is used by the Lambda entry points and by the local worker.</summary>
public interface IStage<in TIn>
{
    Task HandleAsync(TIn message, CancellationToken ct);
}

public interface IMessagePublisher<T>
{
    Task PublishAsync(T message, CancellationToken ct);

    /// <summary>
    /// Publishes several messages (SQS is billed per request: one batch request carries up to 10 messages).
    /// Returns the messages that could NOT be published; empty when everything went through.
    /// </summary>
    async Task<IReadOnlyList<T>> PublishManyAsync(IReadOnlyList<T> messages, CancellationToken ct)
    {
        var failed = new List<T>();
        foreach (var m in messages)
        {
            try { await PublishAsync(m, ct); }
            catch (Exception) when (!ct.IsCancellationRequested) { failed.Add(m); }
        }
        return failed;
    }
}

/// <summary>
/// Collects messages during one Lambda invocation and sends them in batches at the end (<see cref="FlushAsync"/>).
/// Used by the Checker: 10 results become 1 SQS request instead of 10.
/// </summary>
public sealed class BufferedPublisher<T>(IMessagePublisher<T> inner) : IMessagePublisher<T>
{
    public const int MaxBatch = 10;
    private readonly System.Collections.Concurrent.ConcurrentQueue<T> buffer = new();

    public Task PublishAsync(T message, CancellationToken ct)
    {
        buffer.Enqueue(message);
        return Task.CompletedTask;
    }

    /// <summary>Sends everything buffered. Throws if some messages could not be sent (the caller then fails the SQS batch so it is retried).</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        var pending = new List<T>();
        while (buffer.TryDequeue(out var m)) pending.Add(m);

        var unsent = 0;
        foreach (var chunk in pending.Chunk(MaxBatch))
            unsent += (await inner.PublishManyAsync(chunk, ct)).Count;
        if (unsent > 0) throw new InvalidOperationException($"{unsent} of {pending.Count} messages could not be published.");
    }
}

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string textBody, CancellationToken ct);
}

public interface IWebhookSender
{
    /// <summary>Posts the JSON body, signed with HMAC-SHA256 when a secret is given. Throws on non-2xx.</summary>
    Task SendAsync(string url, string? secret, string jsonBody, CancellationToken ct);
}

public enum DemoMode { Ok, Slow, Down }

public interface IDemoStateStore
{
    Task<DemoMode> GetAsync(CancellationToken ct = default);
    Task SetAsync(DemoMode mode, CancellationToken ct = default);
}

/// <summary>Scheduler stage input: the minute to schedule (truncated to the minute).</summary>
public sealed record SchedulerTick(DateTimeOffset Minute)
{
    public static SchedulerTick Now(TimeProvider clock)
    {
        var n = clock.GetUtcNow();
        return new SchedulerTick(new DateTimeOffset(n.Year, n.Month, n.Day, n.Hour, n.Minute, 0, TimeSpan.Zero));
    }
}

public sealed record WebhookPayload(
    string Event,
    string MonitorId,
    string MonitorName,
    string MonitorUrl,
    string? IncidentId,
    DateTimeOffset At,
    string? Cause,
    double? DurationSeconds);

/// <summary>Reflection-free JSON (Native AOT).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(CheckMessage))]
[JsonSerializable(typeof(CheckResultMessage))]
[JsonSerializable(typeof(IncidentEvent))]
[JsonSerializable(typeof(WebhookPayload))]
public sealed partial class StatusHubJson : JsonSerializerContext;

public static class CoreServiceCollectionExtensions
{
    /// <summary>Registers the 4 stages. Publishers, repositories, HttpClient and senders come from the host.</summary>
    public static IServiceCollection AddStatusHubStages(this IServiceCollection s)
    {
        s.TryAddSingleton<IStatusMetrics, NullStatusMetrics>(); // Lambdas replace it with the EMF implementation
        s.AddTransient<IStage<SchedulerTick>, SchedulerStage>();
        s.AddTransient<IStage<CheckMessage>, CheckerStage>();
        s.AddTransient<IStage<CheckResultMessage>, EvaluatorStage>();
        s.AddTransient<IStage<IncidentEvent>, NotifierStage>();
        return s;
    }
}
