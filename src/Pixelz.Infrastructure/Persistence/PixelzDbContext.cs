using Microsoft.EntityFrameworkCore;
using Pixelz.Application.Interfaces;
using Pixelz.Domain.Common;
using Pixelz.Domain.Customers;
using Pixelz.Domain.Orders;
using Pixelz.Domain.Payments;
using Pixelz.Infrastructure.Outbox;

namespace Pixelz.Infrastructure.Persistence;

public class PixelzDbContext(DbContextOptions<PixelzDbContext> options) : DbContext(options), IPixelzDbContext
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    // IPixelzDbContext — read-only surface for CQRS queries
    IQueryable<Customer> IPixelzDbContext.Customers => Customers.AsQueryable();
    IQueryable<Order> IPixelzDbContext.Orders => Orders.AsQueryable();
    IQueryable<OrderItem> IPixelzDbContext.OrderItems => OrderItems.AsQueryable();
    IQueryable<Payment> IPixelzDbContext.Payments => Payments.AsQueryable();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PixelzDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ConvertDomainEventsToOutboxMessages();
        return await base.SaveChangesAsync(cancellationToken);
    }

    private void ConvertDomainEventsToOutboxMessages()
    {
        var aggregates = ChangeTracker
            .Entries<AggregateRoot<Guid>>()
            .Where(e => e.Entity.DomainEvents.Count != 0)
            .Select(e => e.Entity)
            .ToList();

        foreach (var aggregate in aggregates)
        {
            foreach (var domainEvent in aggregate.DomainEvents)
                OutboxMessages.Add(OutboxMessage.Create(domainEvent.GetType().Name, domainEvent));

            aggregate.ClearDomainEvents();
        }
    }
}