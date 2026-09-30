using ResourceExpansion.Api.Domain;
using ResourceExpansion.Api.Expansion;

namespace ResourceExpansion.Api.Features.Memberships.GetMembership;

// Unrequested relationships are null. Requested but empty visits are { "data": [] }.
public sealed record MembershipResponse(
    int Id, string Number, string Plan, string Status, DateOnly StartsOn, DateOnly EndsOn,
    long MonthlyFeeCents, int MemberId,
    MemberResponse? Member,
    Related<VisitResponse>? Visits);

public sealed record MemberResponse(int Id, string Name, string Email);

public sealed record VisitResponse(int Id, DateTimeOffset CheckedInAt, int ClubId, ClubResponse? Club);

public sealed record ClubResponse(int Id, string Name, string City);

// Mapping reads the plan, not whether a navigation happens to be loaded.
public static class MembershipMapping
{
    public static MembershipResponse ToResponse(this Membership membership, ExpandPlan plan) => new(
        membership.Id, membership.Number, membership.Plan, membership.Status,
        membership.StartsOn, membership.EndsOn, membership.MonthlyFeeCents, membership.MemberId,
        Member: plan.Has("member")
            ? new MemberResponse(membership.Member.Id, membership.Member.Name, membership.Member.Email)
            : null,
        Visits: plan.Has("visits")
            ? Related.From(membership.Visits, plan.RelatedLimit, visit => new VisitResponse(
                visit.Id, visit.CheckedInAt, visit.ClubId,
                Club: plan.Has("visits.club") ? new ClubResponse(visit.Club.Id, visit.Club.Name, visit.Club.City) : null))
            : null);
}
