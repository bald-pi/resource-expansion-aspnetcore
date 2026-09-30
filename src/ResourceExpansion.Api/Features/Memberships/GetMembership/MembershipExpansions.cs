using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using ResourceExpansion.Api.Domain;
using ResourceExpansion.Api.Expansion;

namespace ResourceExpansion.Api.Features.Memberships.GetMembership;

// Everything a client may expand on a membership. Adding an expansion is one Allow call
// plus a field in the response.
public static class MembershipExpansions
{
    public static readonly ExpansionRules<Membership> Rules = new ExpansionRules<Membership>()
        .Allow("member", (query, _) => query.Include(membership => membership.Member))
        .Allow("visits", IncludeVisits)
        .Allow("visits.club", (query, take) => IncludeVisits(query, take).ThenInclude(visit => visit.Club));

    // Most recent visits first; Id breaks ties so the preview is stable.
    private static IIncludableQueryable<Membership, IEnumerable<Visit>> IncludeVisits(IQueryable<Membership> query, int take) =>
        query.Include(membership => membership.Visits
            .OrderByDescending(visit => visit.CheckedInAt).ThenByDescending(visit => visit.Id).Take(take));
}
