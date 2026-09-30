using Microsoft.EntityFrameworkCore;
using ResourceExpansion.Api.Domain;

namespace ResourceExpansion.Api.Infrastructure;

public static class DemoData
{
    public static async Task SeedAsync(MembershipsDbContext db)
    {
        if (await db.Memberships.AnyAsync()) return;

        var alice = new Member { Id = 1, Name = "Alice Morgan", Email = "alice@example.com" };
        var downtown = new Club { Id = 1, Name = "Downtown", City = "Seattle" };
        var riverside = new Club { Id = 2, Name = "Riverside", City = "Bellevue" };
        db.Memberships.AddRange(
            new Membership
            {
                Id = 1001, Number = "MEM-1001", Plan = "Premium", Status = "active",
                StartsOn = new(2026, 1, 1), EndsOn = new(2026, 12, 31), MonthlyFeeCents = 4900, Member = alice,
                Visits =
                [
                    new() { Id = 1, CheckedInAt = new(2026, 9, 20, 7, 30, 0, TimeSpan.Zero), Club = downtown },
                    new() { Id = 2, CheckedInAt = new(2026, 9, 24, 18, 15, 0, TimeSpan.Zero), Club = riverside },
                    new() { Id = 3, CheckedInAt = new(2026, 9, 27, 6, 45, 0, TimeSpan.Zero), Club = downtown }
                ]
            },
            // A renewal that has not started yet, so it has no visits.
            new Membership
            {
                Id = 1002, Number = "MEM-1002", Plan = "Premium", Status = "pending",
                StartsOn = new(2027, 1, 1), EndsOn = new(2027, 12, 31), MonthlyFeeCents = 4900, Member = alice
            });

        await db.SaveChangesAsync();
    }
}
