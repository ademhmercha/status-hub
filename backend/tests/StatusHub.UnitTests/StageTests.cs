using System.Net;
using System.Security.Authentication;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using StatusHub.Application;
using StatusHub.Domain;

namespace StatusHub.UnitTests;

public class CheckerStageTests
{
    private static readonly DateTimeOffset Minute = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private static CheckMessage Msg(string url = "https://example.com", int expected = 200, string? keyword = null, int timeout = 5, string method = "GET") =>
        new("a", "m", Minute, url, method, expected, keyword, timeout);

    private static (CheckerStage Stage, Recorder<CheckResultMessage> Out) Build(HttpMessageHandler h, bool allowPrivate = false)
    {
        var rec = new Recorder<CheckResultMessage>();
        return (new CheckerStage(new HttpClient(h), rec, TimeProvider.System, new CheckerOptions(allowPrivate)), rec);
    }

    [Fact]
    public async Task Ok_WhenStatusMatches()
    {
        var (s, _) = Build(StubHandler.Returning(200));
        var r = await s.CheckAsync(Msg(), default);
        Assert.True(r.Success);
        Assert.Equal(200, r.StatusCode);
        Assert.Equal(ErrorKind.None, r.Error);
        Assert.Equal(Minute, r.Minute);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(404)]
    [InlineData(301)]
    public async Task UnexpectedStatus_IsClassified(int status)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        var (s, _) = Build(handler);
        var r = await s.CheckAsync(Msg(), default);
        Assert.False(r.Success);
        Assert.Equal(ErrorKind.UnexpectedStatus, r.Error);
        Assert.Equal(status, r.StatusCode);
    }

    [Fact]
    public async Task CustomExpectedStatus_IsHonoured()
    {
        var (s, _) = Build(StubHandler.Returning(204));
        Assert.True((await s.CheckAsync(Msg(expected: 204), default)).Success);
    }

    [Fact]
    public async Task Keyword_Present_IsOk_CaseInsensitive()
    {
        var (s, _) = Build(StubHandler.Returning(200, "<html>All Systems Operational</html>"));
        Assert.True((await s.CheckAsync(Msg(keyword: "all systems"), default)).Success);
    }

    [Fact]
    public async Task Keyword_Missing_IsClassified()
    {
        var (s, _) = Build(StubHandler.Returning(200, "<html>hello</html>"));
        var r = await s.CheckAsync(Msg(keyword: "operational"), default);
        Assert.False(r.Success);
        Assert.Equal(ErrorKind.KeywordMissing, r.Error);
        Assert.Equal(200, r.StatusCode);
    }

    [Fact]
    public async Task WrongStatus_TakesPrecedenceOverKeyword()
    {
        var (s, _) = Build(StubHandler.Returning(500, "operational"));
        Assert.Equal(ErrorKind.UnexpectedStatus, (await s.CheckAsync(Msg(keyword: "operational"), default)).Error);
    }

    [Fact]
    public async Task Timeout_IsClassified()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        });
        var (s, _) = Build(handler);
        var r = await s.CheckAsync(Msg(timeout: 1), default);
        Assert.Equal(ErrorKind.Timeout, r.Error);
        Assert.InRange(r.LatencyMs, 900, 5000);
    }

    [Fact]
    public async Task CallerCancellation_IsNotReportedAsTimeout()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler(async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        });
        var (s, _) = Build(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.CheckAsync(Msg(timeout: 30), cts.Token));
    }

    [Fact]
    public async Task Dns_ByHttpRequestError()
    {
        var (s, _) = Build(StubHandler.Throwing(new HttpRequestException(HttpRequestError.NameResolutionError, "no such host")));
        Assert.Equal(ErrorKind.Dns, (await s.CheckAsync(Msg(), default)).Error);
    }

    [Fact]
    public async Task Dns_BySocketInnerException()
    {
        var ex = new HttpRequestException("failed", new SocketException((int)SocketError.HostNotFound));
        var (s, _) = Build(StubHandler.Throwing(ex));
        Assert.Equal(ErrorKind.Dns, (await s.CheckAsync(Msg(), default)).Error);
    }

    [Fact]
    public async Task Tls_ByHttpRequestError()
    {
        var (s, _) = Build(StubHandler.Throwing(new HttpRequestException(HttpRequestError.SecureConnectionError, "bad cert")));
        Assert.Equal(ErrorKind.Tls, (await s.CheckAsync(Msg(), default)).Error);
    }

    [Fact]
    public async Task Tls_ByAuthenticationInnerException()
    {
        var ex = new HttpRequestException("ssl", new AuthenticationException("certificate expired"));
        var (s, _) = Build(StubHandler.Throwing(ex));
        Assert.Equal(ErrorKind.Tls, (await s.CheckAsync(Msg(), default)).Error);
    }

    [Fact]
    public async Task ConnectionRefused_IsConnectionError()
    {
        var (s, _) = Build(StubHandler.Throwing(new HttpRequestException(HttpRequestError.ConnectionError, "refused")));
        Assert.Equal(ErrorKind.Connection, (await s.CheckAsync(Msg(), default)).Error);
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://169.254.169.254/")]
    [InlineData("ftp://example.com/")]
    public async Task BlockedTargets_AreNotRequested(string url)
    {
        var called = false;
        var handler = new StubHandler((_, _) => { called = true; return Task.FromResult(new HttpResponseMessage()); });
        var (s, _) = Build(handler);
        var r = await s.CheckAsync(Msg(url), default);
        Assert.False(r.Success);
        Assert.Equal(ErrorKind.Other, r.Error);
        Assert.False(called);
    }

    [Fact]
    public async Task Handle_PublishesResult_AndSendsConfiguredMethodAndUserAgent()
    {
        HttpRequestMessage? seen = null;
        var handler = new StubHandler((req, _) => { seen = req; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });
        var (s, rec) = Build(handler);
        await s.HandleAsync(Msg(method: "HEAD"), default);
        Assert.Equal(HttpMethod.Head, seen!.Method);
        Assert.Contains("StatusHub", seen.Headers.UserAgent.ToString());
        Assert.Single(rec.Messages);
    }
}

