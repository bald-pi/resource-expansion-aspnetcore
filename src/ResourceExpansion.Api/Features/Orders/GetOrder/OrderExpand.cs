namespace ResourceExpansion.Api.Features.Orders.GetOrder;

// Query string for GET /api/orders/{id}?expand=...&relatedLimit=...
public sealed record GetOrderQuery(string? Expand, int? RelatedLimit);

// The validated expansion plan. Request strings are only compared against the allowlist;
// they never reach EF Core.
public sealed record OrderExpand(bool Customer, bool Items, int RelatedLimit)
{
    public const int DefaultRelatedLimit = 2;
    public const int MaxRelatedLimit = 50;
    // There is no universal depth limit. OData's ASP.NET Core default is 2 and Stripe allows 4.
    // Choose the smallest depth your clients actually need.
    public const int MaxDepth = 2;

    public static readonly string[] Allowed = ["customer", "items"];

    public static HashSet<string> SplitPaths(string? expand) => (expand ?? "")
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Call only after GetOrderQueryValidator has accepted the query.
    public static OrderExpand From(GetOrderQuery query)
    {
        var paths = SplitPaths(query.Expand);
        return new OrderExpand(
            Customer: paths.Contains("customer"),
            Items: paths.Contains("items"),
            RelatedLimit: query.RelatedLimit ?? DefaultRelatedLimit);
    }
}
