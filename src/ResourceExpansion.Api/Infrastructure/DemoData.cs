using Microsoft.EntityFrameworkCore;
using ResourceExpansion.Api.Domain;

namespace ResourceExpansion.Api.Infrastructure;

public static class DemoData
{
    public static async Task SeedAsync(OrdersDbContext db)
    {
        if (await db.Orders.AnyAsync()) return;

        var alice = new Customer { Id = 1, Name = "Alice Morgan", Email = "alice@example.com" };
        db.Orders.AddRange(
            new Order
            {
                Id = 1001, Number = "ORD-1001", Status = "paid", TotalCents = 16000, Customer = alice,
                Items =
                [
                    new() { Id = 1, Quantity = 1, UnitPriceCents = 10000, Product = new() { Id = 1, Name = "Mechanical keyboard" } },
                    new() { Id = 2, Quantity = 1, UnitPriceCents = 4000, Product = new() { Id = 2, Name = "Wireless mouse" } },
                    new() { Id = 3, Quantity = 1, UnitPriceCents = 2000, Product = new() { Id = 3, Name = "Desk mat" } }
                ]
            },
            new Order { Id = 1002, Number = "ORD-1002", Status = "draft", TotalCents = 0, Customer = alice });

        await db.SaveChangesAsync();
    }
}
