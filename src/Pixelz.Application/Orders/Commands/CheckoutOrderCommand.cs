using MediatR;
using Microsoft.Extensions.Logging;
using Pixelz.Application.Interfaces;

namespace Pixelz.Application.Orders.Commands;

// ── Command ───────────────────────────────────────────────────────────────────

public record CheckoutOrderCommand(
    Guid             OrderId,
    Guid             CustomerId,
    PaymentMethodDto PaymentMethod,
    string           IdempotencyKey
) : IRequest<CheckoutOrderResult>;

// ── Result ────────────────────────────────────────────────────────────────────

public class CheckoutOrderResult
{
    public bool IsSuccess { get; private init; }
    public Guid? OrderId { get; private init; }
    public string? PspTransactionId { get; private init; }
    public string? ErrorCode { get; private init; }
    public string? ErrorMessage { get; private init; }
    public static CheckoutOrderResult Success(Guid orderId, string pspTxn) => new() { IsSuccess = true, OrderId = orderId, PspTransactionId = pspTxn };
    public static CheckoutOrderResult BusinessError(string message) => new() { IsSuccess = false, ErrorCode = "BUSINESS_ERROR", ErrorMessage = message };
    public static CheckoutOrderResult PaymentFailed(string code, string message) => new() { IsSuccess = false, ErrorCode = code, ErrorMessage = message };
    public static CheckoutOrderResult NotFound(string message) => new() { IsSuccess = false, ErrorCode = "NOT_FOUND", ErrorMessage = message };
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class CheckoutOrderCommandHandler(
    IOrderRepository orderRepository,
    IPaymentService paymentService,
    IUnitOfWork unitOfWork,
    ILogger<CheckoutOrderCommandHandler> logger) : IRequestHandler<CheckoutOrderCommand, CheckoutOrderResult>
{
    public async Task<CheckoutOrderResult> Handle(
        CheckoutOrderCommand command, CancellationToken cancellationToken)
    {
        logger.LogInformation("Checkout started. OrderId={OrderId} CustomerId={CustomerId} IdempotencyKey={Key}", command.OrderId, command.CustomerId, command.IdempotencyKey);

        // 1. Load order (with items for domain rule validation)
        var order = await orderRepository.GetByIdWithItemsAsync(command.OrderId, cancellationToken);
        if (order is null || order.CustomerId != command.CustomerId)
        {
            logger.LogWarning("Order not found or unauthorized. OrderId={OrderId} RequestedBy={CustomerId}", command.OrderId, command.CustomerId);
            return CheckoutOrderResult.NotFound($"Order '{command.OrderId}' not found");
        }

        // 2. Apply domain business rule
        var initResult = order.InitiateCheckout();
        if (initResult.IsFailure)
        {
            logger.LogWarning("Checkout rejected by domain rule. OrderId={OrderId} Reason={Reason}", command.OrderId, initResult.Error);
            return CheckoutOrderResult.BusinessError(initResult.Error);
        }

        // 3. Call Payment PSP
        logger.LogInformation("Calling PSP. OrderId={OrderId} Amount={Amount} {Currency}", command.OrderId, order.TotalAmount.Amount, order.TotalAmount.Currency);

        var chargeRequest = new ChargeRequest(order.TotalAmount, command.PaymentMethod, command.IdempotencyKey);
        var paymentResult = await paymentService.ChargeAsync(chargeRequest, cancellationToken);

        if (!paymentResult.IsSuccess)
        {
            logger.LogWarning("Payment failed. OrderId={OrderId} PspErrorCode={Code} Reason={Reason}", command.OrderId, paymentResult.PspErrorCode, paymentResult.FailureReason);
            order.MarkPaymentFailed(paymentResult.FailureReason!);
            await orderRepository.SaveChangesAsync(cancellationToken);
            return CheckoutOrderResult.PaymentFailed(paymentResult.PspErrorCode!, paymentResult.FailureReason!);
        }

        // 4. Mark paid + write OutboxMessages — ATOMIC in one DB transaction
        // EF SaveChanges interceptor converts DomainEvents → OutboxMessages before commit
        order.MarkAsPaid(paymentResult.PaymentId, paymentResult.PspTransactionId!);

        await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            await orderRepository.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

        logger.LogInformation("Checkout completed successfully. OrderId={OrderId} PspTxn={PspTxn}", command.OrderId, paymentResult.PspTransactionId);

        return CheckoutOrderResult.Success(order.Id, paymentResult.PspTransactionId!);
    }
}