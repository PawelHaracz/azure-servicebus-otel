# Correlation ID and Trace Context Propagation

This document explains how correlation IDs and distributed trace context are propagated through the Azure Service Bus messaging flow.

## Flow Diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  1. HTTP Request arrives                                                     │
│     └─► Activity.Current.TraceId becomes the CorrelationId                  │
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│  2. Controller creates OrderMessage with CorrelationId embedded             │
│     └─► OrderMessage { ..., CorrelationId = correlationId }                 │
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│  3. ServiceBusService sends message with TWO propagation mechanisms:        │
│     a) ServiceBusMessage.CorrelationId = correlationId (Service Bus native) │
│     b) ApplicationProperties["traceparent"] = W3C trace context             │
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│  4. Processor extracts trace context and continues the trace:               │
│     └─► StartActivity(..., parentContext) links spans together              │
│     └─► Reads CorrelationId from message body for logging                   │
└─────────────────────────────────────────────────────────────────────────────┘
```

## Two Propagation Mechanisms

### 1. Business Correlation ID (in message body)

The `CorrelationId` is stored in the message payload for business-level correlation and logging:

```csharp
var orderMessage = new OrderMessage
{
    OrderId = orderId,
    // ... other properties
    CorrelationId = correlationId  // Embedded in JSON body
};
```

### 2. W3C Trace Context (in message properties)

The OpenTelemetry trace context is propagated via Service Bus application properties using the [W3C Trace Context](https://www.w3.org/TR/trace-context/) standard:

```csharp
// Format: 00-{traceId}-{spanId}-{flags}
serviceBusMessage.ApplicationProperties["traceparent"] = 
    $"00-{context.TraceId}-{context.SpanId}-{(context.TraceFlags == ActivityTraceFlags.Recorded ? "01" : "00")}";
```

## Implementation Details

### Step 1: Controller - Generate CorrelationId

**File:** `Controllers/OrdersController.cs`

```csharp
// Use the current trace ID as correlation ID (links HTTP request to message processing)
var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString();

// Tag the activity for tracing
activity?.SetTag("correlation.id", correlationId);

// Include in structured logging
_logger.LogInformation(
    "Creating order {OrderId} for product {ProductName}, CorrelationId: {CorrelationId}",
    orderId, request.ProductName, correlationId);

// Embed in message
var orderMessage = new OrderMessage
{
    // ...
    CorrelationId = correlationId
};
```

### Step 2: ServiceBusService - Propagate Context

**File:** `Services/ServiceBusService.cs`

```csharp
var serviceBusMessage = new ServiceBusMessage(jsonMessage)
{
    ContentType = "application/json",
    CorrelationId = correlationId,  // Native Service Bus property
    MessageId = Guid.NewGuid().ToString()
};

// Propagate W3C trace context for distributed tracing
if (activity?.Context is { } context)
{
    serviceBusMessage.ApplicationProperties["traceparent"] = 
        $"00-{context.TraceId}-{context.SpanId}-{(context.TraceFlags == ActivityTraceFlags.Recorded ? "01" : "00")}";
    
    if (!string.IsNullOrEmpty(activity.TraceStateString))
    {
        serviceBusMessage.ApplicationProperties["tracestate"] = activity.TraceStateString;
    }
}
```

### Step 3: Processor - Extract and Continue Trace

**File:** `Processors/OrderQueueProcessor.cs`

```csharp
private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
{
    // Extract trace context from message properties
    var parentContext = ExtractTraceContext(args.Message);

    // Start new activity as child of the producer span
    using var activity = TelemetryConstants.ActivitySource.StartActivity(
        $"Process {queueName}",
        ActivityKind.Consumer,
        parentContext);  // Links to parent span!

    // Read correlation ID from message body for logging
    var orderMessage = JsonSerializer.Deserialize<OrderMessage>(args.Message.Body.ToString());
    
    activity?.SetTag("correlation.id", orderMessage.CorrelationId);
    
    _logger.LogInformation(
        "Processing order {OrderId}, CorrelationId: {CorrelationId}",
        orderMessage.OrderId, orderMessage.CorrelationId);
}
```

### Step 4: Extract Trace Context Helper

```csharp
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
```

## Result in Application Insights

All spans share the same **TraceId**, creating a single end-to-end transaction view:

```
TraceId: abc123def456...
│
├─► HTTP POST /api/orders              (SpanId: 001)
│   │
│   └─► ServiceBus Send orders-queue   (SpanId: 002, Parent: 001)
│       │
│       └─► Process orders-queue       (SpanId: 003, Parent: 002)
│           │
│           └─► ServiceBus Send order-processed-queue (SpanId: 004, Parent: 003)
│               │
│               └─► Complete order-processed-queue    (SpanId: 005, Parent: 004)
```

## Querying in Application Insights

### Find all logs for a correlation ID

```kusto
traces
| where customDimensions.CorrelationId == "your-correlation-id"
| project timestamp, message, customDimensions
| order by timestamp asc
```

### View end-to-end transaction

```kusto
union requests, dependencies, traces
| where operation_Id == "your-trace-id"
| project timestamp, itemType, name, message, duration
| order by timestamp asc
```

### Track message flow across queues

```kusto
dependencies
| where target contains "servicebus"
| where customDimensions.["correlation.id"] != ""
| summarize count() by tostring(customDimensions.["correlation.id"]), target
```

## Key Benefits

1. **End-to-End Visibility**: Single trace spans HTTP request through all queue hops
2. **Business Correlation**: CorrelationId in message body for domain-level tracking
3. **W3C Standard**: Uses industry-standard trace context format
4. **Application Insights Integration**: Full transaction view in Azure portal
5. **Structured Logging**: All logs tagged with CorrelationId for easy filtering

