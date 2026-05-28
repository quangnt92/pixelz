using Microsoft.EntityFrameworkCore;
using Pixelz.Application.Interfaces;
using Pixelz.Domain.Customers;
using Pixelz.Domain.Orders;
using Pixelz.Domain.Payments;

namespace Pixelz.Infrastructure.Persistence;

// ── OrderRepository ───────────────────────────────────────────────────────────

public class OrderRepository(PixelzDbContext db) : IOrderRepository
{
    public async Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await db.Orders.FindAsync([id], ct);

    public async Task<Order?> GetByIdWithItemsAsync(Guid id, CancellationToken ct = default)
        => await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task AddAsync(Order order, CancellationToken ct = default)
        => await db.Orders.AddAsync(order, ct);

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await db.SaveChangesAsync(ct);
}

// ── CustomerRepository ────────────────────────────────────────────────────────

public class CustomerRepository(PixelzDbContext db) : ICustomerRepository
{
    public async Task<Customer?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await db.Customers.FindAsync([id], ct);

    public async Task<Customer?> GetByEmailAsync(string email, CancellationToken ct = default)
        => await db.Customers.FirstOrDefaultAsync(c => c.Email == email.ToLowerInvariant(), ct);

    public async Task AddAsync(Customer customer, CancellationToken ct = default)
        => await db.Customers.AddAsync(customer, ct);

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await db.SaveChangesAsync(ct);
}

// ── PaymentRepository ─────────────────────────────────────────────────────────

public class PaymentRepository(PixelzDbContext db) : IPaymentRepository
{
    public async Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await db.Payments.FindAsync([id], ct);

    public async Task<Payment?> GetByIdempotencyKeyAsync(string key, CancellationToken ct = default)
        => await db.Payments.FirstOrDefaultAsync(p => p.IdempotencyKey == key, ct);

    public async Task AddAsync(Payment payment, CancellationToken ct = default)
        => await db.Payments.AddAsync(payment, ct);

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await db.SaveChangesAsync(ct);
}

// ── UnitOfWork ────────────────────────────────────────────────────────────────

public class UnitOfWork(PixelzDbContext db) : IUnitOfWork
{
    public async Task ExecuteInTransactionAsync(Func<Task> action, CancellationToken ct = default)
    {
        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                await action();
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        });
    }
}