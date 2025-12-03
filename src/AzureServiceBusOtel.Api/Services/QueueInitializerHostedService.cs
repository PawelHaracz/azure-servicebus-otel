namespace AzureServiceBusOtel.Api.Services;

/// <summary>
/// Hosted service that initializes Service Bus queues on application startup.
/// </summary>
public sealed class QueueInitializerHostedService : IHostedService
{
    private readonly IQueueManager _queueManager;
    private readonly ILogger<QueueInitializerHostedService> _logger;

    public QueueInitializerHostedService(
        IQueueManager queueManager,
        ILogger<QueueInitializerHostedService> logger)
    {
        _queueManager = queueManager;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing Service Bus queues...");

        try
        {
            await _queueManager.EnsureQueuesExistAsync(cancellationToken);
            _logger.LogInformation("Service Bus queues initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Service Bus queues");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

