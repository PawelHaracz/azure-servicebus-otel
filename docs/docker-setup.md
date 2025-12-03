# Docker Setup with Jaeger and Prometheus

This document explains how to run the application with Jaeger for distributed tracing and Prometheus for metrics collection.

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                            Docker Compose                                    │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  ┌──────────────┐     OTLP/gRPC      ┌──────────────┐                       │
│  │              │ ─────────────────► │    Jaeger    │  :16686 (UI)          │
│  │              │     (traces)       │  (all-in-one)│  :4317 (OTLP)         │
│  │              │                    └──────────────┘                       │
│  │              │                                                            │
│  │  Application │     /metrics       ┌──────────────┐                       │
│  │   (.NET 9)   │ ◄───────────────── │  Prometheus  │  :9090 (UI)           │
│  │              │     (scrape)       │              │                       │
│  │   :5000      │                    └──────────────┘                       │
│  │              │                                                            │
│  │              │     OTLP           ┌──────────────┐                       │
│  │              │ ─────────────────► │    Azure     │  (cloud)              │
│  └──────────────┘     (all signals)  │ App Insights │                       │
│                                      └──────────────┘                       │
└─────────────────────────────────────────────────────────────────────────────┘
```

## Prerequisites

- Docker and Docker Compose installed
- Azure Service Bus connection string
- Azure Application Insights connection string (optional)

## Quick Start

### 1. Create Environment File

Create a `.env` file in the project root:

```bash
# Azure Service Bus Connection String
SERVICEBUS_CONNECTION_STRING=Endpoint=sb://your-namespace.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=YOUR_KEY

# Azure Application Insights Connection String (optional)
APPLICATIONINSIGHTS_CONNECTION_STRING=InstrumentationKey=your-key;IngestionEndpoint=https://your-region.in.applicationinsights.azure.com/
```

### 2. Start All Services

```bash
docker-compose up -d
```

### 3. Access the Services

| Service | URL | Description |
|---------|-----|-------------|
| API | http://localhost:5000 | Application API |
| Jaeger UI | http://localhost:16686 | View distributed traces |
| Prometheus | http://localhost:9090 | View and query metrics |
| Metrics Endpoint | http://localhost:5000/metrics | Raw Prometheus metrics |
| Health Check | http://localhost:5000/health | Application health |

## Testing the Flow

### Create an Order

```bash
curl -X POST http://localhost:5000/api/orders \
  -H "Content-Type: application/json" \
  -d '{
    "productName": "Widget Pro",
    "quantity": 5,
    "unitPrice": 29.99,
    "customerEmail": "test@example.com"
  }'
```

### View Traces in Jaeger

1. Open http://localhost:16686
2. Select "AzureServiceBusOtel.Api" from the Service dropdown
3. Click "Find Traces"
4. Click on a trace to see the end-to-end flow

### View Metrics in Prometheus

1. Open http://localhost:9090
2. Try these queries:

```promql
# Messages sent per queue
servicebus_messages_sent_total

# Message processing duration
histogram_quantile(0.95, rate(servicebus_message_processing_duration_bucket[5m]))

# Orders created
orders_created_total

# HTTP request duration
histogram_quantile(0.95, rate(http_server_request_duration_seconds_bucket[5m]))
```

## Configuration Options

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `SERVICEBUS_CONNECTION_STRING` | Azure Service Bus connection string | Required |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Azure App Insights connection string | Optional |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP endpoint for traces/metrics | `http://jaeger:4317` |
| `ASPNETCORE_ENVIRONMENT` | Environment name | `Docker` |

### appsettings.Docker.json Options

```json
{
  "OpenTelemetry": {
    "OtlpEndpoint": "http://jaeger:4317",
    "EnableAzureMonitor": true,
    "EnableOtlp": true,
    "EnablePrometheus": true
  }
}
```

## Docker Commands

```bash
# Start all services
docker-compose up -d

# View logs
docker-compose logs -f api

# Stop all services
docker-compose down

# Rebuild and start
docker-compose up -d --build

# Remove volumes (clean slate)
docker-compose down -v
```

## Telemetry Destinations

| Signal | Jaeger | Prometheus | Azure App Insights |
|--------|--------|------------|-------------------|
| Traces | ✅ OTLP | ❌ | ✅ Azure Monitor |
| Metrics | ❌ | ✅ Scrape | ✅ Azure Monitor |
| Logs | ✅ OTLP | ❌ | ✅ Azure Monitor |

## Troubleshooting

### No traces appearing in Jaeger

1. Check the API logs: `docker-compose logs api`
2. Verify OTLP endpoint is reachable: `docker-compose logs jaeger`
3. Ensure `EnableOtlp` is `true` in configuration

### No metrics in Prometheus

1. Check if metrics endpoint is accessible: `curl http://localhost:5000/metrics`
2. Verify Prometheus can reach the API: `docker-compose logs prometheus`
3. Check Prometheus targets: http://localhost:9090/targets

### Connection to Service Bus fails

1. Verify connection string is correct in `.env` file
2. Check if Service Bus namespace is accessible from Docker network
3. Review API logs for detailed error messages

## Exposed Metrics

### Service Bus Metrics
- `servicebus_messages_sent_total` - Counter of messages sent
- `servicebus_messages_received_total` - Counter of messages received
- `servicebus_messages_processed_total` - Counter of processed messages
- `servicebus_messages_failed_total` - Counter of failed messages
- `servicebus_message_processing_duration_milliseconds` - Histogram of processing time
- `servicebus_message_latency_milliseconds` - Histogram of queue latency

### Order Metrics
- `orders_created_total` - Counter of orders created
- `orders_completed_total` - Counter of orders completed
- `orders_total_value_usd` - Histogram of order values
- `orders_end_to_end_duration_milliseconds` - Histogram of E2E duration

### Runtime Metrics
- `process_cpu_count` - Number of CPUs
- `process_memory_bytes` - Memory usage
- `dotnet_gc_*` - Garbage collection metrics

