using Microsoft.EntityFrameworkCore;
using ResourceExpansion.Api.Domain;

namespace ResourceExpansion.Api.Infrastructure;

// EF Core conventions discover the relationships from the navigation and foreign key properties.
public sealed class MembershipsDbContext(DbContextOptions<MembershipsDbContext> options) : DbContext(options)
{
    public DbSet<Membership> Memberships => Set<Membership>();
}
