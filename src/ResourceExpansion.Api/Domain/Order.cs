namespace ResourceExpansion.Api.Domain;

public sealed class Order
{
    public int Id { get; set; }
    public required string Number { get; set; }
    public required string Status { get; set; }
    public long TotalCents { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public List<OrderItem> Items { get; set; } = [];
}

public sealed class Customer
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Email { get; set; }
}

public sealed class OrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int Quantity { get; set; }
    public long UnitPriceCents { get; set; }
}
