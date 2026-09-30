namespace ResourceExpansion.Api.Domain;

public sealed class Membership
{
    public int Id { get; set; }
    public required string Number { get; set; }
    public required string Plan { get; set; }
    public required string Status { get; set; }
    public DateOnly StartsOn { get; set; }
    public DateOnly EndsOn { get; set; }
    public long MonthlyFeeCents { get; set; }
    public int MemberId { get; set; }
    public Member Member { get; set; } = null!;
    public List<Visit> Visits { get; set; } = [];
}

public sealed class Member
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Email { get; set; }
}

public sealed class Visit
{
    public int Id { get; set; }
    public int MembershipId { get; set; }
    public DateTimeOffset CheckedInAt { get; set; }
    public int ClubId { get; set; }
    public Club Club { get; set; } = null!;
}

public sealed class Club
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string City { get; set; }
}
