using Amazon.Lambda.Core;
using AWS.Lambda.Powertools.Logging;
using AWS.Lambda.Powertools.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StatusHub.Application;
using StatusHub.Infrastructure;

namespace StatusHub.Observability;

/// <summary>
/// Powertools for AWS Lambda (logging + EMF metrics), compiled into the JIT Lambdas and the API only. The Native AOT Checker cannot use
/// Powertools.Logging (trim warnings), so it runs on the hand-written equivalents of StatusHub.Infrastructure/Telemetry.cs.
/// </summary>
internal static class PowertoolsSetup
{
    public static void Use(IServiceCollection services)
    {
        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.AddPowertoolsLogger(c => { });
        });
        services.AddSingleton<IStatusMetrics, PowertoolsStatusMetrics>();
        Telemetry.Backend = new PowertoolsBackend();
    }
}

internal sealed class PowertoolsStatusMetrics : IStatusMetrics
{
    // Stages may run checks concurrently: serialize access to the shared Powertools metrics context.
    private static readonly object Gate = new();

    public void Count(string name, double value = 1)
    {
        lock (Gate) Metrics.AddMetric(name, value, MetricUnit.Count);
    }

    public void Milliseconds(string name, double value)
    {
        lock (Gate) Metrics.AddMetric(name, value, MetricUnit.Milliseconds);
    }

    public void Seconds(string name, double value)
    {
        lock (Gate) Metrics.AddMetric(name, value, MetricUnit.Seconds);
    }
}

internal sealed class PowertoolsBackend : ITelemetryBackend
{
    public void SetCorrelationId(string correlationId) => Logger.AppendKey("correlation_id", correlationId);
    public void CaptureColdStart(ILambdaContext context) => Metrics.Instance.CaptureColdStartMetric(context);
    public void Flush() => Metrics.Flush();
    public void LogError(Exception ex, string message) => Logger.LogError(ex, message);
}
