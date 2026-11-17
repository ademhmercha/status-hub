using StatusHub.Application;
using StatusHub.Domain;

namespace StatusHub.UnitTests;

public sealed class Recorder<T> : IMessagePublisher<T>
{
    public List<T> Messages { get; } = [];
    public Exception? FailWith { get; set; }
    public Task PublishAsync(T message, CancellationToken ct)
    {
        if (FailWith is not null) throw FailWith;
        Messages.Add(message);
        return Task.CompletedTask;
    }
}

public sealed class FakeIdempotency : IIdempotencyStore
{
    public HashSet<string> Keys { get; } = [];
    public Task<bool> TryAcquireAsync(string key, TimeSpan ttl, CancellationToken ct = default) => Task.FromResult(Keys.Add(key));
    public Task ReleaseAsync(string key, CancellationToken ct = default) { Keys.Remove(key); return Task.CompletedTask; }
}

public sealed class FakeMonitors : IMonitorRepository
{
    public Dictionary<string, UptimeMonitor> Items { get; } = [];
    public Task CreateAsync(UptimeMonitor m, CancellationToken ct = default) { Items[m.MonitorId] = m; return Task.CompletedTask; }
    public Task<UptimeMonitor?> GetAsync(string a, string id, CancellationToken ct = default) =>
        Task.FromResult(Items.TryGetValue(id, out var m) && m.AccountId == a ? m : null);
    public Task<IReadOnlyList<UptimeMonitor>> ListAsync(string a, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UptimeMonitor>>(Items.Values.Where(m => m.AccountId == a).ToList());
    public Task UpdateAsync(UptimeMonitor m, CancellationToken ct = default) { Items[m.MonitorId] = m; return Task.CompletedTask; }
    public Task DeleteAsync(string a, string id, CancellationToken ct = default) { Items.Remove(id); return Task.CompletedTask; }
    public Task<IReadOnlyList<UptimeMonitor>> GetDueAsync(DateTimeOffset minute, CancellationToken ct = default)
    {
        var keys = UptimeMonitor.DueKeysAt(minute).ToHashSet();
        return Task.FromResult<IReadOnlyList<UptimeMonitor>>(Items.Values.Where(m => m.Active && keys.Contains(m.DueKey)).ToList());
    }
}

public sealed class FakeResults : IResultRepository
{
    public List<CheckResult> Items { get; } = [];
    public Task AddAsync(CheckResult r, CancellationToken ct = default) { Items.Add(r); return Task.CompletedTask; }
    public Task<IReadOnlyList<CheckResult>> GetRangeAsync(string id, DateTimeOffset f, DateTimeOffset t, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CheckResult>>(Items.Where(r => r.MonitorId == id && r.Timestamp >= f && r.Timestamp <= t).ToList());
}

public sealed class FakeAggregates : IAggregateRepository
{
    public List<(string Monitor, DateOnly Day, bool Success, int Latency)> Records { get; } = [];
    public Task RecordAsync(string m, DateOnly d, bool s, int l, CancellationToken ct = default) { Records.Add((m, d, s, l)); return Task.CompletedTask; }
    public Task<IReadOnlyList<DailyAggregate>> GetRangeAsync(string m, DateOnly f, DateOnly t, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DailyAggregate>>([]);
}

public sealed class FakeIncidents : IIncidentRepository
{
    public Dictionary<string, Incident> Items { get; } = [];
    public Task OpenAsync(Incident i, CancellationToken ct = default) { Items[i.IncidentId] = i; return Task.CompletedTask; }
    public Task ResolveAsync(string m, string id, DateTimeOffset at, CancellationToken ct = default) { Items[id] = Items[id] with { ResolvedAt = at }; return Task.CompletedTask; }
    public Task<Incident?> GetAsync(string m, string id, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(id));
    public Task<IReadOnlyList<Incident>> ListForMonitorAsync(string m, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Incident>>(Items.Values.Where(i => i.MonitorId == m).OrderByDescending(i => i.OpenedAt).Take(limit).ToList());
    public Task<IReadOnlyList<Incident>> ListOpenForAccountAsync(string a, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Incident>>(Items.Values.Where(i => i.AccountId == a && i.IsOpen).ToList());
}

public sealed class FakeAlerts(params AlertSubscription[] subs) : IAlertRepository
{
    public Task CreateAsync(AlertSubscription s, CancellationToken ct = default) => Task.CompletedTask;
    public Task<AlertSubscription?> GetAsync(string a, string id, CancellationToken ct = default) => Task.FromResult(subs.FirstOrDefault(s => s.SubscriptionId == id));
    public Task<IReadOnlyList<AlertSubscription>> ListAsync(string a, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AlertSubscription>>(subs.Where(s => s.AccountId == a).ToList());
    public Task UpdateAsync(AlertSubscription s, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteAsync(string a, string id, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FakeEmail : IEmailSender
{
    public List<(string To, string Subject, string Body)> Sent { get; } = [];
    public int FailFirst { get; set; }
    public int Calls { get; private set; }
    public Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        Calls++;
        if (Calls <= FailFirst) throw new InvalidOperationException("smtp down");
        Sent.Add((to, subject, body));
        return Task.CompletedTask;
    }
}

public sealed class FakeWebhook : IWebhookSender
{
    public List<(string Url, string? Secret, string Body)> Sent { get; } = [];
    public bool AlwaysFail { get; set; }
    public int Calls { get; private set; }
    public Task SendAsync(string url, string? secret, string body, CancellationToken ct)
    {
        Calls++;
        if (AlwaysFail) throw new HttpRequestException("500");
        Sent.Add((url, secret, body));
        return Task.CompletedTask;
    }
}

public sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fn) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => fn(request, ct);
    public static StubHandler Returning(int status, string body = "") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent(body) }));
    public static StubHandler Throwing(Exception ex) => new((_, _) => throw ex);
}

public sealed class RecordingMetrics : StatusHub.Application.IStatusMetrics
{
    public List<(string Name, double Value, string Unit)> Items { get; } = [];
    public void Count(string name, double value = 1) => Items.Add((name, value, "Count"));
    public void Milliseconds(string name, double value) => Items.Add((name, value, "Milliseconds"));
    public void Seconds(string name, double value) => Items.Add((name, value, "Seconds"));
    public double Sum(string name) => Items.Where(i => i.Name == name).Sum(i => i.Value);
    public bool Has(string name) => Items.Any(i => i.Name == name);
}
