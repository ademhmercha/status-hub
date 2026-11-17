using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using StatusHub.Application;
using StatusHub.Domain;
using StatusHub.Infrastructure;

namespace StatusHub.UnitTests;

public class MetricsTests
{
    private static readonly DateTimeOffset Minute = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static (CheckerStage Stage, RecordingMetrics Metrics) Checker(int status, DateTimeOffset now)
    {
        var m = new RecordingMetrics();
        var stage = new CheckerStage(new HttpClient(StubHandler.Returning(status)), new Recorder<CheckResultMessage>(), new FixedClock(now),
            new CheckerOptions(), m, NullLogger<CheckerStage>.Instance);
        return (stage, m);
    }

    private static CheckMessage Msg() => new("a", "m1", Minute, "https://example.com", "GET", 200, null, 5);

    [Fact]
    public async Task Checker_OnTime_RecordsExecutedLatencyAndDelay()
    {
        var (s, m) = Checker(200, Minute.AddSeconds(3));
        await s.HandleAsync(Msg(), default);
        Assert.Equal(1, m.Sum(MetricNames.ChecksExecuted));
        Assert.Equal(1, m.Sum(MetricNames.ChecksOnTime));
        Assert.False(m.Has(MetricNames.ChecksLate));
        Assert.False(m.Has(MetricNames.ChecksFailed));
        Assert.Equal(3, m.Sum(MetricNames.CheckDelaySeconds));
        Assert.True(m.Has(MetricNames.CheckLatencyMs));
    }

    [Theory]
    [InlineData(60, true)]   // exactly 60 s is still on time
    [InlineData(61, false)]
    [InlineData(300, false)]
    public async Task Checker_SloThreshold_Is60Seconds(int delaySeconds, bool onTime)
    {
        var (s, m) = Checker(200, Minute.AddSeconds(delaySeconds));
        await s.HandleAsync(Msg(), default);
        Assert.Equal(onTime ? 1 : 0, m.Sum(MetricNames.ChecksOnTime));
        Assert.Equal(onTime ? 0 : 1, m.Sum(MetricNames.ChecksLate));
    }

    [Fact]
    public async Task Checker_TargetFailure_CountsAsFailedButStillOnTime()
    {
        var (s, m) = Checker(503, Minute.AddSeconds(2));
        await s.HandleAsync(Msg(), default);
        Assert.Equal(1, m.Sum(MetricNames.ChecksFailed));
        Assert.Equal(1, m.Sum(MetricNames.ChecksOnTime)); // the check ran on time: the SLO measures StatusHub, not the target
    }

    [Fact]
    public async Task Scheduler_CountsScheduledChecks_IncludingAlreadyPublishedOnes()
    {
        var monitors = new FakeMonitors();
        for (var i = 0; i < 3; i++)
            await monitors.CreateAsync(new UptimeMonitor("a", "m" + i, "n", "https://x.io", "GET", 1, 200, null, 5, true, Minute));
        var idem = new FakeIdempotency();
        var metrics = new RecordingMetrics();
        var stage = new SchedulerStage(monitors, idem, new Recorder<CheckMessage>(), metrics, NullLogger<SchedulerStage>.Instance);

        await stage.HandleAsync(new SchedulerTick(Minute), default);
        await stage.HandleAsync(new SchedulerTick(Minute), default); // duplicate invocation: nothing re-published, still due
        Assert.Equal(6, metrics.Sum(MetricNames.ChecksScheduled));
    }

    [Fact]
    public async Task Evaluator_CountsResultsAndIncidents()
    {
        var monitors = new FakeMonitors();
        monitors.Items["m"] = new UptimeMonitor("a", "m", "Site", "https://x.io", "GET", 1, 200, null, 5, true, Minute);
        var metrics = new RecordingMetrics();
        var stage = new EvaluatorStage(monitors, new FakeResults(), new FakeAggregates(), new FakeIncidents(), new FakeIdempotency(),
            new Recorder<IncidentEvent>(), metrics, NullLogger<EvaluatorStage>.Instance);

        async Task Feed(int minute, bool ok) => await stage.HandleAsync(new CheckResultMessage("a", "m", Minute.AddMinutes(minute),
            Minute.AddMinutes(minute), ok, ok ? 200 : 500, 100, ok ? ErrorKind.None : ErrorKind.UnexpectedStatus, null), default);

        await Feed(0, false);
        await Feed(1, false);
        await Feed(2, true);
        await Feed(3, true);

        Assert.Equal(4, metrics.Sum(MetricNames.ResultsProcessed));
        Assert.Equal(1, metrics.Sum(MetricNames.IncidentsOpened));
        Assert.Equal(1, metrics.Sum(MetricNames.IncidentsResolved));
    }

