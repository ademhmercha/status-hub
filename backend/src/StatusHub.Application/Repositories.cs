using StatusHub.Domain;

namespace StatusHub.Application;

public interface IAccountRepository
{
    Task<Account?> GetAsync(string userId, CancellationToken ct = default);
    Task<Account?> GetBySlugAsync(string slug, CancellationToken ct = default);
    /// <summary>Creates the account and reserves its slug atomically. Returns false if the slug or the account already exists.</summary>
    Task<bool> CreateAsync(Account account, CancellationToken ct = default);
    /// <summary>Moves the slug atomically. Returns false if the new slug is taken.</summary>
    Task<bool> ChangeSlugAsync(string userId, string newSlug, CancellationToken ct = default);
}

public interface IMonitorRepository
{
    Task CreateAsync(UptimeMonitor monitor, CancellationToken ct = default);
    Task<UptimeMonitor?> GetAsync(string accountId, string monitorId, CancellationToken ct = default);
    Task<IReadOnlyList<UptimeMonitor>> ListAsync(string accountId, CancellationToken ct = default);
    Task UpdateAsync(UptimeMonitor monitor, CancellationToken ct = default);
    Task DeleteAsync(string accountId, string monitorId, CancellationToken ct = default);
    /// <summary>Active monitors that must be checked at the given minute.</summary>
    Task<IReadOnlyList<UptimeMonitor>> GetDueAsync(DateTimeOffset minute, CancellationToken ct = default);
}

public interface IResultRepository
{
    Task AddAsync(CheckResult result, CancellationToken ct = default);
    Task<IReadOnlyList<CheckResult>> GetRangeAsync(string monitorId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}

public interface IIncidentRepository
{
    Task OpenAsync(Incident incident, CancellationToken ct = default);
    Task ResolveAsync(string monitorId, string incidentId, DateTimeOffset resolvedAt, CancellationToken ct = default);
    Task<Incident?> GetAsync(string monitorId, string incidentId, CancellationToken ct = default);
    /// <summary>Most recent first.</summary>
    Task<IReadOnlyList<Incident>> ListForMonitorAsync(string monitorId, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<Incident>> ListOpenForAccountAsync(string accountId, CancellationToken ct = default);
}

public interface IAggregateRepository
{
    /// <summary>Atomic increment (no read-modify-write).</summary>
    Task RecordAsync(string monitorId, DateOnly date, bool success, int latencyMs, CancellationToken ct = default);
    Task<IReadOnlyList<DailyAggregate>> GetRangeAsync(string monitorId, DateOnly from, DateOnly to, CancellationToken ct = default);
}

public interface IAlertRepository
{
    Task CreateAsync(AlertSubscription subscription, CancellationToken ct = default);
    Task<AlertSubscription?> GetAsync(string accountId, string subscriptionId, CancellationToken ct = default);
    Task<IReadOnlyList<AlertSubscription>> ListAsync(string accountId, CancellationToken ct = default);
    Task UpdateAsync(AlertSubscription subscription, CancellationToken ct = default);
    Task DeleteAsync(string accountId, string subscriptionId, CancellationToken ct = default);
}

public interface IIdempotencyStore
{
    /// <summary>True the first time a key is seen (within the TTL), false afterwards.</summary>
    Task<bool> TryAcquireAsync(string key, TimeSpan ttl, CancellationToken ct = default);
}
