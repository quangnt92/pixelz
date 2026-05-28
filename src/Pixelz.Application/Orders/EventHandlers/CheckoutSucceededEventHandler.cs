using Microsoft.Extensions.Logging;
using Pixelz.Application.Interfaces;
using Pixelz.Domain.Orders.Events;

namespace Pixelz.Application.Orders.EventHandlers;

/// <summary>
/// Handles CheckoutSucceededEvent dispatched by the Outbox background processor.
/// Orchestrates downstream side effects:
///   1. Push order to Production System
///   2. Create Invoice
///   3. Send email confirmation to customer
/// Each step failure triggers a retry via the Outbox exponential backoff mechanism.
/// </summary>
public class CheckoutSucceededEventHandler(
    IEmailService emailService,
    IInvoiceService invoiceService,
    IProductionService productionService,
    IOrderRepository orderRepository,
    ILogger<CheckoutSucceededEventHandler> logger)
{
    public async Task HandleAsync(CheckoutSucceededEvent evt, CancellationToken ct)
    {
        logger.LogInformation("Processing CheckoutSucceeded. OrderId={OrderId} CustomerId={CustomerId} Amount={Amount} {Currency}", evt.OrderId, evt.CustomerId, evt.TotalAmount.Amount, evt.TotalAmount.Currency);

        var order = await orderRepository.GetByIdWithItemsAsync(evt.OrderId, ct)
            ?? throw new InvalidOperationException( $"Order {evt.OrderId} not found while handling CheckoutSucceededEvent");

        // ── Step 1: Push to Production System ─────────────────────────────────

        logger.LogInformation("Pushing order to Production. OrderId={OrderId}", evt.OrderId);

        var productionResult = await productionService.PushOrderAsync(new PushOrderRequest(
            order.Id,
            order.OrderName,
            order.CustomerId,
            order.TotalAmount.Amount,
            order.TotalAmount.Currency,
            order.Items.Select(i => new PushOrderItem(i.ServiceType, i.Quantity, i.UnitPrice.Amount))
        ), ct);

        order.SetInternalOrderId(productionResult.InternalOrderId);
        order.MarkAsProcessing();

        logger.LogInformation("Order pushed to Production. OrderId={OrderId} InternalOrderId={InternalId}", evt.OrderId, productionResult.InternalOrderId);

        // ── Step 2: Create Invoice ─────────────────────────────────────────────

        logger.LogInformation("Creating invoice. OrderId={OrderId}", evt.OrderId);

        var invoiceResult = await invoiceService.CreateInvoiceAsync(new CreateInvoiceRequest(
            order.Id,
            order.OrderName,
            order.CustomerId,
            CustomerEmail: "customer@example.com", // TODO: fetch from Customer service
            order.TotalAmount.Amount,
            order.TotalAmount.Currency,
            evt.PspTransactionId,
            evt.OccurredAt
        ), ct);

        order.SetInvoiceId(invoiceResult.InvoiceId);

        logger.LogInformation("Invoice created. OrderId={OrderId} InvoiceId={InvoiceId}", evt.OrderId, invoiceResult.InvoiceId);

        // ── Step 3: Send confirmation email ───────────────────────────────────

        logger.LogInformation("Sending confirmation email. OrderId={OrderId}", evt.OrderId);

        await emailService.SendCheckoutConfirmationAsync(new CheckoutConfirmationEmail(
            RecipientEmail:   "customer@example.com",
            RecipientName:    "Art Director",
            OrderId:          order.Id,
            OrderName:        order.OrderName,
            TotalAmount:      order.TotalAmount.Amount,
            Currency:         order.TotalAmount.Currency,
            PspTransactionId: evt.PspTransactionId,
            CheckedOutAt:     evt.OccurredAt
        ), ct);

        logger.LogInformation("Confirmation email sent. OrderId={OrderId}", evt.OrderId);

        // ── Persist all state changes together ────────────────────────────────

        await orderRepository.SaveChangesAsync(ct);

        logger.LogInformation("All post-checkout steps completed. OrderId={OrderId}", evt.OrderId);
    }
}