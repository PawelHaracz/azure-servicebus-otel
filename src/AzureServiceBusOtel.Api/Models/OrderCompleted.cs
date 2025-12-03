namespace AzureServiceBusOtel.Api.Models;

/// <summary>
/// Represents the final state of a completed order.
/// </summary>
public sealed record OrderCompletedMessage
{
    public required Guid OrderId { get; init; }
    public required string CustomerEmail { get; init; }
    public required decimal TotalAmount { get; init; }
    public required DateTime CompletedAt { get; init; }
    public required string FinalStatus { get; init; }
    public required string CorrelationId { get; init; }
    public required TimeSpan TotalProcessingTime { get; init; }
}

