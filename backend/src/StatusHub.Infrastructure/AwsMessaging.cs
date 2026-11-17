using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.Lambda.SQSEvents;
using Amazon.SQS;
using Amazon.SQS.Model;
using StatusHub.Application;
using StatusHub.Domain;

namespace StatusHub.Infrastructure;

public sealed class SqsPublisher<T>(IAmazonSQS sqs, string queueUrl, JsonTypeInfo<T> info) : IMessagePublisher<T>
{
    // Continues the X-Ray trace in the consumer Lambda (works without the X-Ray SDK, so also in Native AOT).
    private static Dictionary<string, MessageSystemAttributeValue>? TraceAttributes() =>
        Telemetry.TraceHeader is { Length: > 0 } trace
            ? new Dictionary<string, MessageSystemAttributeValue> { ["AWSTraceHeader"] = new() { DataType = "String", StringValue = trace } }
            : null;

    public async Task PublishAsync(T message, CancellationToken ct) =>
        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = JsonSerializer.Serialize(message, info),
            MessageSystemAttributes = TraceAttributes(),
        }, ct);

    /// <summary>SendMessageBatch: up to 10 messages for the price of one request.</summary>
    public async Task<IReadOnlyList<T>> PublishManyAsync(IReadOnlyList<T> messages, CancellationToken ct)
    {
        if (messages.Count == 0) return [];
        var entries = new List<SendMessageBatchRequestEntry>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
            entries.Add(new SendMessageBatchRequestEntry
            {
                Id = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                MessageBody = JsonSerializer.Serialize(messages[i], info),
                MessageSystemAttributes = TraceAttributes(),
            });
        var res = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = queueUrl, Entries = entries }, ct);
        return res.Failed is { Count: > 0 }
            ? res.Failed.Select(f => messages[int.Parse(f.Id, System.Globalization.CultureInfo.InvariantCulture)]).ToList()
            : [];
    }
}

/// <summary>Publishes IncidentOpened / IncidentResolved on the EventBridge bus (rules route them to the notifier queue).</summary>
public sealed class EventBridgeIncidentPublisher(IAmazonEventBridge events, string busName) : IMessagePublisher<IncidentEvent>
{
    public async Task PublishAsync(IncidentEvent message, CancellationToken ct)
    {
        var res = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    EventBusName = busName,
                    TraceHeader = Telemetry.TraceHeader,
                    Source = "statushub",
                    DetailType = message.Type == IncidentEventType.Opened ? "IncidentOpened" : "IncidentResolved",
                    Detail = JsonSerializer.Serialize(message, StatusHubJson.Default.IncidentEvent),
                },
            ],
        }, ct);
        if (res.FailedEntryCount > 0) throw new InvalidOperationException("EventBridge rejected the event: " + res.Entries[0].ErrorMessage);
    }
}

public static class SqsBatch
{
    /// <summary>
    /// Processes every record and reports only the failed ones (partial batch response), so successes are not redelivered.
    /// For EventBridge -> SQS the record body is the whole event envelope; <paramref name="unwrapDetail"/> extracts "detail".
    /// </summary>
    /// <param name="parallel">Process the records of the batch concurrently (Checker: one slow target must not delay the others).
    /// Keep false when records of the same monitor may share a batch (Evaluator updates monitor state).</param>
    /// <param name="afterAll">Runs once after all records (e.g. flush buffered outgoing messages). If it throws, the whole batch is retried.</param>
    public static async Task<SQSBatchResponse> ProcessAsync<T>(
        SQSEvent evt, IStage<T> stage, JsonTypeInfo<T> info, CancellationToken ct,
        bool unwrapDetail = false, bool parallel = false, Func<CancellationToken, Task>? afterAll = null)
    {
        async Task<bool> HandleOne(SQSEvent.SQSMessage record)
        {
            try
            {
                var body = record.Body;
                if (unwrapDetail)
                {
                    using var doc = JsonDocument.Parse(body);
                    body = doc.RootElement.GetProperty("detail").GetRawText();
                }
                var message = JsonSerializer.Deserialize(body, info) ?? throw new JsonException("Empty message.");
                await stage.HandleAsync(message, ct);
                return true;
            }
            catch (Exception ex)
            {
                AWS.Lambda.Powertools.Logging.Logger.LogError(ex, "Record {MessageId} failed and will be retried", record.MessageId);
                return false;
            }
        }

        bool[] ok;
        if (parallel) ok = await Task.WhenAll(evt.Records.Select(HandleOne));
        else
        {
            ok = new bool[evt.Records.Count];
            for (var i = 0; i < ok.Length; i++) ok[i] = await HandleOne(evt.Records[i]);
        }

        if (afterAll is not null)
        {
            try { await afterAll(ct); }
            catch (Exception ex)
            {
                AWS.Lambda.Powertools.Logging.Logger.LogError(ex, "Flushing outgoing messages failed: the whole batch will be retried");
                Array.Fill(ok, false);
            }
        }

        var failures = new List<SQSBatchResponse.BatchItemFailure>();
        for (var i = 0; i < ok.Length; i++)
            if (!ok[i]) failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = evt.Records[i].MessageId });
        return new SQSBatchResponse { BatchItemFailures = failures };
    }
}
