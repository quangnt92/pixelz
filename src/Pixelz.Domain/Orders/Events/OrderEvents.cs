using Pixelz.Domain.Common;

namespace Pixelz.Domain.Orders.Events;

public record CheckoutSucceededEvent(
    Guid   OrderId,
    Guid   CustomerId,
    Money  TotalAmount,
    Guid   PaymentId,
    string PspTransactionId,
    string OrderName
) : DomainEventBase;

public record OrderCheckoutInitiatedEvent(
    Guid  OrderId,
    Guid  CustomerId,
    Money TotalAmount
) : DomainEventBase;

public record PaymentFailedEvent(
    Guid   OrderId,
    Guid   CustomerId,
    string Reason
) : DomainEventBase;
