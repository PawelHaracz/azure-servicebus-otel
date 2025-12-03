namespace AzureServiceBusOtel.Api.Models;

/// <summary>
/// Represents a processed order message sent to the order-processed queue.
/// </summary>
public sealed record OrderProcessedMessage
{
    public required Guid OrderId { get; init; }
    public required string ProductName { get; init; }
    public required int Quantity { get; init; }
    public required decimal TotalAmount { get; init; }
    public required string CustomerEmail { get; init; }
    public required DateTime ProcessedAt { get; init; }
    public required string ProcessedBy { get; init; }
    public required string CorrelationId { get; init; }
    public required string Status { get; init; }
}

