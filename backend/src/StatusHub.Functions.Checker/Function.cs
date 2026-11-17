using StatusHub.Application;
using StatusHub.Domain;

namespace StatusHub.Functions.Checker;

/// <summary>Lambda entry point. Only wires the shared stage; no business logic here.</summary>
public sealed class Function(IStage<CheckMessage> stage)
{
    public Task HandleAsync(CheckMessage input, CancellationToken ct = default) => stage.HandleAsync(input, ct);
}
