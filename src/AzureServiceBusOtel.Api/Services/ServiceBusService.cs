using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using AzureServiceBusOtel.Api.Configuration;
using AzureServiceBusOtel.Api.Telemetry;
using Microsoft.Extensions.Options;

namespace AzureServiceBusOtel.Api.Services;

/// <summary>
/// Service for sending messages to Azure Service Bus queues with OpenTelemetry instrumentation.
/// </summary>
public interface IServiceBusService
{
    /// <summary>
    /// Sends a message to the specified queue with telemetry propagation.
    /// </summary>
    Task SendMessageAsync<T>(string queueName, T message, string correlationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message to the orders queue.
    /// </summary>
    Task SendToOrdersQueueAsync<T>(T message, string correlationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message to the order-processed queue.
    /// </summary>
    Task SendToOrderProcessedQueueAsync<T>(T message, string correlationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementation of Service Bus operations with full OpenTelemetry support.
/// </summary>
public sealed class ServiceBusService : IServiceBusService
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusSettings _settings;
    private readonly ILogger<ServiceBusService> _logger;

    public ServiceBusService(
        ServiceBusClient client,
        IOptions<ServiceBusSettings> settings,
        ILogger<ServiceBusService> logger)
    {
        _client = client;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task SendMessageAsync<T>(string queueName, T message, string correlationId, CancellationToken cancellationToken = default)
    {
        using var activity = TelemetryConstants.ActivitySource.StartActivity(
            $"ServiceBus Send {queueName}",
            ActivityKind.Producer);

        activity?.SetTag("messaging.system", "servicebus");
        activity?.SetTag("messaging.destination.name", queueName);
        activity?.SetTag("messaging.operation", "send");
        activity?.SetTag("correlation.id", correlationId);

        try
        {
            await using var sender = _client.CreateSender(queueName);

            var jsonMessage = JsonSerializer.Serialize(message, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var serviceBusMessage = new ServiceBusMessage(jsonMessage)
            {
                ContentType = "application/json",
                CorrelationId = correlationId,
                MessageId = Guid.NewGuid().ToString()
            };

            // Propagate trace context through message application properties
            if (activity?.Context is { } context)
            {
                serviceBusMessage.ApplicationProperties["traceparent"] = $"00-{context.TraceId}-{context.SpanId}-{(context.TraceFlags == ActivityTraceFlags.Recorded ? "01" : "00")}";
                
                if (!string.IsNullOrEmpty(activity.TraceStateString))
                {
                    serviceBusMessage.ApplicationProperties["tracestate"] = activity.TraceStateString;
                }
            }

            serviceBusMessage.ApplicationProperties["MessageType"] = typeof(T).Name;

            await sender.SendMessageAsync(serviceBusMessage, cancellationToken);

            // Record metrics
            ServiceBusMetrics.RecordMessageSent(queueName);

            _logger.LogInformation(
                "Message sent to queue {QueueName} with CorrelationId {CorrelationId} and MessageId {MessageId}",
                queueName,
                correlationId,
                serviceBusMessage.MessageId);

            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddTag("exception.type", ex.GetType().FullName);
            activity?.AddTag("exception.message", ex.Message);

            _logger.LogError(ex,
                "Failed to send message to queue {QueueName} with CorrelationId {CorrelationId}",
                queueName,
                correlationId);

            throw;
        }
    }

    public Task SendToOrdersQueueAsync<T>(T message, string correlationId, CancellationToken cancellationToken = default)
        => SendMessageAsync(_settings.OrdersQueueName, message, correlationId, cancellationToken);

    public Task SendToOrderProcessedQueueAsync<T>(T message, string correlationId, CancellationToken cancellationToken = default)
        => SendMessageAsync(_settings.OrderProcessedQueueName, message, correlationId, cancellationToken);
}

