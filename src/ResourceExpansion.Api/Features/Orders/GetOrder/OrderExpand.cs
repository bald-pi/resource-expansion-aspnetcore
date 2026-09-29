namespace ResourceExpansion.Api.Features.Orders.GetOrder;

// Query string for GET /api/orders/{id}?expand=...&relatedLimit=...
public sealed record GetOrderQuery(string? Expand, int? RelatedLimit);

// The validated expansion plan. Request strings are only compared against the allowlist;
// they never reach EF Core.
public sealed record OrderExpand(bool Customer, bool Items, bool ItemProducts, int RelatedLimit)
{
    public const int DefaultRelatedLimit = 2;
    public const int MaxRelatedLimit = 50;
    // There is no universal depth limit. OData's ASP.NET Core default is 2 and Stripe allows 4.
    // Choose the smallest depth your clients actually need.
    public const int MaxDepth = 2;

    public static readonly string[] Allowed = ["customer", "items", "items.product"];

    public static HashSet<string> SplitPaths(string? expand) => (expand ?? "")
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Call only after GetOrderQueryValidator has accepted the query.
    public static OrderExpand From(GetOrderQuery query)
    {
        var paths = SplitPaths(query.Expand);

        // Expanding items.product implies items.
        var itemProducts = paths.Contains("items.product");
        return new OrderExpand(
            Customer: paths.Contains("customer"),
            Items: itemProducts || paths.Contains("items"),
            ItemProducts: itemProducts,
            RelatedLimit: query.RelatedLimit ?? DefaultRelatedLimit);
    }
}
