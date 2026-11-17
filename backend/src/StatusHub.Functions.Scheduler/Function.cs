using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Microsoft.Extensions.DependencyInjection;
using StatusHub.Application;
using StatusHub.Infrastructure;
using StatusHub.Observability;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace StatusHub.Functions.Scheduler;

/// <summary>EventBridge Scheduler (every minute) -> publishes one check message per due monitor.</summary>
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

    public Task HandleAsync(Stream input, ILambdaContext context) =>
        Telemetry.RunAsync(context, () =>
            Services.GetRequiredService<IStage<SchedulerTick>>().HandleAsync(SchedulerTick.Now(TimeProvider.System), CancellationToken.None));
}
