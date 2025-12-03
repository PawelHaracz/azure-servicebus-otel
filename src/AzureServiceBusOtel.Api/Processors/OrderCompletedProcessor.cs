using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using AzureServiceBusOtel.Api.Configuration;
using AzureServiceBusOtel.Api.Models;
using AzureServiceBusOtel.Api.Telemetry;
using Microsoft.Extensions.Options;

namespace AzureServiceBusOtel.Api.Processors;

/// <summary>
/// Background service that processes messages from the order-processed queue (final step).
/// </summary>
public sealed class OrderCompletedProcessor : BackgroundService
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly ServiceBusSettings _settings;
    private readonly ILogger<OrderCompletedProcessor> _logger;

    public OrderCompletedProcessor(
        ServiceBusClient serviceBusClient,
        IOptions<ServiceBusSettings> settings,
        ILogger<OrderCompletedProcessor> logger)
    {
        _serviceBusClient = serviceBusClient;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Order Completed Processor for queue {QueueName}", _settings.OrderProcessedQueueName);

        await using var processor = _serviceBusClient.CreateProcessor(
            _settings.OrderProcessedQueueName,
            new ServiceBusProcessorOptions
            {
                MaxConcurrentCalls = _settings.MaxConcurrentCalls,
                AutoCompleteMessages = false,
                PrefetchCount = 10
            });

        processor.ProcessMessageAsync += ProcessMessageAsync;
        processor.ProcessErrorAsync += ProcessErrorAsync;

        await processor.StartProcessingAsync(stoppingToken);

        _logger.LogInformation("Order Completed Processor started successfully");

        // Wait until cancellation is requested
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Order Completed Processor stopping...");
        }

        await processor.StopProcessingAsync();
        _logger.LogInformation("Order Completed Processor stopped");
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var stopwatch = Stopwatch.StartNew();
        var queueName = _settings.OrderProcessedQueueName;

        // Extract trace context from message
        var parentContext = ExtractTraceContext(args.Message);

        using var activity = TelemetryConstants.ActivitySource.StartActivity(
            $"Complete {queueName}",
            ActivityKind.Consumer,
            parentContext);

        activity?.SetTag("messaging.system", "servicebus");
        activity?.SetTag("messaging.destination.name", queueName);
        activity?.SetTag("messaging.operation", "complete");
        activity?.SetTag("messaging.message.id", args.Message.MessageId);

        ServiceBusMetrics.RecordMessageReceived(queueName);

        try
        {
            var processedMessage = JsonSerializer.Deserialize<OrderProcessedMessage>(
                args.Message.Body.ToString(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (processedMessage is null)
            {
                _logger.LogWarning("Received null or invalid processed order message with MessageId {MessageId}",
                    args.Message.MessageId);
                await args.DeadLetterMessageAsync(args.Message, "InvalidMessage", "Could not deserialize processed order message");
                return;
            }

            activity?.SetTag("order.id", processedMessage.OrderId.ToString());
            activity?.SetTag("correlation.id", processedMessage.CorrelationId);
            activity?.SetTag("order.total_amount", processedMessage.TotalAmount);

            // Calculate message latency
            var latency = (DateTime.UtcNow - processedMessage.ProcessedAt).TotalMilliseconds;
            ServiceBusMetrics.RecordMessageLatency(queueName, latency);

            _logger.LogInformation(
                "Completing order {OrderId} for customer {CustomerEmail}, Total: {TotalAmount:C}, CorrelationId: {CorrelationId}",
                processedMessage.OrderId,
                processedMessage.CustomerEmail,
                processedMessage.TotalAmount,
                processedMessage.CorrelationId);

            // Simulate final processing (e.g., sending confirmation email, updating database)
            await Task.Delay(Random.Shared.Next(50, 200), args.CancellationToken);

            // Create completed order record
            var completedOrder = new OrderCompletedMessage
            {
                OrderId = processedMessage.OrderId,
                CustomerEmail = processedMessage.CustomerEmail,
                TotalAmount = processedMessage.TotalAmount,
                CompletedAt = DateTime.UtcNow,
                FinalStatus = "Completed",
                CorrelationId = processedMessage.CorrelationId,
                TotalProcessingTime = DateTime.UtcNow - processedMessage.ProcessedAt
            };

            await args.CompleteMessageAsync(args.Message, args.CancellationToken);

            stopwatch.Stop();
            ServiceBusMetrics.RecordMessageProcessed(queueName, stopwatch.ElapsedMilliseconds);

            // Record order completion metrics
            OrderMetrics.RecordOrderCompleted(
                (double)completedOrder.TotalAmount,
                completedOrder.TotalProcessingTime.TotalMilliseconds);

            _logger.LogInformation(
                "Order {OrderId} completed successfully in {ElapsedMs}ms. Status: {FinalStatus}, TotalAmount: {TotalAmount:C}",
                completedOrder.OrderId,
                stopwatch.ElapsedMilliseconds,
                completedOrder.FinalStatus,
                completedOrder.TotalAmount);

            // Log the end-to-end completion
            _logger.LogInformation(
                "End-to-end order processing complete for OrderId {OrderId}, CorrelationId: {CorrelationId}",
                completedOrder.OrderId,
                completedOrder.CorrelationId);

            activity?.SetStatus(ActivityStatusCode.Ok);
            activity?.SetTag("order.final_status", completedOrder.FinalStatus);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ServiceBusMetrics.RecordMessageFailed(queueName, ex.GetType().Name);

            _logger.LogError(ex,
                "Error completing message {MessageId} from queue {QueueName}",
                args.Message.MessageId,
                queueName);

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddTag("exception.type", ex.GetType().FullName);
            activity?.AddTag("exception.message", ex.Message);

            // Let the message be retried
            await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception,
            "Error in Service Bus processor. Source: {ErrorSource}, Namespace: {Namespace}, EntityPath: {EntityPath}",
            args.ErrorSource,
            args.FullyQualifiedNamespace,
            args.EntityPath);

        return Task.CompletedTask;
    }

    private static ActivityContext ExtractTraceContext(ServiceBusReceivedMessage message)
    {
        if (message.ApplicationProperties.TryGetValue("traceparent", out var traceparentObj) &&
            traceparentObj is string traceparent)
        {
            try
            {
                // Parse W3C trace context: 00-{traceId}-{spanId}-{flags}
                var parts = traceparent.Split('-');
                if (parts.Length >= 4 && parts[1].Length == 32 && parts[2].Length == 16)
                {
                    var traceId = ActivityTraceId.CreateFromString(parts[1].AsSpan());
                    var spanId = ActivitySpanId.CreateFromString(parts[2].AsSpan());
                    var flags = parts[3] == "01" ? ActivityTraceFlags.Recorded : ActivityTraceFlags.None;

                    string? traceState = null;
                    if (message.ApplicationProperties.TryGetValue("tracestate", out var traceStateObj))
                    {
                        traceState = traceStateObj as string;
                    }

                    return new ActivityContext(traceId, spanId, flags, traceState);
                }
            }
            catch
            {
                // If parsing fails, return default context
            }
        }

        return default;
    }
}

