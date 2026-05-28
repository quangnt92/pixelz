using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pixelz.Application.Interfaces;
using Pixelz.Application.Orders.Commands;
using Pixelz.Domain.Orders;

namespace Pixelz.Application.Tests;

public class CheckoutOrderCommandHandlerTests
{
    private readonly IOrderRepository _repo = Substitute.For<IOrderRepository>();
    private readonly IPaymentService _psp = Substitute.For<IPaymentService>();
    private readonly IUnitOfWork _uow  = Substitute.For<IUnitOfWork>();

    private CheckoutOrderCommandHandler CreateHandler() => new(_repo, _psp, _uow, NullLogger<CheckoutOrderCommandHandler>.Instance);

    private static (Order order, CheckoutOrderCommand command) BuildScenario(Guid? customerId = null)
    {
        var cid   = customerId ?? Guid.NewGuid();
        var order = Order.Create(cid, "Test Campaign");
        order.AddItem("BACKGROUND_REMOVAL", 2, 50m);
        var command = new CheckoutOrderCommand(order.Id, cid, new PaymentMethodDto("card", "tok_success_1234"), $"idem-{Guid.NewGuid():N}");
        return (order, command);
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_PaymentSucceeds_ShouldReturnSuccessAndMarkOrderPaid()
    {
        var (order, command) = BuildScenario();

        _repo.GetByIdWithItemsAsync(order.Id, default).Returns(order);
        _psp.ChargeAsync(Arg.Any<ChargeRequest>(), default)
            .Returns(PaymentResult.Success(Guid.NewGuid(), "psp_ch_ok123"));
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<Task>>(), default)
            .Returns(info => ((Func<Task>)info[0])());

        var result = await CreateHandler().Handle(command, default);

        result.IsSuccess.Should().BeTrue();
        result.PspTransactionId.Should().Be("psp_ch_ok123");
        order.Status.Should().Be(OrderStatus.Paid);
    }

    // ── Payment failure ───────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_PaymentFails_ShouldReturnFailedAndRevertOrderToDraft()
    {
        var (order, command) = BuildScenario();

        _repo.GetByIdWithItemsAsync(order.Id, default).Returns(order);
        _psp.ChargeAsync(Arg.Any<ChargeRequest>(), default)
            .Returns(PaymentResult.Failed("card_declined", "Your card was declined"));

        var result = await CreateHandler().Handle(command, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("card_declined");
        order.Status.Should().Be(OrderStatus.Draft); // reverted to Draft

        await _psp.Received(1).ChargeAsync(Arg.Any<ChargeRequest>(), default);
    }

    // ── Order not found ───────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_OrderNotFound_ShouldReturnNotFoundWithoutCallingPSP()
    {
        _repo.GetByIdWithItemsAsync(Arg.Any<Guid>(), default).Returns((Order?)null);

        var command = new CheckoutOrderCommand(
            Guid.NewGuid(), Guid.NewGuid(),
            new PaymentMethodDto("card", "tok_any"),
            "idem-key");

        var result = await CreateHandler().Handle(command, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
        await _psp.DidNotReceive().ChargeAsync(Arg.Any<ChargeRequest>(), default);
    }

    // ── IDOR protection ───────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_OrderBelongsToDifferentCustomer_ShouldReturnNotFound()
    {
        var realOwner   = Guid.NewGuid();
        var attacker    = Guid.NewGuid();
        var (order, _)  = BuildScenario(realOwner);

        _repo.GetByIdWithItemsAsync(order.Id, default).Returns(order);

        var command = new CheckoutOrderCommand(
            order.Id, attacker, // attacker tries to checkout another customer's order
            new PaymentMethodDto("card", "tok_any"),
            "idem-key");

        var result = await CreateHandler().Handle(command, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND"); // don't reveal the order exists
        await _psp.DidNotReceive().ChargeAsync(Arg.Any<ChargeRequest>(), default);
    }

    // ── Business rule violation ───────────────────────────────────────────────

    [Fact]
    public async Task Handle_OrderAlreadyPaid_ShouldReturnBusinessError()
    {
        var (order, command) = BuildScenario();
        order.InitiateCheckout();
        order.MarkAsPaid(Guid.NewGuid(), "old_txn");

        _repo.GetByIdWithItemsAsync(order.Id, default).Returns(order);

        var result = await CreateHandler().Handle(command, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_ERROR");
        await _psp.DidNotReceive().ChargeAsync(Arg.Any<ChargeRequest>(), default);
    }
}
