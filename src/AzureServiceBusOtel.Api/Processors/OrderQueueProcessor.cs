using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using AzureServiceBusOtel.Api.Configuration;
using AzureServiceBusOtel.Api.Models;
using AzureServiceBusOtel.Api.Services;
using AzureServiceBusOtel.Api.Telemetry;
using Microsoft.Extensions.Options;

namespace AzureServiceBusOtel.Api.Processors;

/// <summary>
/// Background service that processes messages from the orders queue.
/// </summary>
public sealed class OrderQueueProcessor : BackgroundService
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly IServiceBusService _serviceBusService;
    private readonly ServiceBusSettings _settings;
    private readonly ILogger<OrderQueueProcessor> _logger;

    public OrderQueueProcessor(
        ServiceBusClient serviceBusClient,
        IServiceBusService serviceBusService,
        IOptions<ServiceBusSettings> settings,
        ILogger<OrderQueueProcessor> logger)
    {
        _serviceBusClient = serviceBusClient;
        _serviceBusService = serviceBusService;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Order Queue Processor for queue {QueueName}", _settings.OrdersQueueName);

        await using var processor = _serviceBusClient.CreateProcessor(
            _settings.OrdersQueueName,
            new ServiceBusProcessorOptions
            {
                MaxConcurrentCalls = _settings.MaxConcurrentCalls,
                AutoCompleteMessages = false,
                PrefetchCount = 10
            });

        processor.ProcessMessageAsync += ProcessMessageAsync;
        processor.ProcessErrorAsync += ProcessErrorAsync;

        await processor.StartProcessingAsync(stoppingToken);

        _logger.LogInformation("Order Queue Processor started successfully");

        // Wait until cancellation is requested
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Order Queue Processor stopping...");
        }

        await processor.StopProcessingAsync();
        _logger.LogInformation("Order Queue Processor stopped");
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var stopwatch = Stopwatch.StartNew();
        var queueName = _settings.OrdersQueueName;

        // Extract trace context from message
        var parentContext = ExtractTraceContext(args.Message);

        using var activity = TelemetryConstants.ActivitySource.StartActivity(
            $"Process {queueName}",
            ActivityKind.Consumer,
            parentContext);

        activity?.SetTag("messaging.system", "servicebus");
        activity?.SetTag("messaging.destination.name", queueName);
        activity?.SetTag("messaging.operation", "process");
        activity?.SetTag("messaging.message.id", args.Message.MessageId);

        ServiceBusMetrics.RecordMessageReceived(queueName);

        try
        {
            var orderMessage = JsonSerializer.Deserialize<OrderMessage>(
                args.Message.Body.ToString(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (orderMessage is null)
            {
                _logger.LogWarning("Received null or invalid order message with MessageId {MessageId}",
                    args.Message.MessageId);
                await args.DeadLetterMessageAsync(args.Message, "InvalidMessage", "Could not deserialize order message");
                return;
            }

            activity?.SetTag("order.id", orderMessage.OrderId.ToString());
            activity?.SetTag("correlation.id", orderMessage.CorrelationId);

            // Calculate message latency
            var latency = (DateTime.UtcNow - orderMessage.CreatedAt).TotalMilliseconds;
            ServiceBusMetrics.RecordMessageLatency(queueName, latency);

            _logger.LogInformation(
                "Processing order {OrderId} for product {ProductName}, Quantity: {Quantity}, CorrelationId: {CorrelationId}",
                orderMessage.OrderId,
                orderMessage.ProductName,
                orderMessage.Quantity,
                orderMessage.CorrelationId);

            // Simulate order processing
            await Task.Delay(Random.Shared.Next(100, 500), args.CancellationToken);

            // Calculate total amount
            var totalAmount = orderMessage.Quantity * orderMessage.UnitPrice;

            // Create processed order message
            var processedMessage = new OrderProcessedMessage
            {
                OrderId = orderMessage.OrderId,
                ProductName = orderMessage.ProductName,
                Quantity = orderMessage.Quantity,
                TotalAmount = totalAmount,
                CustomerEmail = orderMessage.CustomerEmail,
                ProcessedAt = DateTime.UtcNow,
                ProcessedBy = Environment.MachineName,
                CorrelationId = orderMessage.CorrelationId,
                Status = "Validated"
            };

            // Send to order-processed queue
            await _serviceBusService.SendToOrderProcessedQueueAsync(
                processedMessage,
                orderMessage.CorrelationId,
                args.CancellationToken);

            await args.CompleteMessageAsync(args.Message, args.CancellationToken);

            stopwatch.Stop();
            ServiceBusMetrics.RecordMessageProcessed(queueName, stopwatch.ElapsedMilliseconds);

            _logger.LogInformation(
                "Order {OrderId} processed successfully in {ElapsedMs}ms, sent to order-processed queue",
                orderMessage.OrderId,
                stopwatch.ElapsedMilliseconds);

            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ServiceBusMetrics.RecordMessageFailed(queueName, ex.GetType().Name);

            _logger.LogError(ex,
                "Error processing message {MessageId} from queue {QueueName}",
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

