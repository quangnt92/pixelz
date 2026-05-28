using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pixelz.Application.Interfaces;
using Pixelz.Application.Orders.EventHandlers;
using Pixelz.Infrastructure.ExternalServices;
using Pixelz.Infrastructure.Outbox;
using Pixelz.Infrastructure.Persistence;

namespace Pixelz.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // ── Database ──────────────────────────────────────────────────────────

        services.AddDbContext<PixelzDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("Default"),
                sql =>
                {
                    sql.EnableRetryOnFailure(
                        maxRetryCount: 3,
                        maxRetryDelay: TimeSpan.FromSeconds(10),
                        errorNumbersToAdd: null);
                    sql.CommandTimeout(30);
                })
            .EnableSensitiveDataLogging(environment.IsDevelopment())
            .EnableDetailedErrors(environment.IsDevelopment()));

        services.AddScoped<IPixelzDbContext>(sp => sp.GetRequiredService<PixelzDbContext>());

        // ── Repositories ──────────────────────────────────────────────────────

        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // ── External services (Mock — swap with real impl in production) ───────

        services.AddScoped<IPaymentService, MockPaymentService>();
        services.AddScoped<IEmailService,   MockEmailService>();
        services.AddScoped<IInvoiceService, MockInvoiceService>();

        services.Configure<MockProductionOptions>(
            configuration.GetSection("MockServices:Production"));
        services.AddScoped<IProductionService, MockProductionService>();

        // ── Event Handlers ────────────────────────────────────────────────────

        services.AddScoped<CheckoutSucceededEventHandler>();

        // ── Outbox background worker ──────────────────────────────────────────

        services.AddHostedService<OutboxProcessor>();

        return services;
    }
}