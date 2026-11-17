using StatusHub.Domain;

namespace StatusHub.Application;

/// <summary>A pipeline stage. Same class is used by the Lambda entry points and by the local worker.</summary>
public interface IStage<in TIn>
{
    Task HandleAsync(TIn message, CancellationToken ct);
}

public interface IMessagePublisher<in T>
{
    Task PublishAsync(T message, CancellationToken ct);
}

/// <summary>Scheduler stage input: the minute to schedule.</summary>
public sealed record SchedulerTick(DateTimeOffset Minute);

public sealed class SchedulerStage() : IStage<SchedulerTick>
{
    // Real logic (due monitors, idempotency) arrives in prompt 1.3.
    public Task HandleAsync(SchedulerTick message, CancellationToken ct) => Task.CompletedTask;
}

public sealed class CheckerStage() : IStage<CheckMessage>
{
    public Task HandleAsync(CheckMessage message, CancellationToken ct) => Task.CompletedTask;
}

public sealed class EvaluatorStage() : IStage<CheckResultMessage>
{
    public Task HandleAsync(CheckResultMessage message, CancellationToken ct) => Task.CompletedTask;
}

public sealed class NotifierStage : IStage<IncidentEvent>
{
    public Task HandleAsync(IncidentEvent message, CancellationToken ct) => Task.CompletedTask;
}

