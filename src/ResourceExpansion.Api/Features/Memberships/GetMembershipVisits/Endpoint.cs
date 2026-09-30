using Microsoft.EntityFrameworkCore;
using ResourceExpansion.Api.Domain;
using ResourceExpansion.Api.Expansion;
using ResourceExpansion.Api.Features.Memberships.GetMembership;
using ResourceExpansion.Api.Infrastructure;

namespace ResourceExpansion.Api.Features.Memberships.GetMembershipVisits;

// A standalone sub-resource endpoint. The performance comparison uses it as the separate-request
// alternative to ?expand=visits. It returns the same bounded preview, with clubs as ids only.
public static class Endpoint
{
    public static void MapGetMembershipVisits(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/memberships/{id:int}/visits", HandleAsync);

    private static async Task<IResult> HandleAsync(int id, int? limit, MembershipsDbContext db,
        CancellationToken cancellationToken)
    {
        var take = limit ?? ExpandPlan.DefaultRelatedLimit;
        if (take is < 1 or > ExpandPlan.MaxRelatedLimit)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["limit"] = [$"limit must be between 1 and {ExpandPlan.MaxRelatedLimit}."]
            });

        // Same ordering as the visits expansion, and one extra row to report hasMore.
        var visits = await db.Set<Visit>()
            .AsNoTracking()
            .Where(visit => visit.MembershipId == id)
            .OrderByDescending(visit => visit.CheckedInAt).ThenByDescending(visit => visit.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken);

        return Results.Ok(Related.From(visits, take,
            visit => new VisitResponse(visit.Id, visit.CheckedInAt, visit.ClubId, Club: null)));
    }
}
