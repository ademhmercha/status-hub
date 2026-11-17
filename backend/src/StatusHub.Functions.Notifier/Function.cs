using StatusHub.Application;
using StatusHub.Domain;

namespace StatusHub.Functions.Notifier;

/// <summary>Lambda entry point. Only wires the shared stage; no business logic here.</summary>
public sealed class Function(IStage<IncidentEvent> stage)
{
    public Task HandleAsync(IncidentEvent input, CancellationToken ct = default) => stage.HandleAsync(input, ct);
}
