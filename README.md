# Azure Service Bus with OpenTelemetry Demo

A .NET 9 Web API demonstrating end-to-end distributed tracing with Azure Service Bus and OpenTelemetry, exporting telemetry to multiple destinations: Azure Application Insights, Jaeger, and Prometheus.

## Architecture

```
┌─────────────┐     ┌──────────────┐     ┌─────────────────────┐     ┌──────────────────────┐     ┌─────────────────────────┐
│   HTTP      │     │   Orders     │     │   Order Queue       │     │   Order Processed    │     │   Order Completed       │
│   Request   │────►│   Queue      │────►│   Processor         │────►│   Queue              │────►│   Processor             │
│             │     │              │     │                     │     │                      │     │                         │
└─────────────┘     └──────────────┘     └─────────────────────┘     └──────────────────────┘     └─────────────────────────┘
```

## Features

- **End-to-End Distributed Tracing**: Full trace propagation across HTTP requests and Service Bus queues
- **Multi-Destination Telemetry**: Export to Azure Application Insights, Jaeger, and Prometheus
- **Custom Metrics**: Message counts, processing duration, order values, and latency measurements
- **Structured Logging**: Rich contextual logging with correlation IDs
- **Queue Auto-Creation**: Automatically creates required queues on startup
- **Docker Support**: Full Docker Compose setup with Jaeger and Prometheus

## Prerequisites

- .NET 9 SDK
- Azure Service Bus namespace with connection string
- (Optional) Azure Application Insights resource
- (Optional) Docker and Docker Compose for local observability stack

## Quick Start

### Option 1: Run Locally

```bash
cd src/AzureServiceBusOtel.Api
dotnet run
```

### Option 2: Run with Docker (Jaeger + Prometheus)

1. Create `.env` file in project root:
```bash
SERVICEBUS_CONNECTION_STRING=Endpoint=sb://your-namespace.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=YOUR_KEY
APPLICATIONINSIGHTS_CONNECTION_STRING=InstrumentationKey=your-key;IngestionEndpoint=https://your-region.in.applicationinsights.azure.com/
```

2. Start all services:
```bash
docker-compose up -d --build
```

3. Access services:

| Service | URL |
|---------|-----|
| API | http://localhost:5050 |
| Jaeger UI | http://localhost:16686 |
| Prometheus | http://localhost:9090 |
| Metrics | http://localhost:5050/metrics |

## Configuration

### appsettings.json

```json
{
  "ServiceBus": {
    "ConnectionString": "Endpoint=sb://your-namespace.servicebus.windows.net/;...",
    "OrdersQueueName": "orders-queue",
    "OrderProcessedQueueName": "order-processed-queue",
    "MaxConcurrentCalls": 5
  },
  "ApplicationInsights": {
    "ConnectionString": "InstrumentationKey=your-key;..."
  },
  "OpenTelemetry": {
    "OtlpEndpoint": "http://localhost:4317",
    "EnableAzureMonitor": true,
    "EnableOtlp": true,
    "EnablePrometheus": true
  }
}
```

### Environment Variables

```bash
export ServiceBus__ConnectionString="Endpoint=sb://..."
export ApplicationInsights__ConnectionString="InstrumentationKey=..."
export OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:4317"
```

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
GET /health
```

### Prometheus Metrics
```http
GET /metrics
```

## Telemetry

### Telemetry Destinations

| Signal | Azure App Insights | Jaeger | Prometheus |
|--------|-------------------|--------|------------|
| Traces | ✅ | ✅ (OTLP) | ❌ |
| Metrics | ✅ | ❌ | ✅ (scrape) |
| Logs | ✅ | ✅ (OTLP) | ❌ |

### Traces (Spans)

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

## Observability Queries

### Application Insights (Kusto)

```kusto
// View End-to-End Transactions
traces
| where customDimensions.CorrelationId != ""
| project timestamp, message, customDimensions.CorrelationId, customDimensions.OrderId
| order by timestamp asc

// View Processing Duration
customMetrics
| where name == "servicebus.message.processing.duration"
| summarize avg(value), percentile(value, 95), max(value) by bin(timestamp, 1m)
| render timechart
```

### Prometheus (PromQL)

```promql
# Messages sent per queue
servicebus_messages_sent_total

# 95th percentile processing duration
histogram_quantile(0.95, rate(servicebus_message_processing_duration_bucket[5m]))

# Orders created rate
rate(orders_created_total[5m])
```

## Project Structure

```
├── docker-compose.yml                    # Docker orchestration
├── prometheus/
│   └── prometheus.yml                    # Prometheus config
├── docs/
│   ├── correlation-propagation.md        # Trace propagation docs
│   └── docker-setup.md                   # Docker setup guide
└── src/AzureServiceBusOtel.Api/
    ├── Configuration/
    │   └── ServiceBusSettings.cs         # Configuration model
    ├── Controllers/
    │   └── OrdersController.cs           # API endpoints
    ├── Models/
    │   ├── Order.cs                      # Order DTOs
    │   ├── OrderProcessed.cs             # Processed order model
    │   └── OrderCompleted.cs             # Completed order model
    ├── Processors/
    │   ├── OrderQueueProcessor.cs        # Processes orders queue
    │   └── OrderCompletedProcessor.cs    # Processes completed orders
    ├── Services/
    │   ├── QueueManager.cs               # Queue creation/management
    │   ├── QueueInitializerHostedService.cs
    │   └── ServiceBusService.cs          # Message sending
    ├── Telemetry/
    │   └── TelemetryConstants.cs         # Metrics & tracing setup
    ├── Dockerfile                        # Container build
    ├── Program.cs                        # Application startup
    ├── appsettings.json                  # Base configuration
    ├── appsettings.Development.json      # Dev configuration
    └── appsettings.Docker.json           # Docker configuration
```

## Docker Commands

```bash
# Start all services
docker-compose up -d --build

# View logs
docker-compose logs -f api

# Stop all services
docker-compose down

# Clean up volumes
docker-compose down -v
```

## Documentation

- [Correlation ID Propagation](docs/correlation-propagation.md) - How trace context is propagated
- [Docker Setup](docs/docker-setup.md) - Full Docker environment guide

## License

MIT
