using Pixelz.Domain.Common;
using Pixelz.Domain.Customers;
using Pixelz.Domain.Orders;
using Pixelz.Domain.Payments;

namespace Pixelz.Application.Interfaces;

// ── Payment ───────────────────────────────────────────────────────────────────

public interface IPaymentService
{
    Task<PaymentResult> ChargeAsync(ChargeRequest request, CancellationToken ct = default);
}

public record ChargeRequest(
    Money  Amount,
    PaymentMethodDto PaymentMethod,
    string IdempotencyKey);

public record PaymentMethodDto(string Type, string Token);

public class PaymentResult
{
    public bool IsSuccess { get; private init; }
    public Guid PaymentId { get; private init; }
    public string? PspTransactionId { get; private init; }
    public string? FailureReason { get; private init; }
    public string? PspErrorCode { get; private init; }
    public static PaymentResult Success(Guid paymentId, string pspTransactionId) => new() { IsSuccess = true, PaymentId = paymentId, PspTransactionId = pspTransactionId };
    public static PaymentResult Failed(string pspErrorCode, string reason) => new() { IsSuccess = false, PspErrorCode = pspErrorCode, FailureReason = reason };
}

// ── Email ─────────────────────────────────────────────────────────────────────

public interface IEmailService
{
    Task SendCheckoutConfirmationAsync(CheckoutConfirmationEmail email, CancellationToken ct = default);
}

public record CheckoutConfirmationEmail(
    string   RecipientEmail,
    string   RecipientName,
    Guid     OrderId,
    string   OrderName,
    decimal  TotalAmount,
    string   Currency,
    string   PspTransactionId,
    DateTime CheckedOutAt);

// ── Production System ─────────────────────────────────────────────────────────

public interface IProductionService
{
    Task<PushOrderResult> PushOrderAsync(PushOrderRequest request, CancellationToken ct = default);
}

public record PushOrderRequest(
    Guid   OrderId,
    string OrderName,
    Guid   CustomerId,
    decimal TotalAmount,
    string Currency,
    IEnumerable<PushOrderItem> Items);

public record PushOrderItem(string ServiceType, int Quantity, decimal UnitPrice);
public record PushOrderResult(string InternalOrderId, string Status);

// ── Invoice System ────────────────────────────────────────────────────────────

public interface IInvoiceService
{
    Task<CreateInvoiceResult> CreateInvoiceAsync(CreateInvoiceRequest request, CancellationToken ct = default);
}

public record CreateInvoiceRequest(
    Guid     OrderId,
    string   OrderName,
    Guid     CustomerId,
    string   CustomerEmail,
    decimal  TotalAmount,
    string   Currency,
    string   PspTransactionId,
    DateTime CheckedOutAt);

public record CreateInvoiceResult(string InvoiceId, string InvoiceUrl);

// ── Repositories ──────────────────────────────────────────────────────────────

public interface IOrderRepository
{
    Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Order?> GetByIdWithItemsAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(Order order, CancellationToken ct = default);
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

public interface ICustomerRepository
{
    Task<Customer?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Customer?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task AddAsync(Customer customer, CancellationToken ct = default);
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

public interface IPaymentRepository
{
    Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Payment?> GetByIdempotencyKeyAsync(string key, CancellationToken ct = default);
    Task AddAsync(Payment payment, CancellationToken ct = default);
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

public interface IUnitOfWork
{
    Task ExecuteInTransactionAsync(Func<Task> action, CancellationToken ct = default);
}

// ── Read-only DB context for CQRS queries ─────────────────────────────────────

public interface IPixelzDbContext
{
    IQueryable<Customer>  Customers  { get; }
    IQueryable<Order>     Orders     { get; }
    IQueryable<OrderItem> OrderItems { get; }
    IQueryable<Payment>   Payments   { get; }
}