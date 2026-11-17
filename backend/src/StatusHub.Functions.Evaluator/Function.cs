using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.Lambda.SQSEvents;
using Microsoft.Extensions.DependencyInjection;
using StatusHub.Application;
using StatusHub.Domain;
using StatusHub.Infrastructure;
using StatusHub.Observability;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace StatusHub.Functions.Evaluator;

/// <summary>SQS results queue -> stores results/aggregates, opens and closes incidents, emits incident events.</summary>
public sealed class Function
{
    private static readonly ServiceProvider Services = Init();

    private static ServiceProvider Init()
    {
        TracingSetup.Register();
        return DependencyInjection.BuildLambdaServices((s, c) =>
        {
            PowertoolsSetup.Use(s);
            s.AddSingleton(TimeProvider.System);
            s.AddStatusHubDynamo(c);
            s.AddStatusHubAwsPublishers(c);
        });
    }

    public Task<SQSBatchResponse> HandleAsync(SQSEvent evt, ILambdaContext context) =>
        Telemetry.RunAsync(context, () =>
            SqsBatch.ProcessAsync(evt, Services.GetRequiredService<IStage<CheckResultMessage>>(), StatusHubJson.Default.CheckResultMessage, CancellationToken.None));
}
