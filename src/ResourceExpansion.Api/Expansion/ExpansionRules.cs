namespace ResourceExpansion.Api.Expansion;

// The allowlist for one resource: each expandable path maps to a typed EF Core Include.
// Request strings are only compared against these paths; they never reach EF Core.
public sealed class ExpansionRules<TEntity> where TEntity : class
{
    private readonly List<(string Path, Func<IQueryable<TEntity>, int, IQueryable<TEntity>> Include)> rules = [];

    public IReadOnlyList<string> Allowed => rules.Select(rule => rule.Path).ToList();

    // The include receives the number of related rows to fetch. A nested path's include
    // must load its parents too, for example Include(...).ThenInclude(...).
    public ExpansionRules<TEntity> Allow(string path, Func<IQueryable<TEntity>, int, IQueryable<TEntity>> include)
    {
        rules.Add((path, include));
        return this;
    }

    public IQueryable<TEntity> Apply(IQueryable<TEntity> query, ExpandPlan plan)
    {
        // Fetch one extra related row so the response can report hasMore without a COUNT query.
        var take = plan.RelatedLimit + 1;

        foreach (var (path, include) in rules)
        {
            // Skip a parent when a selected nested path's include already loads it.
            if (plan.Has(path) && !plan.Paths.Any(other => other.StartsWith(path + '.')))
                query = include(query, take);
        }

        return query;
    }
}

public static class ExpansionQueryableExtensions
{
    public static IQueryable<TEntity> WithExpansions<TEntity>(this IQueryable<TEntity> query,
        ExpansionRules<TEntity> rules, ExpandPlan plan) where TEntity : class => rules.Apply(query, plan);
}
