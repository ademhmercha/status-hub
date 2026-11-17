using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using AWS.Lambda.Powertools.Logging;
using StatusHub.Api;
using StatusHub.Infrastructure;
using StatusHub.Infrastructure.Dynamo;
using StatusHub.LocalHost;
using StatusHub.Observability;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// In AWS: structured JSON logs with correlation id (Powertools), X-Ray subsegments for AWS SDK calls.
var inLambda = Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME") is not null;
if (inLambda)
{
    TracingSetup.Register();
    builder.Logging.ClearProviders();
    builder.Logging.AddPowertoolsLogger(c => { });
}

builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);
builder.Services.AddStatusHubDynamo(cfg);
builder.Services.AddStatusHubHttp(cfg);
builder.Services.AddStatusHubNotifications(cfg);
builder.AddStatusHubAuth();

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddOutputCache();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins((cfg["Cors:Origins"] ?? "http://localhost:4200").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    .AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    string Ip(HttpContext c) => c.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim() ?? c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    void Add(string name, string key, int def, Func<HttpContext, string> part) =>
        o.AddPolicy(name, c => RateLimitPartition.GetFixedWindowLimiter(part(c), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = cfg.GetValue<int?>(key) ?? def, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
        }));
    Add(Endpoints.ApiPolicy, "RateLimit:ApiPerMinute", 120, c => "u:" + (c.User.FindFirst("sub")?.Value ?? Ip(c)));
    Add(Endpoints.PublicPolicy, "RateLimit:PublicPerMinute", 60, c => "ip:" + Ip(c));
    Add(Endpoints.DemoPolicy, "RateLimit:DemoPerMinute", 10, c => "d:" + (c.User.FindFirst("sub")?.Value ?? Ip(c)));
    Add(Endpoints.TokenPolicy, "RateLimit:TokenPerMinute", 10, c => "t:" + Ip(c));
});

// Local mode: in-process worker simulating Scheduler -> Checker -> Evaluator -> Notifier.
var localWorker = cfg.GetValue<bool>("StatusHub:LocalWorker");
if (localWorker) builder.Services.AddLocalPipeline();

var app = builder.Build();

if (localWorker)
{
    // Local only: Terraform owns the table in AWS.
    await TableBootstrapper.EnsureAsync(app.Services.GetRequiredService<IAmazonDynamoDB>(), app.Services.GetRequiredService<DynamoOptions>().TableName);
}

// Correlation id on every response and log line; EMF metrics (cold start) flushed at the end of each request.
app.Use(async (ctx, next) =>
{
    var lambdaContext = ctx.Items["LambdaContext"] as ILambdaContext;
    ctx.Response.Headers[Telemetry.CorrelationHeader] = Telemetry.CorrelationId(lambdaContext?.AwsRequestId) ?? ctx.TraceIdentifier;
    if (!inLambda) { await next(); return; }
    Telemetry.Begin(lambdaContext);
    try { await next(); }
    finally { Telemetry.End(); }
});

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseOutputCache();
app.MapOpenApi().AllowAnonymous();
app.MapStatusHub();

app.Run();

public partial class Program;

