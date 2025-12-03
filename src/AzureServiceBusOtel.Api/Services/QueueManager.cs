using Azure.Messaging.ServiceBus.Administration;
using AzureServiceBusOtel.Api.Configuration;
using Microsoft.Extensions.Options;

namespace AzureServiceBusOtel.Api.Services;

/// <summary>
/// Service responsible for managing Service Bus queues.
/// </summary>
public interface IQueueManager
{
    /// <summary>
    /// Ensures all required queues exist, creating them if necessary.
    /// </summary>
    Task EnsureQueuesExistAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementation of queue management using Service Bus Administration client.
/// </summary>
public sealed class QueueManager : IQueueManager
{
    private readonly ServiceBusAdministrationClient _adminClient;
    private readonly ServiceBusSettings _settings;
    private readonly ILogger<QueueManager> _logger;

    public QueueManager(
        ServiceBusAdministrationClient adminClient,
        IOptions<ServiceBusSettings> settings,
        ILogger<QueueManager> logger)
    {
        _adminClient = adminClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task EnsureQueuesExistAsync(CancellationToken cancellationToken = default)
    {
        await EnsureQueueExistsAsync(_settings.OrdersQueueName, cancellationToken);
        await EnsureQueueExistsAsync(_settings.OrderProcessedQueueName, cancellationToken);
    }

    private async Task EnsureQueueExistsAsync(string queueName, CancellationToken cancellationToken)
    {
        try
        {
            var exists = await _adminClient.QueueExistsAsync(queueName, cancellationToken);

            if (!exists.Value)
            {
                _logger.LogInformation("Creating queue {QueueName}", queueName);

                var options = new CreateQueueOptions(queueName)
                {
                    DefaultMessageTimeToLive = TimeSpan.FromDays(7),
                    LockDuration = TimeSpan.FromMinutes(1),
                    MaxDeliveryCount = 10,
                    EnablePartitioning = false,
                    RequiresSession = false
                };

                await _adminClient.CreateQueueAsync(options, cancellationToken);

                _logger.LogInformation("Queue {QueueName} created successfully", queueName);
            }
            else
            {
                _logger.LogDebug("Queue {QueueName} already exists", queueName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure queue {QueueName} exists", queueName);
            throw;
        }
    }
}

