using Microsoft.EntityFrameworkCore;
using ResourceExpansion.Api.Domain;

namespace ResourceExpansion.Api.Infrastructure;

// EF Core conventions discover the relationships from the navigation and foreign key properties.
public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
}
