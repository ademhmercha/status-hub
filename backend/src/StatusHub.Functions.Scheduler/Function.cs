using StatusHub.Application;
using StatusHub.Domain;

namespace StatusHub.Functions.Scheduler;

/// <summary>Lambda entry point. Only wires the shared stage; no business logic here.</summary>
public sealed class Function(IStage<SchedulerTick> stage)
{
    public Task HandleAsync(SchedulerTick input, CancellationToken ct = default) => stage.HandleAsync(input, ct);
}
