using System.Diagnostics;
using AzureServiceBusOtel.Api.Models;
using AzureServiceBusOtel.Api.Services;
using AzureServiceBusOtel.Api.Telemetry;
using Microsoft.AspNetCore.Mvc;

namespace AzureServiceBusOtel.Api.Controllers;

/// <summary>
/// API controller for order operations.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly IServiceBusService _serviceBusService;
    private readonly ILogger<OrdersController> _logger;

    public OrdersController(
        IServiceBusService serviceBusService,
        ILogger<OrdersController> logger)
    {
        _serviceBusService = serviceBusService;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new order and sends it to the processing queue.
    /// </summary>
    /// <param name="request">The order creation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created order information.</returns>
    [HttpPost]
    [ProducesResponseType(typeof(OrderCreatedResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateOrder(
        [FromBody] CreateOrderRequest request,
        CancellationToken cancellationToken)
    {
        using var activity = TelemetryConstants.ActivitySource.StartActivity(
            "CreateOrder",
            ActivityKind.Internal);

        var orderId = Guid.NewGuid();
        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString();

        activity?.SetTag("order.id", orderId.ToString());
        activity?.SetTag("correlation.id", correlationId);
        activity?.SetTag("order.product_name", request.ProductName);
        activity?.SetTag("order.quantity", request.Quantity);

        _logger.LogInformation(
            "Creating order {OrderId} for product {ProductName}, Quantity: {Quantity}, Customer: {CustomerEmail}, CorrelationId: {CorrelationId}",
            orderId,
            request.ProductName,
            request.Quantity,
            request.CustomerEmail,
            correlationId);

        try
        {
            var orderMessage = new OrderMessage
            {
                OrderId = orderId,
                ProductName = request.ProductName,
                Quantity = request.Quantity,
                UnitPrice = request.UnitPrice,
                CustomerEmail = request.CustomerEmail,
                CreatedAt = DateTime.UtcNow,
                CorrelationId = correlationId
            };

            await _serviceBusService.SendToOrdersQueueAsync(orderMessage, correlationId, cancellationToken);

            // Record metrics
            OrderMetrics.RecordOrderCreated();

            var response = new OrderCreatedResponse(
                orderId,
                "Accepted",
                correlationId);

            _logger.LogInformation(
                "Order {OrderId} created and queued for processing, CorrelationId: {CorrelationId}",
                orderId,
                correlationId);

            activity?.SetStatus(ActivityStatusCode.Ok);

            return Accepted(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create order for product {ProductName}, CorrelationId: {CorrelationId}",
                request.ProductName,
                correlationId);

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddTag("exception.type", ex.GetType().FullName);
            activity?.AddTag("exception.message", ex.Message);

            return StatusCode(StatusCodes.Status500InternalServerError,
                new { error = "Failed to create order", correlationId });
        }
    }

    /// <summary>
    /// Health check endpoint.
    /// </summary>
    [HttpGet("health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Health()
    {
        return Ok(new { status = "Healthy", timestamp = DateTime.UtcNow });
    }
}

