using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using StatusHub.Domain;

namespace StatusHub.Application;

/// <summary>A pipeline stage. The same class is used by the Lambda entry points and by the local worker.</summary>
public interface IStage<in TIn>
{
    Task HandleAsync(TIn message, CancellationToken ct);
}

public interface IMessagePublisher<in T>
{
    Task PublishAsync(T message, CancellationToken ct);
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
        s.AddTransient<IStage<SchedulerTick>, SchedulerStage>();
        s.AddTransient<IStage<CheckMessage>, CheckerStage>();
        s.AddTransient<IStage<CheckResultMessage>, EvaluatorStage>();
        s.AddTransient<IStage<IncidentEvent>, NotifierStage>();
        return s;
    }
}
