namespace ResourceExpansion.Api.Expansion;

// The validated expansion plan. Paths are lowercase and include their implied parents,
// so "visits.club" also selects "visits".
public sealed record ExpandPlan(IReadOnlySet<string> Paths, int RelatedLimit)
{
    public const int DefaultRelatedLimit = 2;
    public const int MaxRelatedLimit = 50;
    // There is no universal depth limit. OData's ASP.NET Core default is 2 and Stripe allows 4.
    // Choose the smallest depth your clients actually need.
    public const int MaxDepth = 2;

    public bool Has(string path) => Paths.Contains(path);

    public static HashSet<string> SplitPaths(string? expand) => (expand ?? "")
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(path => path.ToLowerInvariant())
        .ToHashSet();

    // Call only after ExpandRequestValidator has accepted the request.
    public static ExpandPlan From(IExpandableRequest request)
    {
        var paths = new HashSet<string>();
        foreach (var path in SplitPaths(request.Expand))
        {
            var segments = path.Split('.');
            for (var length = 1; length <= segments.Length; length++)
                paths.Add(string.Join('.', segments[..length]));
        }

        return new ExpandPlan(paths, request.RelatedLimit ?? DefaultRelatedLimit);
    }
}
