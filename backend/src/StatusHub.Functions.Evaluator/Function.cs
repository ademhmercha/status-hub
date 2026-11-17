using StatusHub.Application;
using StatusHub.Domain;

namespace StatusHub.Functions.Evaluator;

/// <summary>Lambda entry point. Only wires the shared stage; no business logic here.</summary>
public sealed class Function(IStage<CheckResultMessage> stage)
{
    public Task HandleAsync(CheckResultMessage input, CancellationToken ct = default) => stage.HandleAsync(input, ct);
}
