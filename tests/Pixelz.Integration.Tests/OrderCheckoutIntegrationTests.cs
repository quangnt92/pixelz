using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pixelz.Application.Interfaces;
using Pixelz.Application.Orders.Commands;
using Pixelz.Application.Orders.EventHandlers;
using Pixelz.Application.Orders.Queries;
using Pixelz.Domain.Orders;
using Pixelz.Infrastructure.ExternalServices;
using Pixelz.Infrastructure.Persistence;

namespace Pixelz.Integration.Tests;

/// <summary>
/// Integration tests exercising Application + Infrastructure stack
/// với in-memory EF Core database. Không dùng WebApplicationFactory
/// để tránh lỗi testhost.deps.json.
/// </summary>
public class OrderCheckoutIntegrationTests : IDisposable {
    private readonly ServiceProvider _serviceProvider;
    private readonly IMediator _mediator;
    private readonly IOrderRepository _orderRepo;
    private readonly PixelzDbContext _db;
    private readonly Guid _customerId = Guid.NewGuid();

    public OrderCheckoutIntegrationTests() {
        var services = new ServiceCollection();

        // ── In-memory EF Core ─────────────────────────────────────────────────
        // ConfigureWarnings: suppress TransactionIgnoredWarning vì in-memory
        // provider không hỗ trợ transaction — hành vi checkout vẫn đúng trong test.
        services.AddDbContext<PixelzDbContext>(opts =>
            opts.UseInMemoryDatabase($"PixelzTest_{Guid.NewGuid():N}")
                .ConfigureWarnings(w =>
                    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics
                             .InMemoryEventId.TransactionIgnoredWarning)));

        services.AddScoped<IPixelzDbContext>(sp =>
            sp.GetRequiredService<PixelzDbContext>());

        // ── Repositories & UoW ────────────────────────────────────────────────
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // ── Mock external services ────────────────────────────────────────────
        services.AddScoped<IPaymentService, MockPaymentService>();
        services.AddScoped<IEmailService, MockEmailService>();
        services.AddScoped<IInvoiceService, MockInvoiceService>();
        services.Configure<MockProductionOptions>(_ => { });
        services.AddScoped<IProductionService, MockProductionService>();

        // ── Event handlers ────────────────────────────────────────────────────
        services.AddScoped<CheckoutSucceededEventHandler>();

