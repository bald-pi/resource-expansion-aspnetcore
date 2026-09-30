using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ResourceExpansion.Api.Expansion;
using ResourceExpansion.Api.Infrastructure;

namespace ResourceExpansion.Api.Features.Memberships.GetMembership;

public static class Endpoint
{
    public static void MapGetMembership(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/memberships/{id:int}", HandleAsync);

    private static async Task<IResult> HandleAsync([AsParameters] GetMembershipRequest request,
        IValidator<GetMembershipRequest> validator, MembershipsDbContext db, CancellationToken cancellationToken)
    {
        // 1. Validate the requested expansions against the allowlist.
        var validation = validator.Validate(request);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var plan = ExpandPlan.From(request);

        // 2. Load only the requested relationships.
        var membership = await db.Memberships
            .AsNoTracking()
            .Where(membership => membership.Id == request.Id)
            .WithExpansions(MembershipExpansions.Rules, plan)
            .SingleOrDefaultAsync(cancellationToken);

        // 3. Map to the response.
        return membership is null
            ? Results.Problem(statusCode: 404, title: "Membership not found")
            : Results.Ok(membership.ToResponse(plan));
    }
}
