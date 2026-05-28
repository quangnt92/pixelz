using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pixelz.Application.Interfaces;
using Pixelz.Application.Orders.Commands;
using Pixelz.Application.Orders.Queries;
using Pixelz.Domain.Common;
using Pixelz.Domain.Orders;

namespace Pixelz.API.Controllers;

[ApiController]
[Route("api/v1/orders")]
[Authorize]
[Produces("application/json")]
public class OrdersController(IMediator mediator, ILogger<OrdersController> logger) : ControllerBase
{
    private Guid CurrentCustomerId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new UnauthorizedAccessException("Invalid user identity in token");

    // ── POST /api/v1/orders ───────────────────────────────────────────────────

    /// <summary>Tạo đơn hàng mới.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(CreateOrderResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(
        [FromBody] CreateOrderRequestDto request,
        CancellationToken ct = default)
    {
        logger.LogInformation("CreateOrder. CustomerId={CustomerId} OrderName={Name}", CurrentCustomerId, request.OrderName);

        var command = new CreateOrderCommand(CurrentCustomerId, request.OrderName, request.Description, request.Items.Select(i => new CreateOrderItemDto(i.ServiceType, i.Quantity, i.UnitPrice, i.Currency ?? "USD")));
        var result = await mediator.Send(command, ct);
        if (!result.IsSuccess) return BadRequest(new ApiErrorDto(result.ErrorCode!, result.ErrorMessage!));

        return CreatedAtAction(nameof(GetById), new { id = result.OrderId }, new CreateOrderResponseDto(result.OrderId!.Value, "Draft", "Tạo đơn hàng thành công."));
    }

    // ── GET /api/v1/orders ────────────────────────────────────────────────────

    /// <summary>Tìm kiếm và lọc đơn hàng theo tên / trạng thái.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<OrderSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchOrders(
        [FromQuery] string? name,
        [FromQuery] OrderStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string sortBy = "createdAt",
        [FromQuery] string sortDir = "desc",
        CancellationToken ct = default)
    {
        logger.LogInformation("SearchOrders. CustomerId={CustomerId} Name={Name} Status={Status}", CurrentCustomerId, name, status);
        var result = await mediator.Send(new SearchOrdersQuery(CurrentCustomerId, name, status, page, pageSize, sortBy, sortDir), ct);
        return Ok(result);
    }

    // ── GET /api/v1/orders/{id} ───────────────────────────────────────────────

    /// <summary>Lấy chi tiết đơn hàng kèm danh sách items.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(OrderDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct = default)
    {
        logger.LogInformation("GetOrderById. OrderId={OrderId} CustomerId={CustomerId}", id, CurrentCustomerId);
        var result = await mediator.Send(new GetOrderByIdQuery(id, CurrentCustomerId), ct);
        return Ok(result);
    }

    // ── POST /api/v1/orders/{id}/checkout ─────────────────────────────────────

    /// <summary>
    /// Thanh toán đơn hàng.
    /// Test tokens: suffix 0000 = card_declined, 9999 = insufficient_funds, else = success.
    /// </summary>
    [HttpPost("{id:guid}/checkout")]
    [ProducesResponseType(typeof(CheckoutResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorDto), StatusCodes.Status402PaymentRequired)]
    [ProducesResponseType(typeof(ApiErrorDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Checkout(
        Guid id,
        [FromBody] CheckoutRequestDto request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct = default)
    {
        idempotencyKey ??= $"checkout-{id:N}-{Guid.NewGuid():N}";

        logger.LogInformation("Checkout requested. OrderId={OrderId} CustomerId={CustomerId} IdempotencyKey={Key}", id, CurrentCustomerId, idempotencyKey);

        var command = new CheckoutOrderCommand(id, CurrentCustomerId, new PaymentMethodDto(request.PaymentMethod.Type, request.PaymentMethod.Token), idempotencyKey);
        var result = await mediator.Send(command, ct);

        return result.ErrorCode switch
        {
            null => Ok(new CheckoutResponseDto(result.OrderId!.Value, "Paid", result.PspTransactionId!, "Thanh toán thành công. Email xác nhận đã được gửi.")),
            "NOT_FOUND" => NotFound(new ApiErrorDto(result.ErrorCode, result.ErrorMessage!)),
            "BUSINESS_ERROR" => BadRequest(new ApiErrorDto(result.ErrorCode, result.ErrorMessage!)),
            _ => StatusCode(StatusCodes.Status402PaymentRequired, new ApiErrorDto(result.ErrorCode!, result.ErrorMessage!))
        };
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public record CreateOrderItemRequestDto(
    string  ServiceType,
    int     Quantity,
    decimal UnitPrice,
    string? Currency = "USD");

public record CreateOrderRequestDto(
    string  OrderName,
    string? Description,
    List<CreateOrderItemRequestDto> Items);

public record CreateOrderResponseDto(Guid Id, string Status, string Message);

public record CheckoutRequestDto(PaymentMethodRequestDto PaymentMethod);
public record PaymentMethodRequestDto(string Type, string Token);

public record CheckoutResponseDto(
    Guid   OrderId,
    string Status,
    string PaymentTransactionId,
    string Message);

public record ApiErrorDto(string Error, string Message);