public class SchedulerStageTests
{
    private static readonly DateTimeOffset Minute = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private static UptimeMonitor M(string id, bool active = true) =>
        new("a", id, id, "https://x.io/" + id, "GET", 1, 200, "kw", 7, active, Minute);

    [Fact]
    public async Task PublishesOneMessagePerDueMonitor_WithConfig()
    {
        var monitors = new FakeMonitors();
        await monitors.CreateAsync(M("m1"));
        await monitors.CreateAsync(M("m2"));
        await monitors.CreateAsync(M("paused", active: false));
        var rec = new Recorder<CheckMessage>();
        var stage = new SchedulerStage(monitors, new FakeIdempotency(), rec, NullLogger<SchedulerStage>.Instance);

        await stage.HandleAsync(new SchedulerTick(Minute), default);

        Assert.Equal(["m1", "m2"], rec.Messages.Select(m => m.MonitorId).Order());
        var msg = rec.Messages.First();
        Assert.Equal(("https://x.io/" + msg.MonitorId, "GET", 200, "kw", 7), (msg.Url, msg.Method, msg.ExpectedStatus, msg.Keyword, msg.TimeoutSeconds));
        Assert.Equal(Minute, msg.Minute);
    }

    [Fact]
    public async Task SameMinuteTwice_PublishesOnce()
    {
        var monitors = new FakeMonitors();
        await monitors.CreateAsync(M("m1"));
        var rec = new Recorder<CheckMessage>();
        var stage = new SchedulerStage(monitors, new FakeIdempotency(), rec, NullLogger<SchedulerStage>.Instance);

        await stage.HandleAsync(new SchedulerTick(Minute), default);
        await stage.HandleAsync(new SchedulerTick(Minute), default);
        Assert.Single(rec.Messages);

        await stage.HandleAsync(new SchedulerTick(Minute.AddMinutes(1)), default);
        Assert.Equal(2, rec.Messages.Count);
    }

    [Fact]
    public async Task PublishFailure_ReleasesKey_SoARetryCanSucceed()
    {
        var monitors = new FakeMonitors();
        await monitors.CreateAsync(M("m1"));
        var idem = new FakeIdempotency();
        var rec = new Recorder<CheckMessage> { FailWith = new InvalidOperationException("sqs down") };
        var stage = new SchedulerStage(monitors, idem, rec, NullLogger<SchedulerStage>.Instance);

        await Assert.ThrowsAsync<AggregateException>(() => stage.HandleAsync(new SchedulerTick(Minute), default));
        Assert.Empty(idem.Keys);

        rec.FailWith = null;
        await stage.HandleAsync(new SchedulerTick(Minute), default);
        Assert.Single(rec.Messages);
    }

    [Fact]
    public void IdempotencyKey_IsMonitorPlusMinute()
    {
        var m = new CheckMessage("a", "mon1", Minute, "https://x", "GET", 200, null, 5);
        Assert.Equal("mon1#202601011000", m.IdempotencyKey);
    }
}

