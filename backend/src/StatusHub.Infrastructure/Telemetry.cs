using Amazon.Lambda.Core;
using AWS.Lambda.Powertools.Logging;
using AWS.Lambda.Powertools.Metrics;
using StatusHub.Application;

namespace StatusHub.Infrastructure;

/// <summary>CloudWatch EMF metrics through Powertools. Namespace comes from POWERTOOLS_METRICS_NAMESPACE, the Service dimension from POWERTOOLS_SERVICE_NAME.</summary>
public sealed class PowertoolsStatusMetrics : IStatusMetrics
{
    public void Count(string name, double value = 1) => Metrics.AddMetric(name, value, MetricUnit.Count);
    public void Milliseconds(string name, double value) => Metrics.AddMetric(name, value, MetricUnit.Milliseconds);
    public void Seconds(string name, double value) => Metrics.AddMetric(name, value, MetricUnit.Seconds);
}

/// <summary>Per-invocation observability: correlation id in every log line, cold-start metric, metrics flush.</summary>
public static class Telemetry
{
    public const string CorrelationHeader = "X-Correlation-Id";

    /// <summary>X-Ray trace header of the current invocation (set by the Lambda runtime when tracing is active).</summary>
    public static string? TraceHeader => Environment.GetEnvironmentVariable("_X_AMZN_TRACE_ID");

    /// <summary>
    /// The X-Ray root id (1-xxxxxxxx-xxxxxxxxxxxxxxxxxxxxxxxx) is propagated Scheduler -> Checker -> Evaluator -> Notifier through the
    /// SQS/EventBridge trace header, so the same id correlates the logs of the whole chain.
    /// </summary>
    public static string? CorrelationId(string? fallback = null)
    {
        var header = TraceHeader;
        if (header is not null)
        {
            foreach (var part in header.Split(';'))
                if (part.StartsWith("Root=", StringComparison.Ordinal)) return part[5..];
        }
        return fallback;
    }

    public static void Begin(ILambdaContext? context)
    {
        Logger.AppendKey("correlation_id", CorrelationId(context?.AwsRequestId) ?? "none");
        if (context is not null) Metrics.Instance.CaptureColdStartMetric(context);
    }

    public static void End() => Metrics.Flush();

    /// <summary>Runs a Lambda handler with correlation, cold-start capture and a guaranteed metrics flush.</summary>
    public static async Task<T> RunAsync<T>(ILambdaContext? context, Func<Task<T>> body)
    {
        Begin(context);
        try { return await body(); }
        finally { End(); }
    }

    public static async Task RunAsync(ILambdaContext? context, Func<Task> body)
    {
        Begin(context);
        try { await body(); }
        finally { End(); }
    }
}
