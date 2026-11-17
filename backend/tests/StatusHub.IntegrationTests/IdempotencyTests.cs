using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using StatusHub.Application;
using StatusHub.Domain;
using StatusHub.Infrastructure.Dynamo;

namespace StatusHub.IntegrationTests;

/// <summary>
/// The real stages on the real repositories (DynamoDB Local), with in-memory queues that can deliver a message twice,
/// like SQS (at-least-once) or an EventBridge Scheduler retry would.
/// </summary>
[Collection("dynamo")]
public class IdempotencyTests(DynamoFixture fx)
{
    private static readonly DateTimeOffset Minute = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed class Queue<T> : IMessagePublisher<T>
    {
        public List<T> Messages { get; } = [];
        public Task PublishAsync(T message, CancellationToken ct) { lock (Messages) Messages.Add(message); return Task.CompletedTask; }
    }

    private sealed class Chain
    {
        public required MonitorRepository Monitors { get; init; }
        public required ResultRepository Results { get; init; }
        public required IncidentRepository Incidents { get; init; }
        public required AggregateRepository Aggregates { get; init; }
        public required SchedulerStage Scheduler { get; init; }
        public required EvaluatorStage Evaluator { get; init; }
        public required Queue<CheckMessage> Checks { get; init; }
        public required Queue<IncidentEvent> Events { get; init; }
        public required Func<DateTimeOffset, int, CheckerStage> Checker { get; init; }
    }

    private async Task<Chain> BuildAsync()
    {
        var (o, _) = await fx.NewTableAsync();
        var monitors = new MonitorRepository(fx.Client, o);
        var results = new ResultRepository(fx.Client, o);
        var incidents = new IncidentRepository(fx.Client, o);
        var aggregates = new AggregateRepository(fx.Client, o);
        var idem = new IdempotencyStore(fx.Client, o, TimeProvider.System);
        var checks = new Queue<CheckMessage>();
        var events = new Queue<IncidentEvent>();
        var resultQueue = new Queue<CheckResultMessage>();
        var metrics = new NullStatusMetrics();

        await monitors.CreateAsync(new UptimeMonitor("acc", "mon", "Site", "https://example.com", "GET", 1, 200, null, 5, true, Minute));

        return new Chain
        {
            Monitors = monitors, Results = results, Incidents = incidents, Aggregates = aggregates, Checks = checks, Events = events,
            Scheduler = new SchedulerStage(monitors, idem, checks, metrics, NullLogger<SchedulerStage>.Instance),
            Evaluator = new EvaluatorStage(monitors, results, aggregates, incidents, idem, events, metrics, NullLogger<EvaluatorStage>.Instance),
            Checker = (minute, status) => new CheckerStage(
                new HttpClient(new StubHttp((HttpStatusCode)status)), resultQueue, new FixedClock(minute.AddSeconds(2)),
                new CheckerOptions(), metrics, NullLogger<CheckerStage>.Instance),
        };
    }

    private sealed class StubHttp(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static async Task<CheckResultMessage> Check(Chain c, CheckMessage msg, int status)
    {
        return await c.Checker(msg.Minute, status).CheckAsync(msg, default);
    }

    [Fact]
    public async Task SchedulerInvokedTwiceForTheSameMinute_PublishesOneCheck()
    {
        var c = await BuildAsync();
        await c.Scheduler.HandleAsync(new SchedulerTick(Minute), default);
        await c.Scheduler.HandleAsync(new SchedulerTick(Minute), default);
        Assert.Single(c.Checks.Messages);

        await c.Scheduler.HandleAsync(new SchedulerTick(Minute.AddMinutes(1)), default);
        Assert.Equal(2, c.Checks.Messages.Count);
    }

    [Fact]
    public async Task SchedulerRunningConcurrently_StillPublishesOneCheck()
    {
        var c = await BuildAsync();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => c.Scheduler.HandleAsync(new SchedulerTick(Minute), default)));
        Assert.Single(c.Checks.Messages);
    }

    [Fact]
    public async Task ResultDeliveredTwiceByTheQueue_IsRecordedOnce()
    {
        var c = await BuildAsync();
        await c.Scheduler.HandleAsync(new SchedulerTick(Minute), default);
        var result = await Check(c, c.Checks.Messages[0], 200);

        await c.Evaluator.HandleAsync(result, default);
        await c.Evaluator.HandleAsync(result, default); // SQS redelivery

        var stored = await c.Results.GetRangeAsync("mon", Minute.AddHours(-1), Minute.AddHours(1));
        Assert.Single(stored);
        var agg = Assert.Single(await c.Aggregates.GetRangeAsync("mon", DateOnly.FromDateTime(Minute.UtcDateTime), DateOnly.FromDateTime(Minute.UtcDateTime)));
        Assert.Equal(1, agg.Total);
        Assert.Equal(1, (await c.Monitors.GetAsync("acc", "mon"))!.ConsecutiveSuccesses);
    }

    [Fact]
    public async Task ResultProcessedConcurrently_IsRecordedExactlyOnce()
    {
        var c = await BuildAsync();
        await c.Scheduler.HandleAsync(new SchedulerTick(Minute), default);
        var result = await Check(c, c.Checks.Messages[0], 200);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => c.Evaluator.HandleAsync(result, default)));

        Assert.Single(await c.Results.GetRangeAsync("mon", Minute.AddHours(-1), Minute.AddHours(1)));
    }

    [Fact]
    public async Task OutageWithDuplicatedDeliveries_OpensOneIncidentAndSendsOneEvent_ThenResolvesOnce()
    {
        var c = await BuildAsync();

        async Task Deliver(int minute, int status, int copies)
        {
            var tick = Minute.AddMinutes(minute);
            await c.Scheduler.HandleAsync(new SchedulerTick(tick), default);
            await c.Scheduler.HandleAsync(new SchedulerTick(tick), default); // duplicated EventBridge Scheduler delivery
            var msg = c.Checks.Messages.Last(m => m.Minute == tick);
            var result = await Check(c, msg, status);
            for (var i = 0; i < copies; i++) await c.Evaluator.HandleAsync(result, default);
        }

        await Deliver(0, 503, 2);
        await Deliver(1, 503, 3); // second failure: incident opens, despite 3 deliveries
        await Deliver(2, 503, 2); // still down: no second incident
        await Deliver(3, 200, 2);
        await Deliver(4, 200, 2); // second success: resolves

        Assert.Equal(5, c.Checks.Messages.Count);
        Assert.Equal(5, (await c.Results.GetRangeAsync("mon", Minute.AddHours(-1), Minute.AddHours(1))).Count);
        var incident = Assert.Single(await c.Incidents.ListForMonitorAsync("mon", 10));
        Assert.False(incident.IsOpen);
        Assert.Equal([IncidentEventType.Opened, IncidentEventType.Resolved], c.Events.Messages.Select(e => e.Type));
        Assert.Null((await c.Monitors.GetAsync("acc", "mon"))!.OpenIncidentId);
    }
}
