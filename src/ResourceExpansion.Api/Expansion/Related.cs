namespace ResourceExpansion.Api.Expansion;

// A bounded preview of a related collection. The query loaded up to limit + 1 rows;
// the extra one only signals HasMore.
public sealed record Related<T>(IReadOnlyList<T> Data, int Limit, bool HasMore);

public static class Related
{
    public static Related<TResult> From<TSource, TResult>(IReadOnlyCollection<TSource> rows, int limit,
        Func<TSource, TResult> map) => new(
        Data: rows.Take(limit).Select(map).ToList(),
        Limit: limit,
        HasMore: rows.Count > limit);
}
