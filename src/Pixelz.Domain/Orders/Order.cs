using Pixelz.Domain.Common;
using Pixelz.Domain.Orders.Events;

namespace Pixelz.Domain.Orders;

public class Order : AggregateRoot<Guid>
{
    private readonly List<OrderItem> _items = [];

    public string OrderName { get; private set; } = null!;
    public Guid CustomerId { get; private set; }
    public OrderStatus Status { get; private set; }
    public Money TotalAmount { get; private set; } = Money.Zero();
    public string? Description { get; private set; }

    // Set after downstream services respond (via Outbox)
    public string? InternalOrderId { get; private set; }
    public string? InvoiceId { get; private set; }
    public DateTime? CheckedOutAt { get; private set; }

    public IReadOnlyList<OrderItem> Items => _items.AsReadOnly();

    // Required by EF Core
    private Order() { }

    // ── Factory ──────────────────────────────────────────────────────────────

    public static Order Create(Guid customerId, string orderName, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderName);

        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            OrderName = orderName.Trim(),
            Description = description?.Trim(),
            Status = OrderStatus.Draft
        };
        order.SetTimestamps();
        return order;
    }

    // ── Business methods ──────────────────────────────────────────────────────

    public Result AddItem(string serviceType, int quantity, decimal unitPrice, string currency = "USD")
    {
        if (Status != OrderStatus.Draft) return Result.Failure($"Cannot add items to an order in status '{Status}'");
        if (quantity <= 0) return Result.Failure("Quantity must be positive");
        if (unitPrice < 0) return Result.Failure("Unit price cannot be negative");

        _items.Add(OrderItem.Create(Id, serviceType, quantity, unitPrice, currency));
        RecalculateTotal(currency);
        Touch();
        return Result.Success();
    }

    public Result InitiateCheckout()
    {
        if (Status != OrderStatus.Draft) return Result.Failure($"Cannot checkout order in status '{Status}'");
        if (_items.Count == 0) return Result.Failure("Order must have at least one item");
        if (TotalAmount.Amount <= 0) return Result.Failure("Order total must be greater than zero");

        Status = OrderStatus.PendingPayment;
        Touch();
        AddDomainEvent(new OrderCheckoutInitiatedEvent(Id, CustomerId, TotalAmount));
        return Result.Success();
    }

    public void MarkAsPaid(Guid paymentId, string pspTransactionId)
    {
        if (Status != OrderStatus.PendingPayment) throw new InvalidOperationException($"Cannot mark paid from status '{Status}'");

        Status = OrderStatus.Paid;
        CheckedOutAt = DateTime.UtcNow;
        Touch();

        // This event triggers Email + Invoice + Production push via Outbox
        AddDomainEvent(new CheckoutSucceededEvent(Id, CustomerId, TotalAmount, paymentId, pspTransactionId, OrderName));
    }

    public void MarkPaymentFailed(string reason)
    {
        // Revert to Draft so customer can retry
        Status = OrderStatus.Draft;
        Touch();
        AddDomainEvent(new PaymentFailedEvent(Id, CustomerId, reason));
    }

    public void SetInternalOrderId(string internalOrderId)
    {
        InternalOrderId = internalOrderId;
        Touch();
    }

    public void SetInvoiceId(string invoiceId)
    {
        InvoiceId = invoiceId;
        Touch();
    }

    public void MarkAsProcessing()
    {
        if (Status != OrderStatus.Paid)
            throw new InvalidOperationException("Order must be Paid before processing");

        Status = OrderStatus.Processing;
        Touch();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void RecalculateTotal(string currency)
    {
        TotalAmount = _items.Aggregate(Money.Zero(currency),(acc, item) => acc.Add(item.TotalPrice));
    }
}