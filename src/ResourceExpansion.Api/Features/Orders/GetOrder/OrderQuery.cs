using Microsoft.EntityFrameworkCore;
using ResourceExpansion.Api.Domain;

namespace ResourceExpansion.Api.Features.Orders.GetOrder;

public static class OrderQuery
{
    public static IQueryable<Order> WithExpansions(this IQueryable<Order> query, OrderExpand expand)
    {
        // Fetch one extra item so the response can report hasMore without a COUNT query.
        var take = expand.RelatedLimit + 1;

        if (expand.Customer)
            query = query.Include(order => order.Customer);

        if (expand.Items)
            query = query.Include(order => order.Items.OrderBy(item => item.Id).Take(take));

        return query;
    }
}