public class EvaluatorStageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private sealed class Ctx
    {
        public FakeMonitors Monitors { get; } = new();
        public FakeResults Results { get; } = new();
        public FakeAggregates Aggregates { get; } = new();
        public FakeIncidents Incidents { get; } = new();
        public FakeIdempotency Idem { get; } = new();
        public Recorder<IncidentEvent> Events { get; } = new();
        public EvaluatorStage Stage { get; }

        public Ctx()
        {
            Monitors.Items["m"] = new UptimeMonitor("a", "m", "Site", "https://x.io", "GET", 1, 200, null, 5, true, T0);
            Stage = new EvaluatorStage(Monitors, Results, Aggregates, Incidents, Idem, Events, NullLogger<EvaluatorStage>.Instance);
        }

        public Task Feed(int minute, bool ok, ErrorKind e = ErrorKind.None, int? status = 200) =>
            Stage.HandleAsync(new CheckResultMessage("a", "m", T0.AddMinutes(minute), T0.AddMinutes(minute).AddSeconds(1), ok, ok ? 200 : status,
                120, ok ? ErrorKind.None : e, ok ? null : "boom"), default);
    }

    [Fact]
    public async Task StoresResultAndAggregate()
    {
        var c = new Ctx();
        await c.Feed(0, true);
        var r = Assert.Single(c.Results.Items);
        Assert.True(r.Success);
        Assert.Equal(120, r.LatencyMs);
        var agg = Assert.Single(c.Aggregates.Records);
        Assert.Equal((true, 120), (agg.Success, agg.Latency));
        Assert.Equal(MonitorStatus.Up, c.Monitors.Items["m"].Status);
    }

    [Fact]
    public async Task OneFailure_NoIncident()
    {
        var c = new Ctx();
        await c.Feed(0, false, ErrorKind.Timeout, null);
        Assert.Empty(c.Incidents.Items);
        Assert.Empty(c.Events.Messages);
    }

    [Fact]
    public async Task TwoFailures_OpenIncident_AndEmitEvent()
    {
        var c = new Ctx();
        await c.Feed(0, false, ErrorKind.Timeout, null);
        await c.Feed(1, false, ErrorKind.Timeout, null);

        var inc = Assert.Single(c.Incidents.Items.Values);
        Assert.True(inc.IsOpen);
        Assert.Equal("Timeout", inc.Cause);
        Assert.Equal(inc.IncidentId, c.Monitors.Items["m"].OpenIncidentId);
        Assert.Equal(MonitorStatus.Down, c.Monitors.Items["m"].Status);
        var e = Assert.Single(c.Events.Messages);
        Assert.Equal(IncidentEventType.Opened, e.Type);
        Assert.Equal("Site", e.MonitorName);
    }

    [Fact]
    public async Task LongOutage_OpensExactlyOneIncident()
    {
        var c = new Ctx();
        for (var i = 0; i < 6; i++) await c.Feed(i, false, ErrorKind.UnexpectedStatus, 503);
        Assert.Single(c.Incidents.Items);
        Assert.Single(c.Events.Messages);
        Assert.Equal("Unexpected status code 503", c.Incidents.Items.Values.Single().Cause);
    }

    [Fact]
    public async Task TwoSuccesses_ResolveIncident_WithDuration()
    {
        var c = new Ctx();
        await c.Feed(0, false, ErrorKind.Timeout, null);
        await c.Feed(1, false, ErrorKind.Timeout, null);
        await c.Feed(5, true);
        Assert.Single(c.Events.Messages); // not resolved yet
        await c.Feed(6, true);

        Assert.Equal(2, c.Events.Messages.Count);
        var e = c.Events.Messages[1];
        Assert.Equal(IncidentEventType.Resolved, e.Type);
        Assert.Equal(TimeSpan.FromMinutes(5), e.Duration);
        Assert.False(c.Incidents.Items.Values.Single().IsOpen);
        Assert.Null(c.Monitors.Items["m"].OpenIncidentId);
        Assert.Equal(MonitorStatus.Up, c.Monitors.Items["m"].Status);
    }

    [Fact]
    public async Task DuplicateDelivery_IsIgnored()
    {
        var c = new Ctx();
        await c.Feed(0, false, ErrorKind.Timeout, null);
        await c.Feed(0, false, ErrorKind.Timeout, null); // same minute redelivered by SQS
        Assert.Single(c.Results.Items);
        Assert.Equal(1, c.Monitors.Items["m"].ConsecutiveFailures);
    }

    [Fact]
    public async Task DeletedMonitor_IsIgnored()
    {
        var c = new Ctx();
        c.Monitors.Items.Clear();
        await c.Feed(0, true);
        Assert.Empty(c.Results.Items);
    }

    [Fact]
    public async Task FailureWhilePublishing_ReleasesKey_AndRethrows()
    {
        var c = new Ctx();
        c.Events.FailWith = new InvalidOperationException("eventbridge down");
        await c.Feed(0, false, ErrorKind.Timeout, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Feed(1, false, ErrorKind.Timeout, null));
        Assert.DoesNotContain(c.Idem.Keys, k => k.EndsWith("202601011001", StringComparison.Ordinal));
    }
}

