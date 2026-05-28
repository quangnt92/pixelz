using Pixelz.Domain.Common;

namespace Pixelz.Domain.Payments;

public class Payment : AggregateRoot<Guid>
{
    public Guid OrderId { get; private set; }
    public Guid CustomerId { get; private set; }
    public Money Amount { get; private set; } = null!;
    public PaymentStatus Status { get; private set; }
    public string PspProvider { get; private set; } = null!;
    public string? PspTransactionId { get; private set; }
    public string? PspRawResponse { get; private set; }
    public string? FailureReason { get; private set; }
    public string IdempotencyKey { get; private set; } = null!;

    // EF Core
    private Payment() { }

    public static Payment CreatePending(
        Guid orderId, Guid customerId, Money amount,
        string pspProvider, string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pspProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            CustomerId = customerId,
            Amount = amount,
            Status = PaymentStatus.Pending,
            PspProvider = pspProvider.ToUpperInvariant(),
            IdempotencyKey = idempotencyKey
        };
        payment.SetTimestamps();
        return payment;
    }

    public void MarkSucceeded(string pspTransactionId, string? rawResponse = null)
    {
        Status = PaymentStatus.Succeeded;
        PspTransactionId = pspTransactionId;
        PspRawResponse = rawResponse;
        Touch();
    }

    public void MarkFailed(string reason, string? rawResponse = null)
    {
        Status = PaymentStatus.Failed;
        FailureReason = reason;
        PspRawResponse = rawResponse;
        Touch();
    }

    public void MarkRefunded()
    {
        Status = PaymentStatus.Refunded;
        Touch();
    }
}

public enum PaymentStatus : byte
{
    Pending   = 0,
    Succeeded = 1,
    Failed    = 2,
    Refunded  = 3
}
