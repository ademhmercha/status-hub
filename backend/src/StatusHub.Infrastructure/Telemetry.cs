using System.Globalization;
using System.Text;
using System.Text.Json;
using Amazon.Lambda.Core;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using StatusHub.Application;

namespace StatusHub.Infrastructure;

// This file is Native AOT safe on purpose: no reflection, no Powertools. The Checker (AOT) uses it as is.
// The JIT Lambdas and the API swap in Powertools (Shared/PowertoolsSetup.cs) for the same behaviour.

/// <summary>What a Lambda needs from observability, so Powertools can be plugged in only where it is allowed (non-AOT).</summary>
public interface ITelemetryBackend
{
    void SetCorrelationId(string correlationId);
    void CaptureColdStart(ILambdaContext context);
    void Flush();
    void LogError(Exception ex, string message);
}

/// <summary>
/// CloudWatch Embedded Metric Format written by hand (one JSON line on stdout, CloudWatch extracts the metrics).
/// Namespace from POWERTOOLS_METRICS_NAMESPACE, Service dimension from POWERTOOLS_SERVICE_NAME (same contract as Powertools).
/// </summary>
public sealed class EmfStatusMetrics : IStatusMetrics
{
    public static EmfStatusMetrics Default { get; } = new();

    private const int MaxValuesPerMetric = 100;
    private readonly object gate = new();
    private readonly Dictionary<string, (string Unit, List<double> Values)> pending = [];
    private readonly TextWriter? output;
    private readonly string metricsNamespace;
    private readonly string service;

    public EmfStatusMetrics(string? metricsNamespace = null, string? service = null, TextWriter? output = null)
    {
        this.metricsNamespace = metricsNamespace ?? Environment.GetEnvironmentVariable("POWERTOOLS_METRICS_NAMESPACE") ?? "StatusHub";
        this.service = service ?? Environment.GetEnvironmentVariable("POWERTOOLS_SERVICE_NAME") ?? "statushub";
        this.output = output;
    }

    public void Count(string name, double value = 1) => Add(name, "Count", value);
    public void Milliseconds(string name, double value) => Add(name, "Milliseconds", value);
    public void Seconds(string name, double value) => Add(name, "Seconds", value);

    private void Add(string name, string unit, double value)
    {
        lock (gate)
        {
            if (!pending.TryGetValue(name, out var entry)) pending[name] = entry = (unit, []);
            entry.Values.Add(value);
        }
    }

    /// <summary>Writes the pending metrics (several lines if a metric has more than 100 values).</summary>
    public void Flush()
    {
        lock (gate)
        {
            while (pending.Count > 0)
            {
                var batch = new List<(string Name, string Unit, double[] Values)>();
                foreach (var name in pending.Keys.ToList())
                {
                    var (unit, values) = pending[name];
                    var take = Math.Min(MaxValuesPerMetric, values.Count);
                    batch.Add((name, unit, values.GetRange(0, take).ToArray()));
                    values.RemoveRange(0, take);
                    if (values.Count == 0) pending.Remove(name);
                }
                Write(batch, ["Service"], new Dictionary<string, string> { ["Service"] = service });
            }
        }
    }

    /// <summary>One metric with its own dimensions (cold start: Service + FunctionName).</summary>
    public void EmitSingle(string name, double value, string unit, IReadOnlyDictionary<string, string> extraDimensions)
    {
        var dims = new Dictionary<string, string> { ["Service"] = service };
        foreach (var (k, v) in extraDimensions) dims[k] = v;
        lock (gate) Write([(name, unit, [value])], dims.Keys.ToArray(), dims);
    }

    private void Write(List<(string Name, string Unit, double[] Values)> metrics, string[] dimensionNames, Dictionary<string, string> dimensions)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartObject("_aws");
            w.WriteNumber("Timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            w.WriteStartArray("CloudWatchMetrics");
            w.WriteStartObject();
            w.WriteString("Namespace", metricsNamespace);
            w.WriteStartArray("Dimensions");
            w.WriteStartArray();
            foreach (var d in dimensionNames) w.WriteStringValue(d);
            w.WriteEndArray();
            w.WriteEndArray();
            w.WriteStartArray("Metrics");
            foreach (var m in metrics)
            {
                w.WriteStartObject();
                w.WriteString("Name", m.Name);
                w.WriteString("Unit", m.Unit);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            foreach (var (k, v) in dimensions) w.WriteString(k, v);
            foreach (var m in metrics)
            {
                if (m.Values.Length == 1) w.WriteNumber(m.Name, m.Values[0]);
                else
                {
                    w.WriteStartArray(m.Name);
                    foreach (var v in m.Values) w.WriteNumberValue(v);
                    w.WriteEndArray();
                }
            }
            w.WriteEndObject();
        }
        (output ?? Console.Out).WriteLine(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
    }
}

/// <summary>Default backend: hand-written EMF and JSON logs (AOT safe).</summary>
public sealed class SimpleTelemetryBackend : ITelemetryBackend
{
    private static bool coldStartDone;

    public void SetCorrelationId(string correlationId) => Telemetry.CurrentCorrelationId = correlationId;

    public void CaptureColdStart(ILambdaContext context)
    {
        if (coldStartDone) return;
        coldStartDone = true;
        EmfStatusMetrics.Default.EmitSingle("ColdStart", 1, "Count", new Dictionary<string, string> { ["FunctionName"] = context.FunctionName });
    }

    public void Flush() => EmfStatusMetrics.Default.Flush();

    public void LogError(Exception ex, string message) =>
        JsonLineLogger.Write("Error", "StatusHub", message, ex);
}

/// <summary>JSON lines with the correlation id, one per log event (AOT safe replacement for the console provider).</summary>
public sealed class JsonLineLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new JsonLineLogger(categoryName);
    public void Dispose() { }
}

public sealed class JsonLineLogger(string category) : ILogger
{
    private static readonly LogLevel Minimum =
        string.Equals(Environment.GetEnvironmentVariable("POWERTOOLS_LOG_LEVEL"), "Debug", StringComparison.OrdinalIgnoreCase) ? LogLevel.Debug : LogLevel.Information;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= Minimum && logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        Write(logLevel.ToString(), category, formatter(state, exception), exception);
    }

    internal static void Write(string level, string category, string message, Exception? ex)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("timestamp", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            w.WriteString("level", level);
            w.WriteString("service", Environment.GetEnvironmentVariable("POWERTOOLS_SERVICE_NAME") ?? "statushub");
            w.WriteString("correlation_id", Telemetry.CurrentCorrelationId ?? "none");
            w.WriteString("category", category);
            w.WriteString("message", message);
            if (ex is not null) w.WriteString("exception", ex.ToString());
            w.WriteEndObject();
        }
        Console.Out.WriteLine(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
    }
}

/// <summary>Per-invocation observability: correlation id in every log line, cold-start metric, metrics flush.</summary>
public static class Telemetry
{
    public const string CorrelationHeader = "X-Correlation-Id";

    public static ITelemetryBackend Backend { get; set; } = new SimpleTelemetryBackend();
    public static string? CurrentCorrelationId { get; set; }

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
        Backend.SetCorrelationId(CorrelationId(context?.AwsRequestId) ?? "none");
        if (context is not null) Backend.CaptureColdStart(context);
    }

    public static void End() => Backend.Flush();

    public static void LogError(Exception ex, string message) => Backend.LogError(ex, message);

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