    [Fact]
    public async Task Notifier_CountsSentAndFailedAlerts()
    {
        var email = new FakeEmail();
        var hook = new FakeWebhook { AlwaysFail = true };
        var metrics = new RecordingMetrics();
        var subs = new[]
        {
            new AlertSubscription("a", "e", AlertType.Email, "me@x.io", null, true),
            new AlertSubscription("a", "w", AlertType.Webhook, "https://h.io", "s", true),
        };
        var stage = new NotifierStage(new FakeAlerts(subs), new AlertDispatcher(email, hook, new NotifierOptions([])), metrics, NullLogger<NotifierStage>.Instance);
        var evt = new IncidentEvent("a", "m", "Site", "https://x.io", "i", IncidentEventType.Opened, Minute, "Timeout", null);

        await stage.HandleAsync(evt, default);
        Assert.Equal(1, metrics.Sum(MetricNames.AlertsSent));
        Assert.Equal(1, metrics.Sum(MetricNames.AlertsFailed));
    }
}

public class TelemetryTests
{
    [Fact]
    public void CorrelationId_IsTheXRayRootId_AndFallsBackWhenTracingIsOff()
    {
        var old = Environment.GetEnvironmentVariable("_X_AMZN_TRACE_ID");
        try
        {
            Environment.SetEnvironmentVariable("_X_AMZN_TRACE_ID", "Root=1-5759e988-bd862e3fe1be46a994272793;Parent=53995c3f42cd8ad8;Sampled=1");
            Assert.Equal("1-5759e988-bd862e3fe1be46a994272793", Telemetry.CorrelationId("req-1"));
            Environment.SetEnvironmentVariable("_X_AMZN_TRACE_ID", null);
            Assert.Equal("req-1", Telemetry.CorrelationId("req-1"));
            Assert.Null(Telemetry.CorrelationId());
        }
        finally
        {
            Environment.SetEnvironmentVariable("_X_AMZN_TRACE_ID", old);
        }
    }
}

public class EmfTests
{
    [Fact]
    public void Flush_WritesOneEmfLine_WithNamespaceDimensionUnitsAndValueArrays()
    {
        var sw = new StringWriter();
        var emf = new EmfStatusMetrics("StatusHub/dev", "checker", sw);
        emf.Count(MetricNames.ChecksExecuted);
        emf.Count(MetricNames.ChecksExecuted);
        emf.Milliseconds(MetricNames.CheckLatencyMs, 120);
        emf.Flush();

        var line = sw.ToString().Trim();
        Assert.DoesNotContain('\n', line);
        using var doc = System.Text.Json.JsonDocument.Parse(line);
        var root = doc.RootElement;
        var dir = root.GetProperty("_aws").GetProperty("CloudWatchMetrics")[0];
        Assert.Equal("StatusHub/dev", dir.GetProperty("Namespace").GetString());
        Assert.Equal("Service", dir.GetProperty("Dimensions")[0][0].GetString());
        Assert.Equal("checker", root.GetProperty("Service").GetString());
        Assert.Equal(2, root.GetProperty("ChecksExecuted").GetArrayLength()); // several values = array
        Assert.Equal(120, root.GetProperty("CheckLatencyMs").GetDouble());   // one value = number
        Assert.Contains(dir.GetProperty("Metrics").EnumerateArray(), m => m.GetProperty("Name").GetString() == "CheckLatencyMs" && m.GetProperty("Unit").GetString() == "Milliseconds");

        sw.GetStringBuilder().Clear();
        emf.Flush(); // nothing pending: nothing written
        Assert.Equal("", sw.ToString());
    }

    [Fact]
    public void Flush_SplitsMoreThan100ValuesOfAMetric()
    {
        var sw = new StringWriter();
        var emf = new EmfStatusMetrics("ns", "svc", sw);
        for (var i = 0; i < 230; i++) emf.Count("X");
        emf.Flush();
        Assert.Equal(3, sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void ColdStart_UsesServiceAndFunctionNameDimensions()
    {
        var sw = new StringWriter();
        new EmfStatusMetrics("ns", "svc", sw).EmitSingle("ColdStart", 1, "Count", new Dictionary<string, string> { ["FunctionName"] = "fn" });
        using var doc = System.Text.Json.JsonDocument.Parse(sw.ToString());
        var dims = doc.RootElement.GetProperty("_aws").GetProperty("CloudWatchMetrics")[0].GetProperty("Dimensions")[0];
        Assert.Equal(["Service", "FunctionName"], dims.EnumerateArray().Select(d => d.GetString()));
    }
}
