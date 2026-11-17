using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.Lambda.SQSEvents;
using Microsoft.Extensions.DependencyInjection;
using StatusHub.Application;
using StatusHub.Domain;
using StatusHub.Infrastructure;

namespace StatusHub.Functions.Checker;

/// <summary>Native AOT Lambda (custom runtime provided.al2023). SQS checks queue -> HTTP check -> SQS results queue.</summary>
public static class Program
{
    private static readonly ServiceProvider Services = DependencyInjection.BuildLambdaServices((s, c) =>
    {
        s.AddSingleton(TimeProvider.System);
        s.AddStatusHubHttp(c);
        s.AddStatusHubAwsPublishers(c, bufferResults: true);
    });

    public static async Task Main()
    {
        Func<SQSEvent, ILambdaContext, Task<SQSBatchResponse>> handler = HandleAsync;
        await LambdaBootstrapBuilder.Create(handler, new SourceGeneratorLambdaJsonSerializer<LambdaJson>()).Build().RunAsync();
    }

    public static Task<SQSBatchResponse> HandleAsync(SQSEvent evt, ILambdaContext context) =>
        Telemetry.RunAsync(context, () =>
            SqsBatch.ProcessAsync(evt, Services.GetRequiredService<IStage<CheckMessage>>(), StatusHubJson.Default.CheckMessage, CancellationToken.None,
                parallel: true, // 10 checks of a batch run concurrently
                afterAll: ct => Services.GetRequiredService<BufferedPublisher<CheckResultMessage>>().FlushAsync(ct))); // 1 SQS request for the results
}

[System.Text.Json.Serialization.JsonSerializable(typeof(SQSEvent))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SQSBatchResponse))]
public partial class LambdaJson : System.Text.Json.Serialization.JsonSerializerContext;