using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using StatusHub.Application;
using StatusHub.Domain;

namespace StatusHub.Infrastructure.Dynamo;

public sealed class AccountRepository(IAmazonDynamoDB db, DynamoOptions o) : IAccountRepository
{
    public async Task<Account?> GetAsync(string userId, CancellationToken ct = default)
    {
        var r = await db.GetItemAsync(o.TableName, Av.Key(Keys.User(userId), "PROFILE"), ct);
        return r.Item is { Count: > 0 } ? Map(r.Item) : null;
    }

    public async Task<Account?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        var r = await db.GetItemAsync(o.TableName, Av.Key(Keys.Slug(slug), "SLUG"), ct);
        return r.Item is { Count: > 0 } ? await GetAsync(r.Item.Str("userId"), ct) : null;
    }

    public async Task<bool> CreateAsync(Account a, CancellationToken ct = default)
    {
        var profile = Av.Key(Keys.User(a.UserId), "PROFILE");
        profile["type"] = Av.S("ACCOUNT");
        profile["userId"] = Av.S(a.UserId);
        profile["email"] = Av.S(a.Email);
        profile["slug"] = Av.S(a.Slug);
        profile["createdAt"] = Av.S(Av.Iso(a.CreatedAt));
        var slug = Av.Key(Keys.Slug(a.Slug), "SLUG");
        slug["userId"] = Av.S(a.UserId);
        try
        {
            await db.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new() { Put = new Put { TableName = o.TableName, Item = profile, ConditionExpression = "attribute_not_exists(PK)" } },
                    new() { Put = new Put { TableName = o.TableName, Item = slug, ConditionExpression = "attribute_not_exists(PK)" } },
                ],
            }, ct);
            return true;
        }
        catch (TransactionCanceledException) { return false; }
    }

    public async Task<bool> ChangeSlugAsync(string userId, string newSlug, CancellationToken ct = default)
    {
        var account = await GetAsync(userId, ct) ?? throw new InvalidOperationException("Account not found.");
        if (account.Slug == newSlug) return true;
        var slug = Av.Key(Keys.Slug(newSlug), "SLUG");
        slug["userId"] = Av.S(userId);
        try
        {
            await db.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new() { Put = new Put { TableName = o.TableName, Item = slug, ConditionExpression = "attribute_not_exists(PK)" } },
                    new() { Delete = new Delete { TableName = o.TableName, Key = Av.Key(Keys.Slug(account.Slug), "SLUG") } },
                    new()
                    {
                        Update = new Update
                        {
                            TableName = o.TableName,
                            Key = Av.Key(Keys.User(userId), "PROFILE"),
                            UpdateExpression = "SET slug = :s",
                            ExpressionAttributeValues = new() { [":s"] = Av.S(newSlug) },
                        },
                    },
                ],
            }, ct);
            return true;
        }
        catch (TransactionCanceledException) { return false; }
    }

    private static Account Map(Item i) => new(i.Str("userId"), i.Str("email"), i.Str("slug"), i.Ts("createdAt"));
}

public sealed class MonitorRepository(IAmazonDynamoDB db, DynamoOptions o) : IMonitorRepository
{
    public Task CreateAsync(UptimeMonitor m, CancellationToken ct = default) =>
        db.PutItemAsync(o.TableName, ToItem(m), ct);

    public Task UpdateAsync(UptimeMonitor m, CancellationToken ct = default) =>
        db.PutItemAsync(new PutItemRequest
        {
            TableName = o.TableName,
            Item = ToItem(m),
            ConditionExpression = "attribute_exists(PK)",
        }, ct);

    public async Task<UptimeMonitor?> GetAsync(string accountId, string monitorId, CancellationToken ct = default)
    {
        var r = await db.GetItemAsync(o.TableName, Av.Key(Keys.User(accountId), "MON#" + monitorId), ct);
        return r.Item is { Count: > 0 } ? Map(r.Item) : null;
    }

    public async Task<IReadOnlyList<UptimeMonitor>> ListAsync(string accountId, CancellationToken ct = default)
    {
        var items = await db.QueryAllAsync(new QueryRequest
        {
            TableName = o.TableName,
            KeyConditionExpression = "PK = :pk AND begins_with(SK, :p)",
            ExpressionAttributeValues = new() { [":pk"] = Av.S(Keys.User(accountId)), [":p"] = Av.S("MON#") },
        }, ct);
        return items.Select(Map).OrderBy(m => m.CreatedAt).ToList();
    }

