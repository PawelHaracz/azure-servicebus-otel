namespace AzureServiceBusOtel.Api.Models;

/// <summary>
/// Represents an incoming order request.
/// </summary>
public sealed record CreateOrderRequest(
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    string CustomerEmail);

/// <summary>
/// Represents an order message sent to the orders queue.
/// </summary>
public sealed record OrderMessage
{
    public required Guid OrderId { get; init; }
    public required string ProductName { get; init; }
    public required int Quantity { get; init; }
    public required decimal UnitPrice { get; init; }
    public required string CustomerEmail { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required string CorrelationId { get; init; }
}

/// <summary>
/// API response after creating an order.
/// </summary>
public sealed record OrderCreatedResponse(
    Guid OrderId,
    string Status,
    string CorrelationId);

