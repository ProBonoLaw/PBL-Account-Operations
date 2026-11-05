using System.Text.Json;
using System.Globalization;
using Azure;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Processor;
using Azure.Messaging.EventHubs.Producer;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace AccountOperations;

public sealed class InvalidEventException(string message) : Exception(message);

public static class EventContent
{
    public static BinaryData Envelope(object content) => BinaryData.FromString(
        Newtonsoft.Json.JsonConvert.SerializeObject(new { contentData = content }));

    public static string? PartitionKey(object content)
    {
        var item = content as JObject ?? JObject.FromObject(content);
        foreach (var name in new[] { "accountId", "siteId", "caseId" })
        {
            var value = item[name]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    public static JsonElement Unwrap(JsonElement root)
    {
        var content = root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("contentData", out var nested) ? nested : root;
        if (content.ValueKind != JsonValueKind.String) return content;
        using var document = JsonDocument.Parse(content.GetString()!);
        return document.RootElement.Clone();
    }

    public static string? String(JsonElement content, string name) =>
        content.ValueKind == JsonValueKind.Object && content.TryGetProperty(name, out var item) &&
        item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    public static string Required(JsonElement content, string name) =>
        !string.IsNullOrWhiteSpace(String(content, name)) ? String(content, name)!
            : throw new InvalidEventException($"Event is missing {name}");
}

public sealed class EventPublisher(string hubName) : IAsyncDisposable
{
    private readonly EventHubProducerClient producer = new(Settings.Required("EVENTHUB_NAMESPACE"),
        hubName, Settings.Credential,
        new EventHubProducerClientOptions { ConnectionOptions = Settings.ConnectionOptions() });

    public Task PublishAsync(object content, CancellationToken token) =>
        producer.SendAsync([new EventData(EventContent.Envelope(content))],
            new SendEventOptions { PartitionKey = EventContent.PartitionKey(content) }, token);

    public ValueTask DisposeAsync() => producer.DisposeAsync();
}

public interface IAccountEventHandler
{
    Task HandleAsync(JsonElement content, CancellationToken token);
}

public sealed class EventWorker : IHostedService, IDisposable
{
    private readonly EventProcessorClient processor;
    private readonly BlobContainerClient failedEvents;
    private readonly SemaphoreSlim processing = new(1, 1);
    private readonly IAccountEventHandler handler;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<EventWorker> logger;
    private readonly string consumerGroup;
    private int stopping;

    public EventWorker(IAccountEventHandler handler, IHostApplicationLifetime lifetime,
        ILogger<EventWorker> logger)
    {
        this.handler = handler;
        this.lifetime = lifetime;
        this.logger = logger;
        consumerGroup = Settings.Required("EVENTHUB_CONSUMER_GROUP");
        var checkpoints = new BlobContainerClient(
            new Uri(Settings.Required("CHECKPOINT_BLOB_CONTAINER_URI")), Settings.Credential);
        failedEvents = new BlobContainerClient(
            new Uri(Settings.Required("FAILED_EVENTS_BLOB_CONTAINER_URI")), Settings.Credential);
        var role = Settings.Required("ROLE");
        var hubName = role switch
        {
            "account" => Settings.Required("ACCOUNT_EVENTHUB_NAME"),
            "sharepoint" => Settings.Required("ACCOUNT_EVENTHUB_NAME"),
            "email" => Settings.Required("ACCOUNT_EVENTHUB_NAME"),
            _ => throw new InvalidOperationException($"Unsupported ROLE: {role}")
        };
        processor = new EventProcessorClient(checkpoints, consumerGroup,
            Settings.Required("EVENTHUB_NAMESPACE"), hubName, Settings.Credential,
            new EventProcessorClientOptions { ConnectionOptions = Settings.ConnectionOptions() });
        var cutover = Settings.Get("EVENTHUB_START_ENQUEUED_UTC");
        var startingPosition = string.IsNullOrWhiteSpace(cutover)
            ? EventPosition.Latest
            : EventPosition.FromEnqueuedTime(DateTimeOffset.Parse(cutover,
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).AddTicks(-1));
        processor.PartitionInitializingAsync += args =>
        {
            args.DefaultStartingPosition = startingPosition;
            return Task.CompletedTask;
        };
        processor.ProcessEventAsync += ProcessAsync;
        processor.ProcessErrorAsync += ErrorAsync;
    }

    public Task StartAsync(CancellationToken token) => processor.StartProcessingAsync(token);
    public Task StopAsync(CancellationToken token) => processor.StopProcessingAsync(token);
    public void Dispose() => processing.Dispose();

    private async Task ProcessAsync(ProcessEventArgs args)
    {
        if (args.Data is null) return;
        try
        {
            await processing.WaitAsync(args.CancellationToken);
            try { await ProcessOneAsync(args); }
            finally { processing.Release(); }
        }
        catch (OperationCanceledException) when (args.CancellationToken.IsCancellationRequested) { }
    }

    private async Task ProcessOneAsync(ProcessEventArgs args)
    {
        try
        {
            if (Volatile.Read(ref stopping) != 0) return;
            using var document = JsonDocument.Parse(args.Data!.EventBody.ToMemory());
            try
            {
                var content = EventContent.Unwrap(document.RootElement);
                if (content.ValueKind != JsonValueKind.Object)
                    throw new InvalidEventException("Event content must be an object");
                await handler.HandleAsync(content, args.CancellationToken);
            }
            catch (InvalidEventException error)
            {
                await failedEvents.GetBlobClient(
                    $"{consumerGroup}/{args.Partition.PartitionId}/{args.Data.SequenceNumber}.json")
                    .UploadAsync(new BinaryData(args.Data.EventBody.ToMemory()), overwrite: true,
                        cancellationToken: args.CancellationToken);
                logger.LogError(error, "Retained invalid event at {Partition}/{Sequence}",
                    args.Partition.PartitionId, args.Data.SequenceNumber);
            }
            await args.UpdateCheckpointAsync(args.CancellationToken);
        }
        catch (JsonException error)
        {
            try
            {
                await failedEvents.GetBlobClient(
                    $"{consumerGroup}/{args.Partition.PartitionId}/{args.Data!.SequenceNumber}.json")
                    .UploadAsync(new BinaryData(args.Data.EventBody.ToMemory()), overwrite: true,
                        cancellationToken: args.CancellationToken);
                await args.UpdateCheckpointAsync(args.CancellationToken);
            }
            catch (Exception retentionError) { Stop(retentionError, args); }
            logger.LogError(error, "Retained malformed event");
        }
        catch (OperationCanceledException) when (args.CancellationToken.IsCancellationRequested) { }
        catch (Exception error) { Stop(error, args); }
    }

    private Task ErrorAsync(ProcessErrorEventArgs args)
    {
        logger.LogError(args.Exception, "Event Hubs error during {Operation}", args.Operation);
        if (args.Exception is RequestFailedException) Stop(args.Exception);
        return Task.CompletedTask;
    }

    private void Stop(Exception error, ProcessEventArgs? args = null)
    {
        logger.LogCritical(error, "Worker stopped without checkpoint at {Partition}/{Sequence}",
            args?.Partition.PartitionId, args?.Data?.SequenceNumber);
        if (Interlocked.Exchange(ref stopping, 1) == 0)
        {
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
    }
}
