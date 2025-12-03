namespace AzureServiceBusOtel.Api.Configuration;

/// <summary>
/// Configuration settings for Azure Service Bus.
/// </summary>
public sealed class ServiceBusSettings
{
    public const string SectionName = "ServiceBus";

    /// <summary>
    /// The connection string for the Service Bus namespace.
    /// </summary>
    public required string ConnectionString { get; init; }

    /// <summary>
    /// The name of the queue for incoming orders.
    /// </summary>
    public string OrdersQueueName { get; init; } = "orders-queue";

    /// <summary>
    /// The name of the queue for processed orders.
    /// </summary>
    public string OrderProcessedQueueName { get; init; } = "order-processed-queue";

    /// <summary>
    /// Maximum number of concurrent calls to the message handler.
    /// </summary>
    public int MaxConcurrentCalls { get; init; } = 5;
}

