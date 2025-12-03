using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AzureServiceBusOtel.Api.Telemetry;

/// <summary>
/// Contains telemetry constants and shared instrumentation objects.
/// </summary>
public static class TelemetryConstants
{
    /// <summary>
    /// The name of the service for telemetry purposes.
    /// </summary>
    public const string ServiceName = "AzureServiceBusOtel.Api";

    /// <summary>
    /// The version of the service.
    /// </summary>
    public const string ServiceVersion = "1.0.0";

    /// <summary>
    /// ActivitySource for creating spans/traces.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);

    /// <summary>
    /// Meter for creating metrics.
    /// </summary>
    public static readonly Meter Meter = new(ServiceName, ServiceVersion);
}

/// <summary>
/// Custom metrics for the Service Bus operations.
/// </summary>
public static class ServiceBusMetrics
{
    private static readonly Counter<long> MessagesSentCounter = TelemetryConstants.Meter.CreateCounter<long>(
        "servicebus.messages.sent",
        unit: "{message}",
        description: "Total number of messages sent to Service Bus queues");

    private static readonly Counter<long> MessagesReceivedCounter = TelemetryConstants.Meter.CreateCounter<long>(
        "servicebus.messages.received",
        unit: "{message}",
        description: "Total number of messages received from Service Bus queues");

    private static readonly Counter<long> MessagesProcessedCounter = TelemetryConstants.Meter.CreateCounter<long>(
        "servicebus.messages.processed",
        unit: "{message}",
        description: "Total number of messages successfully processed");

    private static readonly Counter<long> MessagesFailedCounter = TelemetryConstants.Meter.CreateCounter<long>(
        "servicebus.messages.failed",
        unit: "{message}",
        description: "Total number of messages that failed processing");

    private static readonly Histogram<double> MessageProcessingDuration = TelemetryConstants.Meter.CreateHistogram<double>(
        "servicebus.message.processing.duration",
        unit: "ms",
        description: "Duration of message processing in milliseconds");

    private static readonly Histogram<double> MessageLatency = TelemetryConstants.Meter.CreateHistogram<double>(
        "servicebus.message.latency",
        unit: "ms",
        description: "Time from message creation to processing start");

    /// <summary>
    /// Records a message sent event.
    /// </summary>
    public static void RecordMessageSent(string queueName)
    {
        MessagesSentCounter.Add(1, new KeyValuePair<string, object?>("queue.name", queueName));
    }

    /// <summary>
    /// Records a message received event.
    /// </summary>
    public static void RecordMessageReceived(string queueName)
    {
        MessagesReceivedCounter.Add(1, new KeyValuePair<string, object?>("queue.name", queueName));
    }

    /// <summary>
    /// Records a successfully processed message.
    /// </summary>
    public static void RecordMessageProcessed(string queueName, double processingTimeMs)
    {
        MessagesProcessedCounter.Add(1, new KeyValuePair<string, object?>("queue.name", queueName));
        MessageProcessingDuration.Record(processingTimeMs, new KeyValuePair<string, object?>("queue.name", queueName));
    }

    /// <summary>
    /// Records a failed message processing.
    /// </summary>
    public static void RecordMessageFailed(string queueName, string errorType)
    {
        MessagesFailedCounter.Add(1,
            new KeyValuePair<string, object?>("queue.name", queueName),
            new KeyValuePair<string, object?>("error.type", errorType));
    }

    /// <summary>
    /// Records the latency from message creation to processing.
    /// </summary>
    public static void RecordMessageLatency(string queueName, double latencyMs)
    {
        MessageLatency.Record(latencyMs, new KeyValuePair<string, object?>("queue.name", queueName));
    }
}

/// <summary>
/// Custom metrics for order processing.
/// </summary>
public static class OrderMetrics
{
    private static readonly Counter<long> OrdersCreatedCounter = TelemetryConstants.Meter.CreateCounter<long>(
        "orders.created",
        unit: "{order}",
        description: "Total number of orders created");

    private static readonly Counter<long> OrdersCompletedCounter = TelemetryConstants.Meter.CreateCounter<long>(
        "orders.completed",
        unit: "{order}",
        description: "Total number of orders completed");

    private static readonly Histogram<double> OrderTotalValue = TelemetryConstants.Meter.CreateHistogram<double>(
        "orders.total.value",
        unit: "USD",
        description: "Order total value distribution");

    private static readonly Histogram<double> OrderEndToEndDuration = TelemetryConstants.Meter.CreateHistogram<double>(
        "orders.end_to_end.duration",
        unit: "ms",
        description: "End-to-end order processing duration");

    public static void RecordOrderCreated() => OrdersCreatedCounter.Add(1);

    public static void RecordOrderCompleted(double totalValue, double endToEndDurationMs)
    {
        OrdersCompletedCounter.Add(1);
        OrderTotalValue.Record(totalValue);
        OrderEndToEndDuration.Record(endToEndDurationMs);
    }
}