    public Task DeleteAsync(string accountId, string monitorId, CancellationToken ct = default) =>
        db.DeleteItemAsync(o.TableName, Av.Key(Keys.User(accountId), "MON#" + monitorId), ct);

    public async Task<IReadOnlyList<UptimeMonitor>> GetDueAsync(DateTimeOffset minute, CancellationToken ct = default)
    {
        var queries = UptimeMonitor.DueKeysAt(minute).Select(key => db.QueryAllAsync(new QueryRequest
        {
            TableName = o.TableName,
            IndexName = Keys.Gsi2,
            KeyConditionExpression = "GSI2PK = :k",
            ExpressionAttributeValues = new() { [":k"] = Av.S(key) },
        }, ct));
        var pages = await Task.WhenAll(queries);
        return pages.SelectMany(p => p).Select(Map).ToList();
    }

    internal static Item ToItem(UptimeMonitor m)
    {
        var i = Av.Key(Keys.User(m.AccountId), "MON#" + m.MonitorId);
        i["type"] = Av.S("MONITOR");
        i["accountId"] = Av.S(m.AccountId);
        i["monitorId"] = Av.S(m.MonitorId);
        i["name"] = Av.S(m.Name);
        i["url"] = Av.S(m.Url);
        i["method"] = Av.S(m.Method);
        i["interval"] = Av.N(m.IntervalMinutes);
        i["expectedStatus"] = Av.N(m.ExpectedStatus);
        i["timeout"] = Av.N(m.TimeoutSeconds);
        i["active"] = Av.B(m.Active);
        i["createdAt"] = Av.S(Av.Iso(m.CreatedAt));
        i["status"] = Av.S(m.Status.ToString());
        i["failures"] = Av.N(m.ConsecutiveFailures);
        i["successes"] = Av.N(m.ConsecutiveSuccesses);
        i.SetIfNotNull("keyword", m.Keyword);
        i.SetIfNotNull("openIncidentId", m.OpenIncidentId);
        if (m.LastCheckedAt is { } l) i["lastCheckedAt"] = Av.S(Av.Iso(l));
        if (m.Active)
        {
            // Sparse index: only active monitors are schedulable.
            i["GSI2PK"] = Av.S(m.DueKey);
            i["GSI2SK"] = Av.S($"{m.AccountId}#{m.MonitorId}");
        }
        return i;
    }

    internal static UptimeMonitor Map(Item i) => new(
        i.Str("accountId"), i.Str("monitorId"), i.Str("name"), i.Str("url"), i.Str("method"),
        (int)i.Num("interval"), (int)i.Num("expectedStatus"), i.StrOrNull("keyword"), (int)i.Num("timeout"),
        i.Bool("active"), i.Ts("createdAt"), Enum.Parse<MonitorStatus>(i.Str("status")),
        (int)i.Num("failures"), (int)i.Num("successes"), i.StrOrNull("openIncidentId"), i.TsOrNull("lastCheckedAt"));
}

public sealed class ResultRepository(IAmazonDynamoDB db, DynamoOptions o) : IResultRepository
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public Task AddAsync(CheckResult r, CancellationToken ct = default)
    {
        var i = Av.Key(Keys.Mon(r.MonitorId), Keys.ResultSk(r.Timestamp));
        i["type"] = Av.S("RESULT");
        i["monitorId"] = Av.S(r.MonitorId);
        i["ts"] = Av.S(Av.Iso(r.Timestamp));
        i["success"] = Av.B(r.Success);
        i["latencyMs"] = Av.N(r.LatencyMs);
        i["error"] = Av.S(r.Error.ToString());
        if (r.StatusCode is { } sc) i["statusCode"] = Av.N(sc);
        i.SetIfNotNull("errorMessage", r.ErrorMessage);
        i["ttl"] = Av.N((r.Timestamp + Retention).ToUnixTimeSeconds());
        return db.PutItemAsync(o.TableName, i, ct);
    }

    public async Task<IReadOnlyList<CheckResult>> GetRangeAsync(string monitorId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var items = await db.QueryAllAsync(new QueryRequest
        {
            TableName = o.TableName,
            KeyConditionExpression = "PK = :pk AND SK BETWEEN :a AND :b",
            ExpressionAttributeValues = new()
            {
                [":pk"] = Av.S(Keys.Mon(monitorId)),
                [":a"] = Av.S(Keys.ResultSk(from)),
                [":b"] = Av.S(Keys.ResultSk(to)),
            },
        }, ct);
        return items.Select(i => new CheckResult(
            i.Str("monitorId"), i.Ts("ts"), i.Bool("success"), (int?)i.NumOrNull("statusCode"),
            (int)i.Num("latencyMs"), Enum.Parse<ErrorKind>(i.Str("error")), i.StrOrNull("errorMessage"))).ToList();
    }
}

