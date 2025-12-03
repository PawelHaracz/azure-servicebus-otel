using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using AzureServiceBusOtel.Api.Configuration;
using AzureServiceBusOtel.Api.Processors;
using AzureServiceBusOtel.Api.Services;
using AzureServiceBusOtel.Api.Telemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// =============================================================================
// Configuration
// =============================================================================

// Explicitly configure all configuration sources
builder.Configuration
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

builder.Services.Configure<ServiceBusSettings>(
    builder.Configuration.GetSection(ServiceBusSettings.SectionName));

var serviceBusSettings = builder.Configuration
    .GetSection(ServiceBusSettings.SectionName)
    .Get<ServiceBusSettings>()
    ?? throw new InvalidOperationException("ServiceBus configuration is required");

// OpenTelemetry configuration
var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"] 
    ?? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
var enableAzureMonitor = builder.Configuration.GetValue("OpenTelemetry:EnableAzureMonitor", true);
var enableOtlp = builder.Configuration.GetValue("OpenTelemetry:EnableOtlp", !string.IsNullOrEmpty(otlpEndpoint));
var enablePrometheus = builder.Configuration.GetValue("OpenTelemetry:EnablePrometheus", false);

// =============================================================================
// Azure Service Bus
// =============================================================================

builder.Services.AddSingleton(_ => new ServiceBusClient(serviceBusSettings.ConnectionString));

builder.Services.AddSingleton(_ => new ServiceBusAdministrationClient(serviceBusSettings.ConnectionString));

// Register Service Bus services
builder.Services.AddSingleton<IQueueManager, QueueManager>();
builder.Services.AddSingleton<IServiceBusService, ServiceBusService>();

// =============================================================================
// OpenTelemetry Configuration
// =============================================================================

var resourceBuilder = ResourceBuilder.CreateDefault()
    .AddService(
        serviceName: TelemetryConstants.ServiceName,
        serviceVersion: TelemetryConstants.ServiceVersion)
    .AddAttributes(new Dictionary<string, object>
    {
        ["deployment.environment"] = builder.Environment.EnvironmentName,
        ["host.name"] = Environment.MachineName
    });

// Configure OpenTelemetry
var otelBuilder = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: TelemetryConstants.ServiceName,
        serviceVersion: TelemetryConstants.ServiceVersion))
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(TelemetryConstants.ServiceName)
            .AddAspNetCoreInstrumentation(options =>
            {
                options.RecordException = true;
                options.Filter = httpContext =>
                {
                    // Filter out health check and metrics requests from tracing
                    var path = httpContext.Request.Path.Value ?? string.Empty;
                    return !path.Contains("health", StringComparison.OrdinalIgnoreCase)
                        && !path.Contains("metrics", StringComparison.OrdinalIgnoreCase);
                };
            })
            .AddHttpClientInstrumentation(options =>
            {
                options.RecordException = true;
            });

        // Add OTLP exporter for Jaeger
        if (enableOtlp && !string.IsNullOrEmpty(otlpEndpoint))
        {
            tracing.AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(otlpEndpoint);
                options.Protocol = OtlpExportProtocol.Grpc;
            });
        }
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter(TelemetryConstants.ServiceName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation();

        // Add OTLP exporter for metrics
        if (enableOtlp && !string.IsNullOrEmpty(otlpEndpoint))
        {
            metrics.AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(otlpEndpoint);
                options.Protocol = OtlpExportProtocol.Grpc;
            });
        }

        // Add Prometheus exporter
        if (enablePrometheus)
        {
            metrics.AddPrometheusExporter();
        }
    });

// Add Azure Monitor exporter (exports to Application Insights)
if (enableAzureMonitor)
{
    var appInsightsConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"];
    if (!string.IsNullOrEmpty(appInsightsConnectionString))
    {
        otelBuilder.UseAzureMonitor(options =>
        {
            options.ConnectionString = appInsightsConnectionString;
        });
    }
}

// Configure logging with OpenTelemetry
builder.Logging.ClearProviders();
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
    logging.ParseStateValues = true;

    // Add OTLP exporter for logs
    if (enableOtlp && !string.IsNullOrEmpty(otlpEndpoint))
    {
        logging.AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri(otlpEndpoint);
            options.Protocol = OtlpExportProtocol.Grpc;
        });
    }
});
builder.Logging.AddConsole();

// =============================================================================
// Background Services
// =============================================================================

// Queue initializer (runs first to ensure queues exist)
builder.Services.AddHostedService<QueueInitializerHostedService>();

// Message processors
builder.Services.AddHostedService<OrderQueueProcessor>();
builder.Services.AddHostedService<OrderCompletedProcessor>();

// =============================================================================
// ASP.NET Core Services
// =============================================================================

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

// Add health checks
builder.Services.AddHealthChecks()
    .AddAzureServiceBusQueue(
        connectionStringFactory: sp => serviceBusSettings.ConnectionString,
        queueNameFactory: sp => serviceBusSettings.OrdersQueueName,
        name: "servicebus-orders-queue");

var app = builder.Build();

// =============================================================================
// Middleware Pipeline
// =============================================================================

if (app.Environment.IsDevelopment() || app.Environment.EnvironmentName == "Docker")
{
    app.MapOpenApi();
}

app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

// Map Prometheus metrics endpoint
if (enablePrometheus)
{
    app.MapPrometheusScrapingEndpoint("/metrics");
}

// =============================================================================
// Application Startup
// =============================================================================

app.Logger.LogInformation("Starting {ServiceName} v{ServiceVersion}",
    TelemetryConstants.ServiceName,
    TelemetryConstants.ServiceVersion);

app.Logger.LogInformation("Environment: {Environment}", app.Environment.EnvironmentName);

app.Logger.LogInformation("Telemetry Configuration - AzureMonitor: {AzureMonitor}, OTLP: {Otlp}, Prometheus: {Prometheus}",
    enableAzureMonitor, enableOtlp, enablePrometheus);

if (enableOtlp && !string.IsNullOrEmpty(otlpEndpoint))
{
    app.Logger.LogInformation("OTLP Endpoint: {OtlpEndpoint}", otlpEndpoint);
}

app.Logger.LogInformation("Queues: Orders={OrdersQueue}, Processed={ProcessedQueue}",
    serviceBusSettings.OrdersQueueName,
    serviceBusSettings.OrderProcessedQueueName);

await app.RunAsync();