        // ── MediatR — đăng ký tất cả handlers từ Application assembly ─────────
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(
                typeof(CheckoutOrderCommand).Assembly));

        // ── Logging ───────────────────────────────────────────────────────────
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        _serviceProvider = services.BuildServiceProvider();

        // Resolve các service dùng chung cho tất cả test trong class này
        var scope = _serviceProvider.CreateScope();
        _mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        _orderRepo = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
        _db = scope.ServiceProvider.GetRequiredService<PixelzDbContext>();
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private async Task<Order> CreateDraftOrderAsync(
        string name = "Test Campaign", Guid? customerId = null) {
        var order = Order.Create(customerId ?? _customerId, name);
        order.AddItem("BACKGROUND_REMOVAL", 2, 50m);
        await _orderRepo.AddAsync(order);
        await _orderRepo.SaveChangesAsync();
        return order;
    }

    private CheckoutOrderCommand BuildCheckoutCommand(Guid orderId, string token = "tok_ok")
        => new(orderId, _customerId,
               new PaymentMethodDto("card", token),
               $"idem-{Guid.NewGuid():N}");

    // ── SearchOrders ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchOrders_ShouldReturnOnlyCurrentCustomerOrders() {
        await CreateDraftOrderAsync("My Campaign");

        // Tạo order cho customer khác — không được xuất hiện trong kết quả
        var other = Order.Create(Guid.NewGuid(), "Other Customer Order");
        other.AddItem("SVC", 1, 10m);
        await _orderRepo.AddAsync(other);
        await _orderRepo.SaveChangesAsync();

        var result = await _mediator.Send(
            new SearchOrdersQuery(_customerId));

        result.Data.Should().HaveCount(1);
        result.Data[0].OrderName.Should().Be("My Campaign");
    }

    [Fact]
    public async Task SearchOrders_FilterByName_ShouldReturnMatchingOrders() {
        await CreateDraftOrderAsync("Spring Campaign 2025");
        await CreateDraftOrderAsync("Winter Sale");

        var result = await _mediator.Send(
            new SearchOrdersQuery(_customerId, Name: "Spring"));

        result.Data.Should().HaveCount(1);
        result.Data[0].OrderName.Should().Contain("Spring");
    }

    [Fact]
    public async Task SearchOrders_FilterByStatus_ShouldReturnDraftOnly() {
        await CreateDraftOrderAsync("Draft Order");

        var result = await _mediator.Send(
            new SearchOrdersQuery(_customerId, Status: OrderStatus.Draft));

        result.Data.Should().AllSatisfy(o =>
            o.Status.Should().Be("Draft"));
    }

    [Fact]
    public async Task SearchOrders_NoOrders_ShouldReturnEmptyResult() {
        var result = await _mediator.Send(
            new SearchOrdersQuery(Guid.NewGuid()));

        result.Data.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    // ── Checkout — Happy Path ─────────────────────────────────────────────────

    [Fact]
    public async Task Checkout_ValidToken_ShouldSucceedAndOrderBecomePaid() {
        var order = await CreateDraftOrderAsync();
        var result = await _mediator.Send(BuildCheckoutCommand(order.Id));

        result.IsSuccess.Should().BeTrue();
        result.PspTransactionId.Should().StartWith("mock_ch_");

        var persisted = await _orderRepo.GetByIdAsync(order.Id);
        persisted!.Status.Should().Be(OrderStatus.Paid);
    }

    [Fact]
    public async Task Checkout_Success_ShouldCreateOutboxMessages() {
        var order = await CreateDraftOrderAsync();
        await _mediator.Send(BuildCheckoutCommand(order.Id));

        var outbox = _db.OutboxMessages.ToList();
        outbox.Should().NotBeEmpty();
        outbox.Should().Contain(m => m.EventType == "CheckoutSucceededEvent");
    }

    // ── Checkout — Payment Failures ───────────────────────────────────────────

    [Fact]
    public async Task Checkout_DeclinedToken_ShouldFailAndOrderRemainDraft() {
        var order = await CreateDraftOrderAsync();
        var result = await _mediator.Send(
            BuildCheckoutCommand(order.Id, "tok_declined_0000"));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("card_declined");

        var persisted = await _orderRepo.GetByIdAsync(order.Id);
        persisted!.Status.Should().Be(OrderStatus.Draft);
    }

    [Fact]
    public async Task Checkout_InsufficientFundsToken_ShouldFail() {
        var order = await CreateDraftOrderAsync();
        var result = await _mediator.Send(
            BuildCheckoutCommand(order.Id, "tok_broke_9999"));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("insufficient_funds");
    }

    // ── Checkout — Business Rule Violations ───────────────────────────────────

    [Fact]
    public async Task Checkout_NonExistentOrder_ShouldReturnNotFound() {
        var result = await _mediator.Send(
            BuildCheckoutCommand(Guid.NewGuid()));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Checkout_AlreadyPaidOrder_ShouldReturnBusinessError() {
        var order = await CreateDraftOrderAsync();

        // Checkout lần 1 — thành công
        await _mediator.Send(BuildCheckoutCommand(order.Id));

        // Checkout lần 2 — phải thất bại
        var result = await _mediator.Send(BuildCheckoutCommand(order.Id));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_ERROR");
    }

    [Fact]
    public async Task Checkout_OrderBelongsToOtherCustomer_ShouldReturnNotFound() {
        // Order thuộc customer khác
        var otherOrder = Order.Create(Guid.NewGuid(), "Other Customer");
        otherOrder.AddItem("SVC", 1, 50m);
        await _orderRepo.AddAsync(otherOrder);
        await _orderRepo.SaveChangesAsync();

        // Attacker dùng customerId của mình để checkout order người khác
        var command = new CheckoutOrderCommand(
            otherOrder.Id,
            _customerId,          // ← sai customerId
            new PaymentMethodDto("card", "tok_ok"),
            $"idem-{Guid.NewGuid():N}");

        var result = await _mediator.Send(command);

        // Phải trả NOT_FOUND, không để lộ order tồn tại
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    // ── GetOrderById ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetOrderById_ValidId_ShouldReturnOrderDetail() {
        var order = await CreateDraftOrderAsync("Detail Test");
        var result = await _mediator.Send(
            new GetOrderByIdQuery(order.Id, _customerId));

        result.Should().NotBeNull();
        result.OrderName.Should().Be("Detail Test");
        result.Status.Should().Be("Draft");
        result.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetOrderById_OtherCustomerOrder_ShouldThrowNotFound() {
        var other = Order.Create(Guid.NewGuid(), "Not Mine");
        other.AddItem("SVC", 1, 10m);
        await _orderRepo.AddAsync(other);
        await _orderRepo.SaveChangesAsync();

        var act = async () => await _mediator.Send(
            new GetOrderByIdQuery(other.Id, _customerId));

        await act.Should().ThrowAsync<Pixelz.Domain.Common.NotFoundException>();
    }

    // ── CreateOrder ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateOrder_ValidData_ShouldPersistAndReturnId() {
        var command = new CreateOrderCommand(
            _customerId,
            "New Order via MediatR",
            "Integration test order",
            new[]
            {
                new CreateOrderItemDto("BACKGROUND_REMOVAL", 5, 8.50m),
                new CreateOrderItemDto("COLOR_CORRECTION",   3, 12.00m)
            });

        var result = await _mediator.Send(command);

        result.IsSuccess.Should().BeTrue();
        result.OrderId.Should().NotBeNull();

        var persisted = await _orderRepo.GetByIdWithItemsAsync(result.OrderId!.Value);
        persisted.Should().NotBeNull();
        persisted!.Items.Should().HaveCount(2);
        persisted.TotalAmount.Amount.Should().Be(78.50m); // 5*8.5 + 3*12
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose() => _serviceProvider.Dispose();
}