public sealed class IncidentRepository(IAmazonDynamoDB db, DynamoOptions o) : IIncidentRepository
{
    public Task OpenAsync(Incident inc, CancellationToken ct = default)
    {
        var i = Av.Key(Keys.Mon(inc.MonitorId), "INC#" + inc.IncidentId);
        i["type"] = Av.S("INCIDENT");
        i["accountId"] = Av.S(inc.AccountId);
        i["monitorId"] = Av.S(inc.MonitorId);
        i["incidentId"] = Av.S(inc.IncidentId);
        i["openedAt"] = Av.S(Av.Iso(inc.OpenedAt));
        i["cause"] = Av.S(inc.Cause);
        if (inc.ResolvedAt is { } r) i["resolvedAt"] = Av.S(Av.Iso(r));
        else
        {
            // Sparse index of open incidents per account.
            i["GSI1PK"] = Av.S(Keys.OpenIncidents(inc.AccountId));
            i["GSI1SK"] = Av.S(inc.IncidentId);
        }
        return db.PutItemAsync(o.TableName, i, ct);
    }

    public Task ResolveAsync(string monitorId, string incidentId, DateTimeOffset resolvedAt, CancellationToken ct = default) =>
        db.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = o.TableName,
            Key = Av.Key(Keys.Mon(monitorId), "INC#" + incidentId),
            UpdateExpression = "SET resolvedAt = :r REMOVE GSI1PK, GSI1SK",
            ConditionExpression = "attribute_exists(PK)",
            ExpressionAttributeValues = new() { [":r"] = Av.S(Av.Iso(resolvedAt)) },
        }, ct);

    public async Task<Incident?> GetAsync(string monitorId, string incidentId, CancellationToken ct = default)
    {
        var r = await db.GetItemAsync(o.TableName, Av.Key(Keys.Mon(monitorId), "INC#" + incidentId), ct);
        return r.Item is { Count: > 0 } ? Map(r.Item) : null;
    }

    public async Task<IReadOnlyList<Incident>> ListForMonitorAsync(string monitorId, int limit, CancellationToken ct = default)
    {
        var items = await db.QueryAllAsync(new QueryRequest
        {
            TableName = o.TableName,
            KeyConditionExpression = "PK = :pk AND begins_with(SK, :p)",
            ExpressionAttributeValues = new() { [":pk"] = Av.S(Keys.Mon(monitorId)), [":p"] = Av.S("INC#") },
            ScanIndexForward = false,
            Limit = limit,
        }, ct);
        return items.Take(limit).Select(Map).ToList();
    }

    public async Task<IReadOnlyList<Incident>> ListOpenForAccountAsync(string accountId, CancellationToken ct = default)
    {
        var items = await db.QueryAllAsync(new QueryRequest
        {
            TableName = o.TableName,
            IndexName = Keys.Gsi1,
            KeyConditionExpression = "GSI1PK = :k",
            ExpressionAttributeValues = new() { [":k"] = Av.S(Keys.OpenIncidents(accountId)) },
        }, ct);
        return items.Select(Map).ToList();
    }

    private static Incident Map(Item i) => new(
        i.Str("accountId"), i.Str("monitorId"), i.Str("incidentId"), i.Ts("openedAt"), i.TsOrNull("resolvedAt"), i.Str("cause"));
}

