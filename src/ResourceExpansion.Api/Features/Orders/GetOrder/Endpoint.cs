using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ResourceExpansion.Api.Infrastructure;

namespace ResourceExpansion.Api.Features.Orders.GetOrder;

public static class Endpoint
{
    public static void MapGetOrder(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/orders/{id:int}", HandleAsync);

    private static async Task<IResult> HandleAsync(int id, [AsParameters] GetOrderQuery query,
        IValidator<GetOrderQuery> validator, OrdersDbContext db, CancellationToken cancellationToken)
    {
        // 1. Validate the requested expansions against the allowlist.
        var validation = validator.Validate(query);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var plan = OrderExpand.From(query);

        // 2. Load only the requested relationships.
        var order = await db.Orders
            .AsNoTracking()
            .Where(order => order.Id == id)
            .WithExpansions(plan)
            .SingleOrDefaultAsync(cancellationToken);

        // 3. Map to the response.
        return order is null
            ? Results.Problem(statusCode: 404, title: "Order not found")
            : Results.Ok(OrderResponse.From(order, plan));
    }
}
