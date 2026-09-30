using ResourceExpansion.Api.Expansion;

namespace ResourceExpansion.Api.Features.Memberships.GetMembership;

// Route and query string for GET /api/memberships/{id}?expand=...&relatedLimit=...
public sealed record GetMembershipRequest(int Id, string? Expand, int? RelatedLimit)
    : ExpandableRequest(Expand, RelatedLimit);