public sealed class AggregateRepository(IAmazonDynamoDB db, DynamoOptions o) : IAggregateRepository
{
    public Task RecordAsync(string monitorId, DateOnly date, bool success, int latencyMs, CancellationToken ct = default)
    {
        var bucket = DailyAggregate.BucketFor(latencyMs);
        return db.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = o.TableName,
            Key = Av.Key(Keys.Mon(monitorId), Keys.AggSk(date)),
            UpdateExpression = "ADD cnt :one, ok :ok, sumLat :lat, h" + bucket.ToString(CultureInfo.InvariantCulture) + " :one SET #t = :type, monitorId = :m, #d = :date",
            ExpressionAttributeNames = new() { ["#t"] = "type", ["#d"] = "date" },
            ExpressionAttributeValues = new()
            {
                [":one"] = Av.N(1),
                [":ok"] = Av.N(success ? 1 : 0),
                [":lat"] = Av.N(latencyMs),
                [":type"] = Av.S("AGGREGATE"),
                [":m"] = Av.S(monitorId),
                [":date"] = Av.S(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            },
        }, ct);
    }

    public async Task<IReadOnlyList<DailyAggregate>> GetRangeAsync(string monitorId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var items = await db.QueryAllAsync(new QueryRequest
        {
            TableName = o.TableName,
            KeyConditionExpression = "PK = :pk AND SK BETWEEN :a AND :b",
            ExpressionAttributeValues = new()
            {
                [":pk"] = Av.S(Keys.Mon(monitorId)),
                [":a"] = Av.S(Keys.AggSk(from)),
                [":b"] = Av.S(Keys.AggSk(to)),
            },
        }, ct);
        return items.Select(i =>
        {
            var h = new int[DailyAggregate.BucketCount];
            for (var b = 0; b < h.Length; b++) h[b] = (int)i.NumOrZero("h" + b.ToString(CultureInfo.InvariantCulture));
            return new DailyAggregate(monitorId, DateOnly.ParseExact(i.Str("date"), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                (int)i.NumOrZero("cnt"), (int)i.NumOrZero("ok"), i.NumOrZero("sumLat"), h);
        }).ToList();
    }
}

public sealed class AlertRepository(IAmazonDynamoDB db, DynamoOptions o) : IAlertRepository
{
    public Task CreateAsync(AlertSubscription s, CancellationToken ct = default) =>
        db.PutItemAsync(o.TableName, ToItem(s), ct);

    public Task UpdateAsync(AlertSubscription s, CancellationToken ct = default) =>
        db.PutItemAsync(new PutItemRequest { TableName = o.TableName, Item = ToItem(s), ConditionExpression = "attribute_exists(PK)" }, ct);

    public async Task<AlertSubscription?> GetAsync(string accountId, string subscriptionId, CancellationToken ct = default)
    {
        var r = await db.GetItemAsync(o.TableName, Av.Key(Keys.User(accountId), "SUB#" + subscriptionId), ct);
        return r.Item is { Count: > 0 } ? Map(r.Item) : null;
    }

    public async Task<IReadOnlyList<AlertSubscription>> ListAsync(string accountId, CancellationToken ct = default)
    {
        var items = await db.QueryAllAsync(new QueryRequest
        {
            TableName = o.TableName,
            KeyConditionExpression = "PK = :pk AND begins_with(SK, :p)",
            ExpressionAttributeValues = new() { [":pk"] = Av.S(Keys.User(accountId)), [":p"] = Av.S("SUB#") },
        }, ct);
        return items.Select(Map).ToList();
    }

    public Task DeleteAsync(string accountId, string subscriptionId, CancellationToken ct = default) =>
        db.DeleteItemAsync(o.TableName, Av.Key(Keys.User(accountId), "SUB#" + subscriptionId), ct);

    private static Item ToItem(AlertSubscription s)
    {
        var i = Av.Key(Keys.User(s.AccountId), "SUB#" + s.SubscriptionId);
        i["type"] = Av.S("SUBSCRIPTION");
        i["accountId"] = Av.S(s.AccountId);
        i["subscriptionId"] = Av.S(s.SubscriptionId);
        i["alertType"] = Av.S(s.Type.ToString());
        i["target"] = Av.S(s.Target);
        i["active"] = Av.B(s.Active);
        i.SetIfNotNull("secret", s.Secret);
        return i;
    }

    private static AlertSubscription Map(Item i) => new(
        i.Str("accountId"), i.Str("subscriptionId"), Enum.Parse<AlertType>(i.Str("alertType")),
        i.Str("target"), i.StrOrNull("secret"), i.Bool("active"));
}

public sealed class IdempotencyStore(IAmazonDynamoDB db, DynamoOptions o, TimeProvider clock) : IIdempotencyStore
{
    public async Task<bool> TryAcquireAsync(string key, TimeSpan ttl, CancellationToken ct = default)
    {
        var i = Av.Key("IDEM#" + key, "IDEM");
        i["ttl"] = Av.N(clock.GetUtcNow().Add(ttl).ToUnixTimeSeconds());
        try
        {
            await db.PutItemAsync(new PutItemRequest
            {
                TableName = o.TableName,
                Item = i,
                ConditionExpression = "attribute_not_exists(PK)",
            }, ct);
            return true;
        }
        catch (ConditionalCheckFailedException) { return false; }
    }
}

