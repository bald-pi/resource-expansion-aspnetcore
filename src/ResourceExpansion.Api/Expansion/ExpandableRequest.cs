namespace ResourceExpansion.Api.Expansion;

// Base for any request that supports ?expand=...&relatedLimit=...
public abstract record ExpandableRequest(string? Expand, int? RelatedLimit);
