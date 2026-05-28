using MediatR;
using Microsoft.EntityFrameworkCore;
using Pixelz.Application.Interfaces;
using Pixelz.Domain.Common;

namespace Pixelz.Application.Orders.Queries;

public record GetOrderByIdQuery(Guid OrderId, Guid CustomerId)
    : IRequest<OrderDetailDto>;

public record OrderDetailDto(
    Guid     Id,
    string   OrderName,
    string   Status,
    decimal  TotalAmount,
    string   Currency,
    string?  Description,
    string?  InternalOrderId,
    string?  InvoiceId,
    DateTime CreatedAt,
    DateTime? CheckedOutAt,
    IReadOnlyList<OrderItemDto> Items);

public record OrderItemDto(
    Guid    Id,
    string  ServiceType,
    int     Quantity,
    decimal UnitPrice,
    decimal TotalPrice,
    string  Currency);

public class GetOrderByIdQueryHandler(IPixelzDbContext db) : IRequestHandler<GetOrderByIdQuery, OrderDetailDto>
{
    public async Task<OrderDetailDto> Handle(
        GetOrderByIdQuery request, CancellationToken cancellationToken)
    {
        var order = await db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.Id == request.OrderId && o.CustomerId == request.CustomerId)
            .Select(o => new OrderDetailDto(
                o.Id,
                o.OrderName,
                o.Status.ToString(),
                o.TotalAmount.Amount,
                o.TotalAmount.Currency,
                o.Description,
                o.InternalOrderId,
                o.InvoiceId,
                o.CreatedAt,
                o.CheckedOutAt,
                o.Items.Select(i => new OrderItemDto(
                    i.Id,
                    i.ServiceType,
                    i.Quantity,
                    i.UnitPrice.Amount,
                    i.TotalPrice.Amount,
                    i.UnitPrice.Currency
                )).ToList()))
            .FirstOrDefaultAsync(cancellationToken);

        return order is null ? throw new NotFoundException("Order", request.OrderId) : order;
    }
}
