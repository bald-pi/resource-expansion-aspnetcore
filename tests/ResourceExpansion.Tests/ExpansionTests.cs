using FluentValidation.TestHelper;
using ResourceExpansion.Api.Expansion;
using ResourceExpansion.Api.Features.Memberships.GetMembership;

namespace ResourceExpansion.Tests;

public sealed class ExpansionTests
{
    private readonly ExpandRequestValidator<GetMembershipRequest> validator = new(MembershipExpansions.Rules.Allowed);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Missing_or_blank_expand_uses_baseline(string? expand)
    {
        var request = new GetMembershipRequest(1001, expand, null);
        var plan = ExpandPlan.From(request);

        validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
        Assert.Empty(plan.Paths);
        Assert.Equal(ExpandPlan.DefaultRelatedLimit, plan.RelatedLimit);
    }

    [Fact]
    public void Normalizes_case_whitespace_and_duplicates()
    {
        var request = new GetMembershipRequest(1001, " Member ,VISITS,member", 50);
        var plan = ExpandPlan.From(request);

        validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
        Assert.Equal(["member", "visits"], plan.Paths.Order());
        Assert.Equal(50, plan.RelatedLimit);
    }

    [Fact]
    public void Nested_path_implies_parent()
    {
        var plan = ExpandPlan.From(new GetMembershipRequest(1001, "Visits.Club", null));
        Assert.Equal(["visits", "visits.club"], plan.Paths.Order());
    }

    [Theory]
    [InlineData("unknown", "Unsupported expansion: unknown. Supported: member, visits, visits.club.")]
    [InlineData("member.memberships", "Unsupported expansion: member.memberships. Supported: member, visits, visits.club.")]
    [InlineData("visits.club.address", "Expansion depth cannot exceed 2: visits.club.address.")]
    public void Rejects_paths_outside_the_allowlist(string expand, string message)
    {
        validator.TestValidate(new GetMembershipRequest(1001, expand, null))
            .ShouldHaveValidationErrorFor("expand")
            .WithErrorMessage(message)
            .Only();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Rejects_out_of_range_related_limit(int limit)
    {
        validator.TestValidate(new GetMembershipRequest(1001, null, limit))
            .ShouldHaveValidationErrorFor("relatedLimit")
            .WithErrorMessage("relatedLimit must be between 1 and 50.")
            .Only();
    }

    [Fact]
    public void Reports_errors_for_both_parameters()
    {
        var result = validator.TestValidate(new GetMembershipRequest(1001, "unknown", 0));

        result.ShouldHaveValidationErrorFor("expand");
        result.ShouldHaveValidationErrorFor("relatedLimit");
    }
}
