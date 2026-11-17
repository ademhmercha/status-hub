using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Amazon.DynamoDBv2;
using StatusHub.Api;
using StatusHub.Infrastructure;
using StatusHub.Infrastructure.Dynamo;
using StatusHub.LocalHost;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

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

