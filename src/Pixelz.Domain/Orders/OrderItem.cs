using Pixelz.Domain.Common;

namespace Pixelz.Domain.Orders;

public class OrderItem : Entity<Guid>
{
    public Guid OrderId { get; private set; }
    public string ServiceType { get; private set; } = null!;
    public int Quantity { get; private set; }
    public Money UnitPrice { get; private set; } = null!;
    public string? Metadata { get; private set; } // JSON: image URLs, settings

    public Money TotalPrice => new(UnitPrice.Amount * Quantity, UnitPrice.Currency);

    private OrderItem() { }

    public static OrderItem Create(Guid orderId, string serviceType, int quantity, decimal unitPrice, string currency = "USD")
    {
        var item = new OrderItem
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            ServiceType = serviceType,
            Quantity = quantity,
            UnitPrice = new Money(unitPrice, currency)
        };
        item.SetTimestamps();
        return item;
    }
}

public enum OrderStatus : byte
{
    Draft          = 0,
    PendingPayment = 1,
    Paid           = 2,
    Processing     = 3,
    Completed      = 4,
    Cancelled      = 5
}