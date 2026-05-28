using MediatR;
using Microsoft.Extensions.Logging;
using Pixelz.Application.Interfaces;
using Pixelz.Domain.Orders;

namespace Pixelz.Application.Orders.Commands;

// ── Request DTOs ──────────────────────────────────────────────────────────────

public record CreateOrderItemDto(
    string  ServiceType,
    int     Quantity,
    decimal UnitPrice,
    string  Currency = "USD");

// ── Command ───────────────────────────────────────────────────────────────────

public record CreateOrderCommand(
    Guid   CustomerId,
    string OrderName,
    string? Description,
    IEnumerable<CreateOrderItemDto> Items
) : IRequest<CreateOrderResult>;

// ── Result ────────────────────────────────────────────────────────────────────

public class CreateOrderResult
{
    public bool IsSuccess { get; private init; }
    public Guid? OrderId { get; private init; }
    public string? ErrorCode { get; private init; }
    public string? ErrorMessage { get; private init; }
    public static CreateOrderResult Success(Guid orderId) => new() { IsSuccess = true, OrderId = orderId };
    public static CreateOrderResult Failure(string code, string message) => new() { IsSuccess = false, ErrorCode = code, ErrorMessage = message };
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class CreateOrderCommandHandler(
    IOrderRepository orderRepository,
    ILogger<CreateOrderCommandHandler> logger)
        : IRequestHandler<CreateOrderCommand, CreateOrderResult>
{
    public async Task<CreateOrderResult> Handle(
        CreateOrderCommand command, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Creating order. CustomerId={CustomerId} OrderName={OrderName}",
            command.CustomerId, command.OrderName);

        // 1. Create aggregate
        var order = Order.Create(command.CustomerId, command.OrderName, command.Description);

        // 2. Add items with domain validation
        foreach (var item in command.Items)
        {
            var result = order.AddItem(item.ServiceType, item.Quantity, item.UnitPrice, item.Currency);

            if (result.IsFailure)
            {
                logger.LogWarning("AddItem failed. ServiceType={ServiceType} Reason={Reason}", item.ServiceType, result.Error);
                return CreateOrderResult.Failure("INVALID_ITEM", result.Error);
            }
        }

        // 3. Persist
        await orderRepository.AddAsync(order, cancellationToken);
        await orderRepository.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Order created. OrderId={OrderId} Total={Total} {Currency}", order.Id, order.TotalAmount.Amount, order.TotalAmount.Currency);

        return CreateOrderResult.Success(order.Id);
    }
}