using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.Lambda.SQSEvents;
using Microsoft.Extensions.DependencyInjection;
using StatusHub.Application;
using StatusHub.Domain;
using StatusHub.Infrastructure;
using StatusHub.Observability;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace StatusHub.Functions.Notifier;

/// <summary>SQS notifier queue (fed by an EventBridge rule on IncidentOpened/IncidentResolved) -> email + signed webhooks.</summary>
public sealed class Function
{
    private static readonly ServiceProvider Services = Init();

    private static ServiceProvider Init()
    {
        TracingSetup.Register();
        return DependencyInjection.BuildLambdaServices((s, c) =>
        {
            s.AddSingleton(TimeProvider.System);
            s.AddStatusHubDynamo(c);
            s.AddStatusHubHttp(c);
            s.AddStatusHubNotifications(c);
        });
    }

    public Task<SQSBatchResponse> HandleAsync(SQSEvent evt, ILambdaContext context) =>
        Telemetry.RunAsync(context, () =>
            SqsBatch.ProcessAsync(evt, Services.GetRequiredService<IStage<IncidentEvent>>(), StatusHubJson.Default.IncidentEvent, CancellationToken.None, unwrapDetail: true));
}
