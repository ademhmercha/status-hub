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
    public async Task PublishAsync(T message, CancellationToken ct) =>
        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = JsonSerializer.Serialize(message, info),
            // Continues the X-Ray trace in the consumer Lambda (works without the X-Ray SDK, so also in Native AOT).
            MessageSystemAttributes = Telemetry.TraceHeader is { Length: > 0 } trace
                ? new Dictionary<string, MessageSystemAttributeValue> { ["AWSTraceHeader"] = new() { DataType = "String", StringValue = trace } }
                : null,
        }, ct);
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
    public static async Task<SQSBatchResponse> ProcessAsync<T>(
        SQSEvent evt, IStage<T> stage, JsonTypeInfo<T> info, CancellationToken ct, bool unwrapDetail = false)
    {
        var failures = new List<SQSBatchResponse.BatchItemFailure>();
        foreach (var record in evt.Records)
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
            }
            catch (Exception ex)
            {
                AWS.Lambda.Powertools.Logging.Logger.LogError(ex, "Record {MessageId} failed and will be retried", record.MessageId);
                failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = record.MessageId });
            }
        }
        return new SQSBatchResponse { BatchItemFailures = failures };
    }
}

