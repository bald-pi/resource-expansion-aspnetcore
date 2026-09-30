using Microsoft.EntityFrameworkCore;
using ResourceExpansion.Api.Domain;
using ResourceExpansion.Api.Features.Memberships.GetMembership;
using ResourceExpansion.Api.Infrastructure;

namespace ResourceExpansion.Api.Features.Clubs.GetClub;

// A standalone resource endpoint. The performance comparison uses it as the separate-request
// alternative to ?expand=visits.club.
public static class Endpoint
{
    public static void MapGetClub(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/clubs/{id:int}", HandleAsync);

    private static async Task<IResult> HandleAsync(int id, MembershipsDbContext db, CancellationToken cancellationToken)
    {
        var club = await db.Set<Club>()
            .AsNoTracking()
            .SingleOrDefaultAsync(club => club.Id == id, cancellationToken);

        return club is null
            ? Results.Problem(statusCode: 404, title: "Club not found")
            : Results.Ok(new ClubResponse(club.Id, club.Name, club.City));
    }
}