public class NotifierStageTests
{
    private static readonly IncidentEvent Opened = new("a", "m", "Mon site", "https://x.io", "inc1", IncidentEventType.Opened,
        new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), "Timeout", null);

    private static NotifierStage Build(FakeEmail email, FakeWebhook hook, NotifierOptions? options = null, params AlertSubscription[] subs) =>
        new(new FakeAlerts(subs), new AlertDispatcher(email, hook, options ?? new NotifierOptions([])), NullLogger<NotifierStage>.Instance);

    private static AlertSubscription Email(string id = "e1", bool active = true) => new("a", id, AlertType.Email, "me@x.io", null, active);
    private static AlertSubscription Hook(string id = "w1") => new("a", id, AlertType.Webhook, "https://hook.example/x", "s3cret", true);

    [Fact]
    public async Task SendsEmailAndWebhook_ForOpenedIncident()
    {
        var email = new FakeEmail();
        var hook = new FakeWebhook();
        await Build(email, hook, null, Email(), Hook()).HandleAsync(Opened, default);

        var mail = Assert.Single(email.Sent);
        Assert.Equal("me@x.io", mail.To);
        Assert.Contains("Mon site", mail.Subject);
        Assert.Contains("Timeout", mail.Body);
        var h = Assert.Single(hook.Sent);
        Assert.Equal("s3cret", h.Secret);
        Assert.Contains("\"event\":\"incident.opened\"", h.Body);
        Assert.Contains("\"monitorId\":\"m\"", h.Body);
    }

    [Fact]
    public async Task ResolvedEvent_UsesResolvedWording()
    {
        var email = new FakeEmail();
        var resolved = Opened with { Type = IncidentEventType.Resolved, Duration = TimeSpan.FromSeconds(185) };
        await Build(email, new FakeWebhook(), null, Email()).HandleAsync(resolved, default);
        Assert.Contains("retabli", email.Sent[0].Subject);
        Assert.Contains("3 min 5 s", email.Sent[0].Body);
    }

    [Fact]
    public async Task InactiveSubscriptions_AreSkipped()
    {
        var email = new FakeEmail();
        await Build(email, new FakeWebhook(), null, Email(active: false)).HandleAsync(Opened, default);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task OtherAccountsSubscriptions_AreNotNotified()
    {
        var email = new FakeEmail();
        var other = new AlertSubscription("someone-else", "x", AlertType.Email, "evil@x.io", null, true);
        await Build(email, new FakeWebhook(), null, other).HandleAsync(Opened, default);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task TransientFailure_IsRetried_WithGrowingDelays()
    {
        var email = new FakeEmail { FailFirst = 2 };
        var options = new NotifierOptions([TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
        await Build(email, new FakeWebhook(), options, Email()).HandleAsync(Opened, default);
        Assert.Equal(3, email.Calls);
        Assert.Single(email.Sent);
    }

    [Fact]
    public async Task OneChannelFailing_DoesNotBlockTheOther()
    {
        var email = new FakeEmail();
        var hook = new FakeWebhook { AlwaysFail = true };
        await Build(email, hook, new NotifierOptions([TimeSpan.Zero]), Email(), Hook()).HandleAsync(Opened, default);
        Assert.Single(email.Sent);
        Assert.Equal(2, hook.Calls); // 1 try + 1 retry
    }

    [Fact]
    public async Task EverythingFailing_Throws_SoTheQueueCanDeadLetter()
    {
        var hook = new FakeWebhook { AlwaysFail = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Build(new FakeEmail(), hook, new NotifierOptions([TimeSpan.Zero]), Hook()).HandleAsync(Opened, default));
    }

    [Fact]
    public async Task NoSubscriptions_IsANoOp()
    {
        await Build(new FakeEmail(), new FakeWebhook()).HandleAsync(Opened, default);
    }

    [Fact]
    public async Task RetryPolicy_StopsAfterConfiguredAttempts()
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => RetryPolicy.RunAsync(_ =>
        {
            calls++;
            throw new InvalidOperationException();
        }, [TimeSpan.Zero, TimeSpan.Zero], default));
        Assert.Equal(3, calls);
    }
}
