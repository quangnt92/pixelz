using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pixelz.Application.Interfaces;

namespace Pixelz.Infrastructure.ExternalServices;

// ── Mock Payment PSP ──────────────────────────────────────────────────────────

/// <summary>
/// Token simulation rules:
///   suffix "0000" → card_declined (402)
///   suffix "9999" → insufficient_funds (402)
///   anything else → success (200)
/// </summary>
public class MockPaymentService(ILogger<MockPaymentService> logger) : IPaymentService
{
    public async Task<PaymentResult> ChargeAsync(ChargeRequest request, CancellationToken ct = default)
    {
        await Task.Delay(300, ct); // simulate network latency
        var token = request.PaymentMethod.Token;
        logger.LogInformation("[MockPSP] Processing charge. Amount={Amount} {Currency} Token={Token} IdempotencyKey={Key}", request.Amount.Amount, request.Amount.Currency, token, request.IdempotencyKey);

        if (token.EndsWith("0000"))
        {
            logger.LogWarning("[MockPSP] Simulating card_declined for token ending '0000'");
            return PaymentResult.Failed("card_declined", "Your card was declined");
        }

        if (token.EndsWith("9999"))
        {
            logger.LogWarning("[MockPSP] Simulating insufficient_funds for token ending '9999'");
            return PaymentResult.Failed("insufficient_funds", "Insufficient funds");
        }

        var paymentId = Guid.NewGuid();
        var txnId = $"mock_ch_{Guid.NewGuid():N}";
        logger.LogInformation("[MockPSP] Charge succeeded. PaymentId={PaymentId} TxnId={TxnId}", paymentId, txnId);

        return PaymentResult.Success(paymentId, txnId);
    }
}

// ── Mock Email Service ────────────────────────────────────────────────────────

public class MockEmailService(ILogger<MockEmailService> logger) : IEmailService
{
    public async Task SendCheckoutConfirmationAsync(
        CheckoutConfirmationEmail email, CancellationToken ct = default)
    {
        await Task.Delay(100, ct);
        logger.LogInformation(
            "[MockEmail] Checkout confirmation sent. To={To} OrderId={OrderId} Amount={Amount} {Currency}",
            email.RecipientEmail, email.OrderId, email.TotalAmount, email.Currency);
    }
}

// ── Mock Production Service ───────────────────────────────────────────────────

public class MockProductionOptions
{
    public bool SimulateFailure { get; set; } = false;
    public int SimulateFailureAfterNth { get; set; } = -1;
}

public class MockProductionService(
    IOptions<MockProductionOptions> options,
    ILogger<MockProductionService> logger) : IProductionService
{
    private readonly MockProductionOptions _options = options.Value;
    private int _callCount;

    public async Task<PushOrderResult> PushOrderAsync(PushOrderRequest request, CancellationToken ct = default)
    {
        await Task.Delay(500, ct);
        var n = Interlocked.Increment(ref _callCount);
        if (_options.SimulateFailure ||
            (_options.SimulateFailureAfterNth > 0 && n >= _options.SimulateFailureAfterNth))
        {
            logger.LogWarning("[MockProduction] Simulating failure. OrderId={OrderId}", request.OrderId);
            throw new HttpRequestException("Production service temporarily unavailable (simulated)");
        }

        var internalId = $"PROD-{request.OrderId:N}";
        logger.LogInformation("[MockProduction] Order queued. OrderId={OrderId} InternalId={InternalId}", request.OrderId, internalId);
        return new PushOrderResult(internalId, "QUEUED");
    }
}

// ── Mock Invoice Service ──────────────────────────────────────────────────────

public class MockInvoiceService(ILogger<MockInvoiceService> logger) : IInvoiceService
{
    public async Task<CreateInvoiceResult> CreateInvoiceAsync(
        CreateInvoiceRequest request, CancellationToken ct = default)
    {
        await Task.Delay(150, ct);

        var invoiceId  = $"INV-{DateTime.UtcNow:yyyyMMdd}-{request.OrderId.ToString()[..8].ToUpper()}";
        var invoiceUrl = $"https://invoices.pixelz.com/{invoiceId}";
        logger.LogInformation("[MockInvoice] Invoice created. InvoiceId={InvoiceId} OrderId={OrderId}", invoiceId, request.OrderId);

        return new CreateInvoiceResult(invoiceId, invoiceUrl);
    }
}