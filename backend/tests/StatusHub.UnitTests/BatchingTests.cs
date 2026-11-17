using Amazon.Lambda.SQSEvents;
using Microsoft.Extensions.Logging.Abstractions;
using StatusHub.Application;
using StatusHub.Domain;
using StatusHub.Infrastructure;

namespace StatusHub.UnitTests;

public class BatchingTests
{
    private static readonly DateTimeOffset Minute = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private sealed class CountingPublisher<T> : IMessagePublisher<T>
    {
        public int SingleCalls, BatchCalls;
        public List<T> Sent { get; } = [];
        public Func<T, bool> Fails { get; set; } = _ => false;
        public Task PublishAsync(T m, CancellationToken ct) { SingleCalls++; Sent.Add(m); return Task.CompletedTask; }
        public Task<IReadOnlyList<T>> PublishManyAsync(IReadOnlyList<T> messages, CancellationToken ct)
        {
            BatchCalls++;
            Sent.AddRange(messages.Where(m => !Fails(m)));
            return Task.FromResult<IReadOnlyList<T>>(messages.Where(Fails).ToList());
        }
    }

    [Fact]
    public async Task Scheduler_PublishesInBatchesOfTen()
    {
        var monitors = new FakeMonitors();
        for (var i = 0; i < 25; i++)
            await monitors.CreateAsync(new UptimeMonitor("a", $"m{i:D2}", "n", "https://x.io", "GET", 1, 200, null, 5, true, Minute));
        var pub = new CountingPublisher<CheckMessage>();
        var metrics = new RecordingMetrics();
        var stage = new SchedulerStage(monitors, new FakeIdempotency(), pub, metrics, NullLogger<SchedulerStage>.Instance);

        await stage.HandleAsync(new SchedulerTick(Minute), default);

        Assert.Equal(25, pub.Sent.Count);
        Assert.Equal(3, pub.BatchCalls); // 10 + 10 + 5 instead of 25 requests
        Assert.Equal(0, pub.SingleCalls);
        Assert.Equal(25, metrics.Sum(MetricNames.ChecksScheduled));
    }

    [Fact]
    public async Task Scheduler_ReleasesOnlyTheKeysOfMessagesThatFailed()
    {
        var monitors = new FakeMonitors();
        for (var i = 0; i < 4; i++)
            await monitors.CreateAsync(new UptimeMonitor("a", $"m{i}", "n", "https://x.io", "GET", 1, 200, null, 5, true, Minute));
        var idem = new FakeIdempotency();
        var pub = new CountingPublisher<CheckMessage> { Fails = m => m.MonitorId == "m2" };
        var metrics = new RecordingMetrics();
        var stage = new SchedulerStage(monitors, idem, pub, metrics, NullLogger<SchedulerStage>.Instance);

        await Assert.ThrowsAsync<AggregateException>(() => stage.HandleAsync(new SchedulerTick(Minute), default));

        Assert.Equal(3, pub.Sent.Count);
        Assert.Equal(3, idem.Keys.Count); // sent ones stay protected against duplicates
        Assert.DoesNotContain(idem.Keys, k => k.StartsWith("m2#", StringComparison.Ordinal)); // the failed one can be retried
        Assert.Equal(3, metrics.Sum(MetricNames.ChecksScheduled)); // the failed one is not counted until it is really published
    }

    [Fact]
    public async Task BufferedPublisher_SendsEverythingAtFlush_InChunksOfTen()
    {
        var inner = new CountingPublisher<int>();
        var buffered = new BufferedPublisher<int>(inner);
        for (var i = 0; i < 23; i++) await buffered.PublishAsync(i, default);
        Assert.Empty(inner.Sent); // nothing is sent during the invocation

        await buffered.FlushAsync(default);
        Assert.Equal(23, inner.Sent.Count);
        Assert.Equal(3, inner.BatchCalls);

        await buffered.FlushAsync(default); // nothing left
        Assert.Equal(3, inner.BatchCalls);
    }

