using FluentAssertions;
using Pixelz.Domain.Orders;
using Pixelz.Domain.Orders.Events;

namespace Pixelz.Domain.Tests;

public class OrderTests
{
    private static readonly Guid CustomerId = Guid.NewGuid();

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_WithValidData_ShouldHaveDraftStatus()
    {
        var order = Order.Create(CustomerId, "Campaign Q4");

        order.Status.Should().Be(OrderStatus.Draft);
        order.OrderName.Should().Be("Campaign Q4");
        order.CustomerId.Should().Be(CustomerId);
        order.Items.Should().BeEmpty();
        order.TotalAmount.Amount.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankName_ShouldThrow(string name)
    {
        var act = () => Order.Create(CustomerId, name);
        act.Should().Throw<ArgumentException>();
    }

    // ── AddItem ───────────────────────────────────────────────────────────────

    [Fact]
    public void AddItem_ShouldAccumulateTotalCorrectly()
    {
        var order = Order.Create(CustomerId, "Test Order");

        order.AddItem("BACKGROUND_REMOVAL", 5, 10.00m);
        order.AddItem("COLOR_CORRECTION",   3, 15.00m);

        order.Items.Should().HaveCount(2);
        order.TotalAmount.Amount.Should().Be(95.00m); // 5*10 + 3*15
    }

    [Fact]
    public void AddItem_WhenNotDraft_ShouldReturnFailure()
    {
        var order = CreateOrderWithItem();
        order.InitiateCheckout();

        var result = order.AddItem("SERVICE", 1, 10m);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("PendingPayment");
    }

    // ── InitiateCheckout ──────────────────────────────────────────────────────

    [Fact]
    public void InitiateCheckout_WithItems_ShouldSucceedAndRaiseEvent()
    {
        var order = CreateOrderWithItem();

        var result = order.InitiateCheckout();

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.PendingPayment);
        order.DomainEvents.Should().ContainSingle(e => e is OrderCheckoutInitiatedEvent);
    }

    [Fact]
    public void InitiateCheckout_WithNoItems_ShouldFail()
    {
        var order = Order.Create(CustomerId, "Empty Order");

        var result = order.InitiateCheckout();

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("at least one item");
        order.Status.Should().Be(OrderStatus.Draft);
    }

    [Fact]
    public void InitiateCheckout_WhenAlreadyPendingPayment_ShouldFail()
    {
        var order = CreateOrderWithItem();
        order.InitiateCheckout();

        var result = order.InitiateCheckout();

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("PendingPayment");
    }

    // ── MarkAsPaid ────────────────────────────────────────────────────────────

    [Fact]
    public void MarkAsPaid_ShouldTransitionAndRaiseCheckoutSucceededEvent()
    {
        var order     = CreateOrderWithItem();
        var paymentId = Guid.NewGuid();
        order.InitiateCheckout();

        order.MarkAsPaid(paymentId, "psp_txn_abc123");

        order.Status.Should().Be(OrderStatus.Paid);
        order.CheckedOutAt.Should().NotBeNull();

        var evt = order.DomainEvents
            .OfType<CheckoutSucceededEvent>()
            .Should().ContainSingle().Subject;

        evt.OrderId.Should().Be(order.Id);
        evt.PspTransactionId.Should().Be("psp_txn_abc123");
        evt.TotalAmount.Amount.Should().Be(100m);
    }

    [Fact]
    public void MarkAsPaid_WhenNotPendingPayment_ShouldThrow()
    {
        var order = CreateOrderWithItem();

        var act = () => order.MarkAsPaid(Guid.NewGuid(), "txn");
        act.Should().Throw<InvalidOperationException>();
    }

    // ── MarkPaymentFailed ─────────────────────────────────────────────────────

    [Fact]
    public void MarkPaymentFailed_ShouldRevertToDraftAndRaiseEvent()
    {
        var order = CreateOrderWithItem();
        order.InitiateCheckout();

        order.MarkPaymentFailed("Insufficient funds");

        order.Status.Should().Be(OrderStatus.Draft);
        order.DomainEvents.Should().Contain(e => e is PaymentFailedEvent);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Order CreateOrderWithItem()
    {
        var order = Order.Create(CustomerId, "Test Order");
        order.AddItem("BACKGROUND_REMOVAL", 1, 100.00m);
        return order;
    }
}
