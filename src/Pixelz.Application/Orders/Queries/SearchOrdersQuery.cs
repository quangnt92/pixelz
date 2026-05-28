using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pixelz.Application.Interfaces;
using Pixelz.Domain.Orders;

namespace Pixelz.Application.Orders.Queries;

// ── Query ─────────────────────────────────────────────────────────────────────

public record SearchOrdersQuery(
    Guid         CustomerId,
    string?      Name     = null,
    OrderStatus? Status   = null,
    int          Page     = 1,
    int          PageSize = 20,
    string       SortBy   = "createdAt",
    string       SortDir  = "desc"
) : IRequest<PagedResult<OrderSummaryDto>>;

// ── DTOs ──────────────────────────────────────────────────────────────────────

public record OrderSummaryDto(
    Guid      Id,
    string    OrderName,
    string    Status,
    decimal   TotalAmount,
    string    Currency,
    int       ItemCount,
    DateTime  CreatedAt,
    DateTime? CheckedOutAt);

public record PagedResult<T>(
    IReadOnlyList<T> Data,
    int TotalCount,
    int Page,
    int PageSize)
{
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
    public bool HasNextPage => Page < TotalPages;
    public bool HasPreviousPage => Page > 1;
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class SearchOrdersQueryHandler(IPixelzDbContext db, ILogger<SearchOrdersQueryHandler> logger)
        : IRequestHandler<SearchOrdersQuery, PagedResult<OrderSummaryDto>>
{
    public async Task<PagedResult<OrderSummaryDto>> Handle(
        SearchOrdersQuery query, CancellationToken cancellationToken)
    {
        logger.LogDebug("SearchOrders CustomerId={CustomerId} Name={Name} Status={Status} Page={Page}", query.CustomerId, query.Name, query.Status, query.Page);

        var queryable = db.Orders.AsNoTracking().Where(o => o.CustomerId == query.CustomerId);
        if (!string.IsNullOrWhiteSpace(query.Name))
        {
            var pattern = $"%{query.Name.Trim()}%";
            queryable = queryable.Where(o => EF.Functions.Like(o.OrderName, pattern));
        }

        if (query.Status.HasValue) queryable = queryable.Where(o => o.Status == query.Status.Value);
        var totalCount = await queryable.CountAsync(cancellationToken);
        queryable = (query.SortBy.ToLowerInvariant(), query.SortDir.ToLowerInvariant()) switch
        {
            ("name", "asc") => queryable.OrderBy(o => o.OrderName),
            ("name", _) => queryable.OrderByDescending(o => o.OrderName),
            ("amount", "asc") => queryable.OrderBy(o => o.TotalAmount.Amount),
            ("amount", _) => queryable.OrderByDescending(o => o.TotalAmount.Amount),
            ("createdat", "asc") => queryable.OrderBy(o => o.CreatedAt),
            _ => queryable.OrderByDescending(o => o.CreatedAt)
        };

        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var page = Math.Max(1, query.Page);
        var items = await queryable
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new OrderSummaryDto(
                o.Id,
                o.OrderName,
                o.Status.ToString(),
                o.TotalAmount.Amount,
                o.TotalAmount.Currency,
                o.Items.Count,
                o.CreatedAt,
                o.CheckedOutAt))
            .ToListAsync(cancellationToken);

        logger.LogDebug("SearchOrders returned {Count}/{Total}", items.Count, totalCount);

        return new PagedResult<OrderSummaryDto>(items, totalCount, page, pageSize);
    }
}