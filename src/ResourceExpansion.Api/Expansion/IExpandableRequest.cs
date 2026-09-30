namespace ResourceExpansion.Api.Expansion;

// Implemented by any request that supports ?expand=...&relatedLimit=...
public interface IExpandableRequest
{
    string? Expand { get; }
    int? RelatedLimit { get; }
}
