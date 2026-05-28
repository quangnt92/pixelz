using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pixelz.Domain.Customers;
using Pixelz.Domain.Orders;
using Pixelz.Domain.Payments;
using Pixelz.Infrastructure.Outbox;

namespace Pixelz.Infrastructure.Persistence.Configurations;

// ── Customers ─────────────────────────────────────────────────────────────────

public class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("Customers");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Email)
            .HasMaxLength(320)
            .IsRequired();

        builder.Property(c => c.FullName)
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(c => c.Company)
            .HasMaxLength(255);

        builder.Property(c => c.Phone)
            .HasMaxLength(50);

        builder.Property(c => c.Status)
            .HasConversion<byte>()
            .IsRequired();

        builder.HasIndex(c => c.Email)
            .IsUnique()
            .HasDatabaseName("UQ_Customers_Email");

        builder.HasIndex(c => c.Status)
            .HasDatabaseName("IX_Customers_Status");
    }
}

// ── Orders ────────────────────────────────────────────────────────────────────

public class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.OrderName)
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(o => o.Description)
            .HasMaxLength(1000);

        builder.Property(o => o.InternalOrderId).HasMaxLength(100);
        builder.Property(o => o.InvoiceId).HasMaxLength(100);

        // Money owned type → flat columns
        builder.OwnsOne(o => o.TotalAmount, m =>
        {
            m.Property(x => x.Amount)
                .HasColumnName("TotalAmount")
                .HasPrecision(18, 2)
                .IsRequired();
            m.Property(x => x.Currency)
                .HasColumnName("Currency")
                .HasMaxLength(3)
                .IsRequired();
        });

        builder.Property(o => o.Status)
            .HasConversion<byte>()
            .IsRequired();

        // Optimistic concurrency — prevents race condition on checkout
        builder.Property<byte[]>("RowVersion")
            .IsRowVersion()
            .HasColumnName("RowVersion");

        builder.HasMany(o => o.Items)
            .WithOne()
            .HasForeignKey(i => i.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        // FK to Customers — no cascade delete (keep order history)
        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(o => o.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(o => o.OrderName)
            .HasDatabaseName("IX_Orders_OrderName");

        builder.HasIndex(o => new { o.CustomerId, o.Status })
            .HasDatabaseName("IX_Orders_CustomerId_Status");
    }
}

// ── OrderItems ────────────────────────────────────────────────────────────────

public class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("OrderItems");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.ServiceType)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(i => i.Metadata)
            .HasColumnType("nvarchar(max)");

        builder.OwnsOne(i => i.UnitPrice, m =>
        {
            m.Property(x => x.Amount)
                .HasColumnName("UnitPrice")
                .HasPrecision(18, 2)
                .IsRequired();
            m.Property(x => x.Currency)
                .HasColumnName("Currency")
                .HasMaxLength(3)
                .IsRequired();
        });

        builder.Ignore(i => i.TotalPrice); // computed in domain, not stored

        builder.HasIndex(i => i.OrderId)
            .HasDatabaseName("IX_OrderItems_OrderId");
    }
}

// ── Payments ──────────────────────────────────────────────────────────────────

public class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("Payments");
        builder.HasKey(p => p.Id);

        // Money owned type
        builder.OwnsOne(p => p.Amount, m =>
        {
            m.Property(x => x.Amount)
                .HasColumnName("Amount")
                .HasPrecision(18, 2)
                .IsRequired();
            m.Property(x => x.Currency)
                .HasColumnName("Currency")
                .HasMaxLength(3)
                .IsRequired();
        });

        builder.Property(p => p.Status)
            .HasConversion<byte>()
            .IsRequired();

        builder.Property(p => p.PspProvider)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(p => p.PspTransactionId)
            .HasMaxLength(255);

        builder.Property(p => p.PspRawResponse)
            .HasColumnType("nvarchar(max)");

        builder.Property(p => p.FailureReason)
            .HasMaxLength(500);

        builder.Property(p => p.IdempotencyKey)
            .HasMaxLength(255)
            .IsRequired();

        // Idempotency — prevents double charge
        builder.HasIndex(p => p.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("UQ_Payments_IdempotencyKey");

        builder.HasIndex(p => p.OrderId)
            .HasDatabaseName("IX_Payments_OrderId");

        builder.HasIndex(p => p.PspTransactionId)
            .HasDatabaseName("IX_Payments_PspTransactionId");

        // FK to Orders
        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(p => p.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        // FK to Customers
        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(p => p.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

// ── OutboxMessages ────────────────────────────────────────────────────────────

public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.EventType)
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(m => m.Payload)
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.Property(m => m.Status)
            .HasConversion<byte>();

        builder.Property(m => m.ErrorMessage)
            .HasMaxLength(2000);

        // Partial index: only Pending + Failed rows — keeps index small
        builder.HasIndex(m => new { m.Status, m.NextRetryAt })
            .HasFilter("[Status] IN (0, 3)")
            .HasDatabaseName("IX_OutboxMessages_Status_NextRetry");
    }
}
