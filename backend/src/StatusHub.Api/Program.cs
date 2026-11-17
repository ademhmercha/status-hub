using StatusHub.LocalHost;

var builder = WebApplication.CreateBuilder(args);

// Local mode: in-process worker simulating Scheduler -> Checker -> Evaluator -> Notifier.
if (builder.Configuration.GetValue<bool>("StatusHub:LocalWorker"))
    builder.Services.AddLocalPipeline();

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.Run();

public partial class Program;
