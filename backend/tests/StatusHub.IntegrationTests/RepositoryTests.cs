using StatusHub.Domain;
using StatusHub.Infrastructure.Dynamo;

namespace StatusHub.IntegrationTests;

[Collection("dynamo")]
public class RepositoryTests(DynamoFixture fx)
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static UptimeMonitor NewMonitor(string acc, string id, int interval = 1, bool active = true) =>
        new(acc, id, "Site " + id, "https://example.com/" + id, "GET", interval, 200, null, 10, active, Now);

    [Fact]
    public async Task Account_CreateAndLookupBySlug_EnforcesUniqueSlug()
    {
        var (o, _) = await fx.NewTableAsync();
        var repo = new AccountRepository(fx.Client, o);
        Assert.True(await repo.CreateAsync(new Account("u1", "a@x.io", "acme", Now)));
        Assert.False(await repo.CreateAsync(new Account("u2", "b@x.io", "acme", Now)));
        Assert.Equal("u1", (await repo.GetBySlugAsync("acme"))!.UserId);
        Assert.Null(await repo.GetBySlugAsync("nope"));
        Assert.Equal("a@x.io", (await repo.GetAsync("u1"))!.Email);
    }

    [Fact]
    public async Task Account_ChangeSlug_MovesReservation()
    {
        var (o, _) = await fx.NewTableAsync();
        var repo = new AccountRepository(fx.Client, o);
        await repo.CreateAsync(new Account("u1", "a@x.io", "acme", Now));
        await repo.CreateAsync(new Account("u2", "b@x.io", "taken", Now));
        Assert.False(await repo.ChangeSlugAsync("u1", "taken"));
        Assert.True(await repo.ChangeSlugAsync("u1", "acme2"));
        Assert.Null(await repo.GetBySlugAsync("acme"));
        Assert.Equal("u1", (await repo.GetBySlugAsync("acme2"))!.UserId);
        Assert.Equal("acme2", (await repo.GetAsync("u1"))!.Slug);
    }

    [Fact]
    public async Task Monitor_Crud_RoundTripsAllFields_AndIsScopedByAccount()
    {
        var (o, _) = await fx.NewTableAsync();
        var repo = new MonitorRepository(fx.Client, o);
        var m = NewMonitor("u1", "m1") with { Keyword = "ok", Status = MonitorStatus.Down, ConsecutiveFailures = 2, OpenIncidentId = "inc", LastCheckedAt = Now };
        await repo.CreateAsync(m);
        await repo.CreateAsync(NewMonitor("u2", "m2"));

        Assert.Equal(m, await repo.GetAsync("u1", "m1"));
        Assert.Null(await repo.GetAsync("u2", "m1"));
        Assert.Single(await repo.ListAsync("u1"));

        await repo.UpdateAsync(m with { Name = "renamed" });
        Assert.Equal("renamed", (await repo.GetAsync("u1", "m1"))!.Name);

        await repo.DeleteAsync("u1", "m1");
        Assert.Empty(await repo.ListAsync("u1"));
    }

    [Fact]
    public async Task Monitor_GetDue_ReturnsOnlyActiveMonitorsDueAtThatMinute()
    {
        var (o, _) = await fx.NewTableAsync();
        var repo = new MonitorRepository(fx.Client, o);
        await repo.CreateAsync(NewMonitor("u1", "every1", 1));
        await repo.CreateAsync(NewMonitor("u1", "paused", 1, active: false));
        for (var i = 0; i < 5; i++) await repo.CreateAsync(NewMonitor("u1", "m5-" + i, 5));

        // Over 5 consecutive minutes: the 1-min monitor runs every minute, each 5-min monitor exactly once.
        var counts = new Dictionary<string, int>();
        for (var k = 0; k < 5; k++)
            foreach (var m in await repo.GetDueAsync(Now.AddMinutes(k)))
                counts[m.MonitorId] = counts.GetValueOrDefault(m.MonitorId) + 1;

        Assert.Equal(5, counts["every1"]);
        Assert.DoesNotContain("paused", counts.Keys);
        for (var i = 0; i < 5; i++) Assert.Equal(1, counts["m5-" + i]);
    }

    [Fact]
    public async Task Results_AreStoredWithTtl_AndQueryableByRange()
    {
        var (o, table) = await fx.NewTableAsync();
        var repo = new ResultRepository(fx.Client, o);
        await repo.AddAsync(new CheckResult("m1", Now, true, 200, 120, ErrorKind.None, null));
        await repo.AddAsync(new CheckResult("m1", Now.AddMinutes(1), false, null, 10000, ErrorKind.Timeout, "timed out"));
        await repo.AddAsync(new CheckResult("m1", Now.AddHours(5), true, 200, 90, ErrorKind.None, null));

        var range = await repo.GetRangeAsync("m1", Now, Now.AddMinutes(10));
        Assert.Equal(2, range.Count);
        Assert.Equal(ErrorKind.Timeout, range[1].Error);
        Assert.Null(range[1].StatusCode);

        var raw = await fx.Client.GetItemAsync(table, new Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue> { ["PK"] = new() { S = "MON#m1" }, ["SK"] = new() { S = Keys.ResultSk(Now) } });
        Assert.Equal((Now + TimeSpan.FromDays(30)).ToUnixTimeSeconds(), long.Parse(raw.Item["ttl"].N!));
    }

    [Fact]
    public async Task Incidents_OpenListResolve_AndOpenIndexIsSparse()
    {
        var (o, _) = await fx.NewTableAsync();
        var repo = new IncidentRepository(fx.Client, o);
        var a = new Incident("u1", "m1", Incident.NewId(Now), Now, null, "Timeout");
        var b = new Incident("u2", "m9", Incident.NewId(Now), Now, null, "DNS");
        await repo.OpenAsync(a);
        await repo.OpenAsync(b);

        Assert.Equal(a.IncidentId, Assert.Single(await repo.ListOpenForAccountAsync("u1")).IncidentId);

        await repo.ResolveAsync("m1", a.IncidentId, Now.AddMinutes(7));
        Assert.Empty(await repo.ListOpenForAccountAsync("u1"));
        var resolved = (await repo.GetAsync("m1", a.IncidentId))!;
        Assert.Equal(TimeSpan.FromMinutes(7), resolved.Duration);

        var c = new Incident("u1", "m1", Incident.NewId(Now.AddHours(1)), Now.AddHours(1), null, "x");
        await repo.OpenAsync(c);
        var list = await repo.ListForMonitorAsync("m1", 10);
        Assert.Equal([c.IncidentId, a.IncidentId], list.Select(i => i.IncidentId)); // newest first
    }

    [Fact]
    public async Task Aggregates_AreIncrementedAtomically_AndPercentilesComputed()
    {
        var (o, _) = await fx.NewTableAsync();
        var repo = new AggregateRepository(fx.Client, o);
        var day = DateOnly.FromDateTime(Now.UtcDateTime);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => repo.RecordAsync("m1", day, true, 80)));
        await repo.RecordAsync("m1", day, false, 4000);

        var agg = Assert.Single(await repo.GetRangeAsync("m1", day, day));
        Assert.Equal(21, agg.Total);
        Assert.Equal(20, agg.Successes);
        Assert.Equal(20 * 80 + 4000, agg.SumLatencyMs);
        Assert.Equal(100, agg.P50);
        Assert.Equal(100, agg.P95);
        Assert.InRange(agg.Availability, 95.2, 95.3);
    }

    [Fact]
    public async Task Alerts_Crud_ScopedByAccount()
    {
        var (o, _) = await fx.NewTableAsync();
        var repo = new AlertRepository(fx.Client, o);
        var s = new AlertSubscription("u1", "s1", AlertType.Webhook, "https://hook.example/x", "sec", true);
        await repo.CreateAsync(s);
        await repo.CreateAsync(new AlertSubscription("u2", "s2", AlertType.Email, "a@b.c", null, true));

        Assert.Equal(s, await repo.GetAsync("u1", "s1"));
        Assert.Null(await repo.GetAsync("u2", "s1"));
        Assert.Single(await repo.ListAsync("u1"));
        await repo.UpdateAsync(s with { Active = false });
        Assert.False((await repo.GetAsync("u1", "s1"))!.Active);
        await repo.DeleteAsync("u1", "s1");
        Assert.Empty(await repo.ListAsync("u1"));
    }

    [Fact]
    public async Task Idempotency_SecondAcquireOfSameKeyFails()
    {
        var (o, _) = await fx.NewTableAsync();
        var store = new IdempotencyStore(fx.Client, o, TimeProvider.System);
        Assert.True(await store.TryAcquireAsync("m1#202603101200", TimeSpan.FromHours(1)));
        Assert.False(await store.TryAcquireAsync("m1#202603101200", TimeSpan.FromHours(1)));
        Assert.True(await store.TryAcquireAsync("m1#202603101201", TimeSpan.FromHours(1)));
    }
}

