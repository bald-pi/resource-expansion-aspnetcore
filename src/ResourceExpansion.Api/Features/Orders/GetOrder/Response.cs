using ResourceExpansion.Api.Domain;

namespace ResourceExpansion.Api.Features.Orders.GetOrder;

// Unrequested relationships are null. Requested but empty items are { "data": [] }.
public sealed record OrderResponse(
    int Id, string Number, string Status, long TotalCents, int CustomerId,
    CustomerResponse? Customer,
    ItemsResponse? Items)
{
    public static OrderResponse From(Order order, OrderExpand expand) => new(
        order.Id, order.Number, order.Status, order.TotalCents, order.CustomerId,
        Customer: expand.Customer ? CustomerResponse.From(order.Customer) : null,
        Items: expand.Items ? ItemsResponse.From(order.Items, expand) : null);
}

public sealed record CustomerResponse(int Id, string Name, string Email)
{
    public static CustomerResponse From(Customer customer) => new(customer.Id, customer.Name, customer.Email);
}

// A bounded preview. The query loaded up to limit + 1 items; the extra one only signals HasMore.
public sealed record ItemsResponse(IReadOnlyList<ItemResponse> Data, int Limit, bool HasMore)
{
    public static ItemsResponse From(List<OrderItem> items, OrderExpand expand) => new(
        Data: items.Take(expand.RelatedLimit).Select(item => ItemResponse.From(item, expand)).ToList(),
        Limit: expand.RelatedLimit,
        HasMore: items.Count > expand.RelatedLimit);
}

public sealed record ItemResponse(int Id, int ProductId, int Quantity, long UnitPriceCents, ProductResponse? Product)
{
    public static ItemResponse From(OrderItem item, OrderExpand expand) => new(
        item.Id, item.ProductId, item.Quantity, item.UnitPriceCents,
        expand.ItemProducts ? ProductResponse.From(item.Product) : null);
}

public sealed record ProductResponse(int Id, string Name)
{
    public static ProductResponse From(Product product) => new(product.Id, product.Name);
}
