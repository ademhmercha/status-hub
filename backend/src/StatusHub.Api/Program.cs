using Amazon.DynamoDBv2;
using StatusHub.Application;
using StatusHub.Infrastructure;
using StatusHub.Infrastructure.Dynamo;
using StatusHub.LocalHost;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddStatusHubDynamo(builder.Configuration);
builder.Services.AddStatusHubHttp(builder.Configuration);
builder.Services.AddStatusHubNotifications(builder.Configuration);

// Local mode: in-process worker simulating Scheduler -> Checker -> Evaluator -> Notifier.
var localWorker = builder.Configuration.GetValue<bool>("StatusHub:LocalWorker");
if (localWorker) builder.Services.AddLocalPipeline();

var app = builder.Build();

if (localWorker)
{
    // Local only: Terraform owns the table in AWS.
    await TableBootstrapper.EnsureAsync(app.Services.GetRequiredService<IAmazonDynamoDB>(), app.Services.GetRequiredService<DynamoOptions>().TableName);
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Demo target: its state (ok / slow / down) is driven from the demo panel.
app.MapGet("/demo/target", async (IDemoStateStore state, CancellationToken ct) =>
{
    switch (await state.GetAsync(ct))
    {
        case DemoMode.Down:
            return Results.Text("StatusHub demo target: DOWN", statusCode: StatusCodes.Status503ServiceUnavailable);
        case DemoMode.Slow:
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            return Results.Text("StatusHub demo target: OK (slow)");
        default:
            return Results.Text("StatusHub demo target: OK");
    }
});

app.Run();

public partial class Program;
