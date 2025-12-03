# Azure Service Bus with OpenTelemetry Demo

A .NET 9 Web API demonstrating end-to-end distributed tracing with Azure Service Bus and OpenTelemetry, exporting telemetry to Azure Application Insights.

## Architecture

```
┌─────────────┐     ┌──────────────┐     ┌─────────────────────┐     ┌──────────────────────┐     ┌─────────────────────────┐
│   HTTP      │     │   Orders     │     │   Order Queue       │     │   Order Processed    │     │   Order Completed       │
│   Request   │────►│   Queue      │────►│   Processor         │────►│   Queue              │────►│   Processor             │
│             │     │              │     │                     │     │                      │     │                         │
└─────────────┘     └──────────────┘     └─────────────────────┘     └──────────────────────┘     └─────────────────────────┘
                                                                                                              │
                                                                                                              ▼
                                                                                               ┌─────────────────────────┐
                                                                                               │   Order Completed       │
                                                                                               │   (End-to-End Trace)    │
                                                                                               └─────────────────────────┘
```

## Features

- **End-to-End Distributed Tracing**: Full trace propagation across HTTP requests and Service Bus queues
- **Custom Metrics**: Message counts, processing duration, order values, and latency measurements
- **Structured Logging**: Rich contextual logging with correlation IDs
- **Queue Auto-Creation**: Automatically creates required queues on startup
- **Azure Application Insights Integration**: All telemetry exported to Application Insights

## Prerequisites

- .NET 9 SDK
- Azure Subscription with:
  - Azure Service Bus namespace
  - Azure Application Insights resource
- Azure CLI (for authentication)

## Configuration

### appsettings.json

```json
{
  "ServiceBus": {
    "FullyQualifiedNamespace": "your-namespace.servicebus.windows.net",
    "OrdersQueueName": "orders-queue",
    "OrderProcessedQueueName": "order-processed-queue",
    "MaxConcurrentCalls": 5
  },
  "ApplicationInsights": {
    "ConnectionString": "InstrumentationKey=your-key;IngestionEndpoint=https://your-region.in.applicationinsights.azure.com/"
  }
}
```

### Environment Variables (Alternative)

```bash
export ServiceBus__FullyQualifiedNamespace="your-namespace.servicebus.windows.net"
export ApplicationInsights__ConnectionString="InstrumentationKey=..."
```

## Authentication

The application uses `DefaultAzureCredential` for passwordless authentication. Ensure you have one of these configured:

1. **Azure CLI**: `az login`
2. **Visual Studio/VS Code**: Sign in with your Azure account
3. **Managed Identity**: When running in Azure

### Required RBAC Roles

Assign these roles to your identity on the Service Bus namespace:
- `Azure Service Bus Data Sender`
- `Azure Service Bus Data Receiver`
- `Azure Service Bus Data Owner` (for queue management)

## Running the Application

```bash
cd src/AzureServiceBusOtel.Api
dotnet run
```

The API will be available at:
- HTTP: http://localhost:5000
- HTTPS: https://localhost:5001

## API Endpoints

### Create Order
```http
POST /api/orders
Content-Type: application/json

{
  "productName": "Widget Pro",
  "quantity": 5,
  "unitPrice": 29.99,
  "customerEmail": "customer@example.com"
}
```

Response:
```json
{
  "orderId": "550e8400-e29b-41d4-a716-446655440000",
  "status": "Accepted",
  "correlationId": "0af7651916cd43dd8448eb211c80319c"
}
```

### Health Check
```http
GET /api/orders/health
```

```http
GET /health
```

## Telemetry

### Traces (Spans)

The following operations create spans:

| Operation | Kind | Description |
|-----------|------|-------------|
| `CreateOrder` | Internal | HTTP endpoint handling |
| `ServiceBus Send {queue}` | Producer | Sending message to queue |
| `Process {queue}` | Consumer | Processing message from queue |
| `Complete {queue}` | Consumer | Final message processing |

### Metrics

| Metric | Type | Description |
|--------|------|-------------|
| `servicebus.messages.sent` | Counter | Messages sent to queues |
| `servicebus.messages.received` | Counter | Messages received from queues |
| `servicebus.messages.processed` | Counter | Successfully processed messages |
| `servicebus.messages.failed` | Counter | Failed message processing |
| `servicebus.message.processing.duration` | Histogram | Processing time in ms |
| `servicebus.message.latency` | Histogram | Queue latency in ms |
| `orders.created` | Counter | Total orders created |
| `orders.completed` | Counter | Total orders completed |
| `orders.total.value` | Histogram | Order value distribution |
| `orders.end_to_end.duration` | Histogram | E2E processing time |

### Structured Logs

All logs include:
- `CorrelationId` - Links all operations for an order
- `OrderId` - Unique order identifier
- `QueueName` - Service Bus queue name
- `MessageId` - Service Bus message ID

## Application Insights Queries

### View End-to-End Transactions
```kusto
traces
| where customDimensions.CorrelationId != ""
| project timestamp, message, customDimensions.CorrelationId, customDimensions.OrderId
| order by timestamp asc
```

### View Processing Duration
```kusto
customMetrics
| where name == "servicebus.message.processing.duration"
| summarize avg(value), percentile(value, 95), max(value) by bin(timestamp, 1m)
| render timechart
```

### View Order Flow
```kusto
dependencies
| where target contains "servicebus"
| summarize count() by target, resultCode
```

## Project Structure

```
src/AzureServiceBusOtel.Api/
├── Configuration/
│   └── ServiceBusSettings.cs          # Configuration model
├── Controllers/
│   └── OrdersController.cs            # API endpoints
├── Models/
│   ├── Order.cs                       # Order DTOs
│   ├── OrderProcessed.cs              # Processed order model
│   └── OrderCompleted.cs              # Completed order model
├── Processors/
│   ├── OrderQueueProcessor.cs         # Processes orders queue
│   └── OrderCompletedProcessor.cs     # Processes completed orders
├── Services/
│   ├── QueueManager.cs                # Queue creation/management
│   ├── QueueInitializerHostedService.cs # Startup queue init
│   └── ServiceBusService.cs           # Message sending
├── Telemetry/
│   └── TelemetryConstants.cs          # Metrics & tracing setup
├── Program.cs                         # Application startup
└── appsettings.json                   # Configuration
```

## License

MIT

