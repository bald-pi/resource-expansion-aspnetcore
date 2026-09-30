using Microsoft.EntityFrameworkCore;
using ResourceExpansion.Api.Domain;
using ResourceExpansion.Api.Features.Memberships.GetMembership;
using ResourceExpansion.Api.Infrastructure;

namespace ResourceExpansion.Api.Features.Members.GetMember;

// A standalone resource endpoint. The performance comparison uses it as the separate-request
// alternative to ?expand=member.
public static class Endpoint
{
    public static void MapGetMember(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/members/{id:int}", HandleAsync);

    private static async Task<IResult> HandleAsync(int id, MembershipsDbContext db, CancellationToken cancellationToken)
    {
        var member = await db.Set<Member>()
            .AsNoTracking()
            .SingleOrDefaultAsync(member => member.Id == id, cancellationToken);

        return member is null
            ? Results.Problem(statusCode: 404, title: "Member not found")
            : Results.Ok(new MemberResponse(member.Id, member.Name, member.Email));
    }
}