    [Fact]
    public async Task BufferedPublisher_ThrowsWhenSomeMessagesCannotBeSent()
    {
        var inner = new CountingPublisher<int> { Fails = m => m == 3 };
        var buffered = new BufferedPublisher<int>(inner);
        for (var i = 0; i < 5; i++) await buffered.PublishAsync(i, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => buffered.FlushAsync(default));
    }

    private sealed class RecordingStage : IStage<CheckMessage>
    {
        public int Running, MaxRunning;
        public HashSet<string> FailFor { get; } = [];

        public async Task HandleAsync(CheckMessage m, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref Running);
            int seen;
            while ((seen = MaxRunning) < now && Interlocked.CompareExchange(ref MaxRunning, now, seen) != seen) { }
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref Running);
            if (FailFor.Contains(m.MonitorId)) throw new InvalidOperationException("boom");
        }
    }

    private static SQSEvent Event(params string[] monitorIds) => new()
    {
        Records = monitorIds.Select(id => new SQSEvent.SQSMessage
        {
            MessageId = "msg-" + id,
            Body = System.Text.Json.JsonSerializer.Serialize(new CheckMessage("a", id, Minute, "https://x.io", "GET", 200, null, 5), StatusHubJson.Default.CheckMessage),
        }).ToList(),
    };

    [Fact]
    public async Task SqsBatch_ReportsOnlyFailedRecords_AndRunsInParallelOnDemand()
    {
        var stage = new RecordingStage { FailFor = { "b" } };
        var res = await SqsBatch.ProcessAsync(Event("a", "b", "c", "d"), stage, StatusHubJson.Default.CheckMessage, default, parallel: true);

        Assert.Equal(["msg-b"], res.BatchItemFailures.Select(f => f.ItemIdentifier));
        Assert.True(stage.MaxRunning > 1, "records should run concurrently");

        var sequential = new RecordingStage();
        await SqsBatch.ProcessAsync(Event("a", "b", "c"), sequential, StatusHubJson.Default.CheckMessage, default);
        Assert.Equal(1, sequential.MaxRunning);
    }

    [Fact]
    public async Task SqsBatch_FailsTheWholeBatch_WhenTheFlushFails()
    {
        var res = await SqsBatch.ProcessAsync(Event("a", "b"), new RecordingStage(), StatusHubJson.Default.CheckMessage, default,
            afterAll: _ => throw new InvalidOperationException("sqs down"));
        Assert.Equal(2, res.BatchItemFailures.Count);
    }

    [Fact]
    public async Task SqsBatch_RejectsInvalidMessages_WithoutBlockingOthers()
    {
        var evt = Event("a");
        evt.Records.Add(new SQSEvent.SQSMessage { MessageId = "bad", Body = "not json" });
        var res = await SqsBatch.ProcessAsync(evt, new RecordingStage(), StatusHubJson.Default.CheckMessage, default);
        Assert.Equal(["bad"], res.BatchItemFailures.Select(f => f.ItemIdentifier));
    }

    private sealed class CapturingStage(List<string> seen) : IStage<CheckMessage>
    {
        public Task HandleAsync(CheckMessage m, CancellationToken ct) { seen.Add(m.MonitorId); return Task.CompletedTask; }
    }

    [Fact]
    public async Task SqsBatch_UnwrapsTheEventBridgeEnvelope()
    {
        var detail = System.Text.Json.JsonSerializer.Serialize(new CheckMessage("a", "m1", Minute, "https://x.io", "GET", 200, null, 5), StatusHubJson.Default.CheckMessage);
        var evt = new SQSEvent
        {
            Records = [new SQSEvent.SQSMessage { MessageId = "e1", Body = "{\"source\":\"statushub\",\"detail-type\":\"IncidentOpened\",\"detail\":" + detail + "}" }],
        };
        var seen = new List<string>();
        var res = await SqsBatch.ProcessAsync(evt, new CapturingStage(seen), StatusHubJson.Default.CheckMessage, default, unwrapDetail: true);
        Assert.Empty(res.BatchItemFailures);
        Assert.Equal(["m1"], seen);
    }
}
