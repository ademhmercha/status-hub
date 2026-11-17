global using Item = System.Collections.Generic.Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>;
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace StatusHub.Infrastructure.Dynamo;

public sealed record DynamoOptions(string TableName = "statushub");

/// <summary>Key builders: the single place that knows the key schema (see docs/data-model.md).</summary>
public static class Keys
{
    public const string Pk = "PK";
    public const string Sk = "SK";
    public const string Gsi1 = "GSI1";
    public const string Gsi2 = "GSI2";

    public static string User(string id) => $"USER#{id}";
    public static string Mon(string id) => $"MON#{id}";
    public static string Slug(string slug) => $"SLUG#{slug}";
    public static string OpenIncidents(string accountId) => $"OPEN#{accountId}";
    public static string ResultSk(DateTimeOffset ts) => "RES#" + ts.UtcDateTime.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
    public static string AggSk(DateOnly d) => "AGG#" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

internal static class Av
{
    public static AttributeValue S(string v) => new() { S = v };
    public static AttributeValue N(long v) => new() { N = v.ToString(CultureInfo.InvariantCulture) };
    public static AttributeValue B(bool v) => new() { BOOL = v };

    public static string Str(this Item i, string k) => i[k].S!;
    public static string? StrOrNull(this Item i, string k) => i.TryGetValue(k, out var v) ? v.S : null;
    public static long Num(this Item i, string k) => long.Parse(i[k].N!, CultureInfo.InvariantCulture);
    public static long NumOrZero(this Item i, string k) => i.TryGetValue(k, out var v) && v.N is not null ? long.Parse(v.N, CultureInfo.InvariantCulture) : 0;
    public static long? NumOrNull(this Item i, string k) => i.TryGetValue(k, out var v) && v.N is not null ? long.Parse(v.N, CultureInfo.InvariantCulture) : null;
    public static bool Bool(this Item i, string k) => i.TryGetValue(k, out var v) && v.BOOL == true;

    public static string Iso(DateTimeOffset d) => d.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
    public static DateTimeOffset Ts(this Item i, string k) => DateTimeOffset.Parse(i[k].S!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    public static DateTimeOffset? TsOrNull(this Item i, string k) => i.TryGetValue(k, out var v) && v.S is not null
        ? DateTimeOffset.Parse(v.S, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null;

    public static void SetIfNotNull(this Item i, string k, string? v) { if (v is not null) i[k] = S(v); }

    public static async Task<List<Item>> QueryAllAsync(this IAmazonDynamoDB db, QueryRequest req, CancellationToken ct)
    {
        var items = new List<Item>();
        do
        {
            var res = await db.QueryAsync(req, ct);
            items.AddRange(res.Items);
            req.ExclusiveStartKey = res.LastEvaluatedKey is { Count: > 0 } ? res.LastEvaluatedKey : null;
            if (req.Limit is { } limit && items.Count >= limit) break;
        } while (req.ExclusiveStartKey is not null);
        return items;
    }

    public static Dictionary<string, AttributeValue> Key(string pk, string sk) =>
        new() { [Keys.Pk] = S(pk), [Keys.Sk] = S(sk) };
}

/// <summary>Creates the table and indexes (used by local mode and tests; Terraform owns the real table).</summary>
public static class TableBootstrapper
{
    public static async Task EnsureAsync(IAmazonDynamoDB db, string tableName, CancellationToken ct = default)
    {
        try
        {
            await db.DescribeTableAsync(tableName, ct);
            return;
        }
        catch (ResourceNotFoundException) { }

        await db.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            AttributeDefinitions =
            [
                new("PK", ScalarAttributeType.S), new("SK", ScalarAttributeType.S),
                new("GSI1PK", ScalarAttributeType.S), new("GSI1SK", ScalarAttributeType.S),
                new("GSI2PK", ScalarAttributeType.S), new("GSI2SK", ScalarAttributeType.S),
            ],
            KeySchema = [new("PK", KeyType.HASH), new("SK", KeyType.RANGE)],
            GlobalSecondaryIndexes =
            [
                Gsi("GSI1"), Gsi("GSI2"),
            ],
        }, ct);

        for (var i = 0; i < 50; i++)
        {
            var d = await db.DescribeTableAsync(tableName, ct);
            if (d.Table.TableStatus == TableStatus.ACTIVE) break;
            await Task.Delay(100, ct);
        }

        try
        {
            await db.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
            {
                TableName = tableName,
                TimeToLiveSpecification = new TimeToLiveSpecification { AttributeName = "ttl", Enabled = true },
            }, ct);
        }
        catch (AmazonDynamoDBException) { /* DynamoDB Local versions may not support it */ }
    }

    private static GlobalSecondaryIndex Gsi(string name) => new()
    {
        IndexName = name,
        KeySchema = [new(name + "PK", KeyType.HASH), new(name + "SK", KeyType.RANGE)],
        Projection = new Projection { ProjectionType = ProjectionType.ALL },
    };
}
