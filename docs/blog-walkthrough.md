# Implementing Resource Expansion in ASP.NET Core

An order-status card needs only the order number and status. An order-detail screen also needs the customer and the product names. Resource expansion lets both clients use the same endpoint and ask only for what they need:

```http
GET /api/orders/1001
GET /api/orders/1001?expand=customer
GET /api/orders/1001?expand=customer,items.product
```

The sample uses ASP.NET Core 10 Minimal APIs, FluentValidation, EF Core, and PostgreSQL. It supports two kinds of expansion:

- **Single:** `customer`, a reference loaded with one join.
- **Nested:** `items.product`, a collection whose elements carry their own reference.

The feature is five small files in `Features/Orders/GetOrder`:

| File | Job |
|---|---|
| `OrderExpand.cs` | Query record, allowlist, and the typed expansion plan |
| `GetOrderQueryValidator.cs` | FluentValidation rules for `expand` and `relatedLimit` |
| `OrderQuery.cs` | Add EF Core `Include` calls for the requested relationships |
| `Response.cs` | DTOs and mapping |
| `Endpoint.cs` | Validate, load, map |

## 1. Define the entities, response DTOs, and baseline endpoint

An `Order` has one `Customer` and a list of `OrderItem`s. Each item points to a `Product`. Prices are stored in integer cents.

```csharp
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

public sealed class OrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public int Quantity { get; set; }
    public long UnitPriceCents { get; set; }
}
```

The response always contains the order's own fields plus `customerId`. Each relationship is nullable and filled only when the client requests it:

```csharp
public sealed record OrderResponse(
    int Id, string Number, string Status, long TotalCents, int CustomerId,
    CustomerResponse? Customer,
    ItemsResponse? Items);

public sealed record ItemResponse(int Id, int ProductId, int Quantity, long UnitPriceCents, ProductResponse? Product);
```

The baseline request `GET /api/orders/1001` runs one `SELECT` with no joins and returns `customer` and `items` as `null`.

## 2. Parse and validate the expand parameter using an allowlist

The query string binds to a record through `[AsParameters]`:

```csharp
public sealed record GetOrderQuery(string? Expand, int? RelatedLimit);
```

The API accepts three paths: `customer`, `items`, and `items.product`. One helper splits the value on commas, trims each path, and removes duplicates. The case-insensitive `HashSet` treats `Customer` and `customer` as the same path:

```csharp
public static readonly string[] Allowed = ["customer", "items", "items.product"];

public static HashSet<string> SplitPaths(string? expand) => (expand ?? "")
    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
```

A FluentValidation validator owns every rule and error message:

```csharp
public sealed class GetOrderQueryValidator : AbstractValidator<GetOrderQuery>
{
    public GetOrderQueryValidator()
    {
        // Report only the first failing rule for each parameter.
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(query => query.Expand)
            .Must(expand => TooDeep(expand).Length == 0)
            .WithMessage(query => $"Expansion depth cannot exceed {OrderExpand.MaxDepth}: " +
                                  $"{string.Join(", ", TooDeep(query.Expand))}.")
            .Must(expand => Unsupported(expand).Length == 0)
            .WithMessage(query => $"Unsupported expansion: {string.Join(", ", Unsupported(query.Expand))}. " +
                                  $"Supported: {string.Join(", ", OrderExpand.Allowed)}.")
            .OverridePropertyName("expand");

        RuleFor(query => query.RelatedLimit)
            .InclusiveBetween(1, OrderExpand.MaxRelatedLimit)
            .WithMessage($"relatedLimit must be between 1 and {OrderExpand.MaxRelatedLimit}.")
            .OverridePropertyName("relatedLimit");
    }

    private static string[] TooDeep(string? expand) => OrderExpand.SplitPaths(expand)
        .Where(path => path.Split('.').Length > OrderExpand.MaxDepth)
        .ToArray();

    private static string[] Unsupported(string? expand) => OrderExpand.SplitPaths(expand)
        .Except(OrderExpand.Allowed, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
```

- `RuleLevelCascadeMode = CascadeMode.Stop` stops at the first failing rule for each parameter. A path that is too deep gets only the depth error, not a second "unsupported" error.
- Both `expand` and `relatedLimit` are still checked, so the client sees every problem in one response.
- `OverridePropertyName` keeps the error keys identical to the query parameter names (`expand`, not `Expand`).
- `InclusiveBetween` skips `null`, so an omitted `relatedLimit` falls back to the default.

Register the validator once:

```csharp
builder.Services.AddSingleton<IValidator<GetOrderQuery>, GetOrderQueryValidator>();
```

Once the query is valid, it becomes a typed record of booleans. This step has no error handling, because the validator has already accepted the input:

```csharp
public sealed record OrderExpand(bool Customer, bool Items, bool ItemProducts, int RelatedLimit)
{
    public static OrderExpand From(GetOrderQuery query)
    {
        var paths = SplitPaths(query.Expand);

        // Expanding items.product implies items.
        var itemProducts = paths.Contains("items.product");
        return new OrderExpand(
            Customer: paths.Contains("customer"),
            Items: itemProducts || paths.Contains("items"),
            ItemProducts: itemProducts,
            RelatedLimit: query.RelatedLimit ?? DefaultRelatedLimit);
    }
}
```

The client's string is only compared with the allowlist and never reaches EF Core's string-based `Include("...")`, so a client cannot load a navigation you did not choose to expose. After this point, the code works with `plan.Customer` and `plan.ItemProducts` instead of strings.

A missing or blank `expand` returns the baseline response.

## 3. Load the requested relationships with EF Core

Each flag adds one `Include`:

```csharp
public static IQueryable<Order> WithExpansions(this IQueryable<Order> query, OrderExpand expand)
{
    // Fetch one extra item so the response can report hasMore without a COUNT query.
    var take = expand.RelatedLimit + 1;

    if (expand.Customer)
        query = query.Include(order => order.Customer);

    if (expand.Items)
    {
        var withItems = query.Include(order => order.Items.OrderBy(item => item.Id).Take(take));
        query = expand.ItemProducts ? withItems.ThenInclude(item => item.Product) : withItems;
    }

    return query;
}
```

The endpoint uses it like any other LINQ operator:

```csharp
var order = await db.Orders
    .AsNoTracking()
    .Where(order => order.Id == id)
    .WithExpansions(plan)
    .SingleOrDefaultAsync(cancellationToken);
```

- **Single expansion:** `Include(order => order.Customer)` adds a join.
- **Nested expansion:** `Include(...).ThenInclude(item => item.Product)` loads the items and each item's product in the same query. There is no extra query per product.
- **Filtered include:** `OrderBy(...).Take(take)` makes PostgreSQL limit the items. Stable ordering means the preview always contains the same rows. See [EF Core eager loading](https://learn.microsoft.com/en-us/ef/core/querying/related-data/eager).
- **One query:** every combination runs as a single `SELECT`. If you later add a second collection (for example payments), add `AsSplitQuery()` so the two collections do not multiply each other's rows. See [single vs. split queries](https://learn.microsoft.com/en-us/ef/core/querying/single-split-queries).

## 4. Build the response and define how unrequested relationships are represented

Mapping reads the plan, not whether a navigation happens to be loaded:

```csharp
public static OrderResponse From(Order order, OrderExpand expand) => new(
    order.Id, order.Number, order.Status, order.TotalCents, order.CustomerId,
    Customer: expand.Customer ? CustomerResponse.From(order.Customer) : null,
    Items: expand.Items ? ItemsResponse.From(order.Items, expand) : null);
```

```csharp
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
```

The contract has three states:

| State | JSON |
|---|---|
| Not requested | `"items": null` |
| Requested, empty | `"items": { "data": [], "limit": 2, "hasMore": false }` |
| Requested, more rows exist | `"items": { "data": [ ... ], "limit": 2, "hasMore": true }` |

The same rule applies one level down. With `?expand=items`, each item's `product` is `null`. With `?expand=items.product`, it is an object.

System.Text.Json writes `null` properties by default. Keep that default, or clients cannot tell "not requested" apart from "missing". Because the item list can be cut short, clients should use the order's `totalCents` rather than summing the items.

## 5. Handle unsupported expansions and enforce authorization

An unknown path fails the `Unsupported` rule from section 2. `validation.ToDictionary()` groups the failures by parameter name, and `Results.ValidationProblem` turns them into standard Problem Details:

```json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "expand": ["Unsupported expansion: payments. Supported: customer, items, items.product."]
  }
}
```

The endpoint validates before it touches the database, so an invalid request never runs a query:

```csharp
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
```

**Authorization.** Expansion is available to everyone, but each relationship still has to follow the rules that would apply if the client fetched it directly. If `GET /api/payments` needs an admin, then `?expand=payments` needs one too; otherwise `expand` becomes a way around your authorization. In this sample, the customer, items, and products are visible to anyone who can read the order, so no extra check is needed. When you add a protected relationship, check its policy right after validation and before the query. If the check fails, return 403 for the whole request. Do not silently drop the field, because the client would read the missing data as "none".

## 6. Limit expansion depth and related collection sizes

The limits are constants on `OrderExpand`:

```csharp
public const int DefaultRelatedLimit = 2;
public const int MaxRelatedLimit = 50;

// There is no universal depth limit. OData's ASP.NET Core default is 2 and Stripe allows 4.
// Choose the smallest depth your clients actually need.
public const int MaxDepth = 2;
```

The validator enforces them with the depth rule on `expand` (section 2) and this rule on `relatedLimit`:

```csharp
RuleFor(query => query.RelatedLimit)
    .InclusiveBetween(1, OrderExpand.MaxRelatedLimit)
    .WithMessage($"relatedLimit must be between 1 and {OrderExpand.MaxRelatedLimit}.")
    .OverridePropertyName("relatedLimit");
```

| Control | Value | Why |
|---|---|---|
| Allowlist | 3 paths | Only relationships you chose to expose |
| Max depth | 2 segments | `items.product` is allowed, `items.product.supplier` gets a clear error |
| Default `relatedLimit` | 2 | Small by default, and it makes `hasMore` easy to see in the demo |
| Max `relatedLimit` | 50 | Caps the rows loaded and serialized |
| Fetch size | limit + 1 | Tells the client whether more rows exist, without a `COUNT` |

The allowlist already stops paths deeper than `items.product`. The explicit depth check gives a clearer error, and it still protects the API if someone adds a deeper path later.

`RelatedLimit` is an `int?`, so ASP.NET Core rejects a non-integer such as `abc` with 400 while binding, before the validator runs.

These are bounded previews, not pagination. When a client needs every item, add a paginated endpoint such as `GET /api/orders/{id}/items?cursor=...`.

## 7. Complete implementation with example requests and responses

Run it:

```sh
docker compose up -d --wait
dotnet run --project src/ResourceExpansion.Api
```

```sh
curl "http://localhost:5080/api/orders/1001?expand=customer"
curl "http://localhost:5080/api/orders/1001?expand=customer,items.product&relatedLimit=3"
```

[requests.http](../examples/requests.http) contains every scenario. `dotnet test` starts an isolated PostgreSQL container and checks the response shapes and the SQL each request runs.

<!-- generated examples and source below -->

## Example requests and responses

Captured from the running PostgreSQL-backed API. Dynamic trace IDs, if present, are omitted. All 11 captured cases are available in [examples/responses](../examples/responses); the manifest records request paths, tokens, and statuses.

### Baseline

```http
GET /api/orders/1001
```

HTTP 200

```json
{
  "id": 1001,
  "number": "ORD-1001",
  "status": "paid",
  "totalCents": 16000,
  "customerId": 1,
  "customer": null,
  "items": null
}
```

### Customer

```http
GET /api/orders/1001?expand=customer
```

HTTP 200

```json
{
  "id": 1001,
  "number": "ORD-1001",
  "status": "paid",
  "totalCents": 16000,
  "customerId": 1,
  "customer": {
    "id": 1,
    "name": "Alice Morgan",
    "email": "alice@example.com"
  },
  "items": null
}
```

### Nested

```http
GET /api/orders/1001?expand=customer,items.product&relatedLimit=3
```

HTTP 200

```json
{
  "id": 1001,
  "number": "ORD-1001",
  "status": "paid",
  "totalCents": 16000,
  "customerId": 1,
  "customer": {
    "id": 1,
    "name": "Alice Morgan",
    "email": "alice@example.com"
  },
  "items": {
    "data": [
      {
        "id": 1,
        "productId": 1,
        "quantity": 1,
        "unitPriceCents": 10000,
        "product": {
          "id": 1,
          "name": "Mechanical keyboard"
        }
      },
      {
        "id": 2,
        "productId": 2,
        "quantity": 1,
        "unitPriceCents": 4000,
        "product": {
          "id": 2,
          "name": "Wireless mouse"
        }
      },
      {
        "id": 3,
        "productId": 3,
        "quantity": 1,
        "unitPriceCents": 2000,
        "product": {
          "id": 3,
          "name": "Desk mat"
        }
      }
    ],
    "limit": 3,
    "hasMore": false
  }
}
```

### Truncated

```http
GET /api/orders/1001?expand=items.product&relatedLimit=1
```

HTTP 200

```json
{
  "id": 1001,
  "number": "ORD-1001",
  "status": "paid",
  "totalCents": 16000,
  "customerId": 1,
  "customer": null,
  "items": {
    "data": [
      {
        "id": 1,
        "productId": 1,
        "quantity": 1,
        "unitPriceCents": 10000,
        "product": {
          "id": 1,
          "name": "Mechanical keyboard"
        }
      }
    ],
    "limit": 1,
    "hasMore": true
  }
}
```

### Empty

```http
GET /api/orders/1002?expand=items
```

HTTP 200

```json
{
  "id": 1002,
  "number": "ORD-1002",
  "status": "draft",
  "totalCents": 0,
  "customerId": 1,
  "customer": null,
  "items": {
    "data": [],
    "limit": 2,
    "hasMore": false
  }
}
```

### Unsupported

```http
GET /api/orders/1001?expand=payments
```

HTTP 400

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "expand": [
      "Unsupported expansion: payments. Supported: customer, items, items.product."
    ]
  }
}
```

### Depth

```http
GET /api/orders/1001?expand=items.product.supplier
```

HTTP 400

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "expand": [
      "Expansion depth cannot exceed 2: items.product.supplier."
    ]
  }
}
```

### Missing Order

```http
GET /api/orders/9999
```

HTTP 404

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.5",
  "title": "Order not found",
  "status": 404
}
```

## Complete API implementation

The source below matches the runnable files. Tests live in `tests/ResourceExpansion.Tests`.

### Directory.Build.props

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

### compose.yaml

```yaml
services:
  postgres:
    image: postgres:17-alpine
    environment:
      POSTGRES_DB: expansion_demo
      POSTGRES_USER: expansion
      POSTGRES_PASSWORD: local-demo-password
    ports:
      - "127.0.0.1:54329:5432"
    volumes:
      - orders-data:/var/lib/postgresql/data
    # Lets `docker compose up --wait` return only when the database accepts connections.
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U expansion -d expansion_demo"]
      interval: 2s
      timeout: 5s
      retries: 20

volumes:
  orders-data:
```

### src/ResourceExpansion.Api/ResourceExpansion.Api.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="FluentValidation" Version="12.1.1" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.0" />
  </ItemGroup>
</Project>
```

### src/ResourceExpansion.Api/Program.cs

```csharp
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ResourceExpansion.Api.Features.Orders.GetOrder;
using ResourceExpansion.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddSingleton<IValidator<GetOrderQuery>, GetOrderQueryValidator>();
builder.Services.AddDbContext<OrdersDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Orders")));

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapGetOrder();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
    // A disposable demo schema. Use migrations for a real application.
    await db.Database.EnsureCreatedAsync();
    await DemoData.SeedAsync(db);
}

app.Run();

public partial class Program;
```

### src/ResourceExpansion.Api/Domain/Order.cs

```csharp
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
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public int Quantity { get; set; }
    public long UnitPriceCents { get; set; }
}

public sealed class Product
{
    public int Id { get; set; }
    public required string Name { get; set; }
}
```

### src/ResourceExpansion.Api/Infrastructure/OrdersDbContext.cs

```csharp
using Microsoft.EntityFrameworkCore;
using ResourceExpansion.Api.Domain;

namespace ResourceExpansion.Api.Infrastructure;

// EF Core conventions discover the relationships from the navigation and foreign key properties.
public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
}
```

### src/ResourceExpansion.Api/Infrastructure/DemoData.cs

```csharp
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
```

### src/ResourceExpansion.Api/Features/Orders/GetOrder/OrderExpand.cs

```csharp
namespace ResourceExpansion.Api.Features.Orders.GetOrder;

// Query string for GET /api/orders/{id}?expand=...&relatedLimit=...
public sealed record GetOrderQuery(string? Expand, int? RelatedLimit);

// The validated expansion plan. Request strings are only compared against the allowlist;
// they never reach EF Core.
public sealed record OrderExpand(bool Customer, bool Items, bool ItemProducts, int RelatedLimit)
{
    public const int DefaultRelatedLimit = 2;
    public const int MaxRelatedLimit = 50;
    // There is no universal depth limit. OData's ASP.NET Core default is 2 and Stripe allows 4.
    // Choose the smallest depth your clients actually need.
    public const int MaxDepth = 2;

    public static readonly string[] Allowed = ["customer", "items", "items.product"];

    public static HashSet<string> SplitPaths(string? expand) => (expand ?? "")
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Call only after GetOrderQueryValidator has accepted the query.
    public static OrderExpand From(GetOrderQuery query)
    {
        var paths = SplitPaths(query.Expand);

        // Expanding items.product implies items.
        var itemProducts = paths.Contains("items.product");
        return new OrderExpand(
            Customer: paths.Contains("customer"),
            Items: itemProducts || paths.Contains("items"),
            ItemProducts: itemProducts,
            RelatedLimit: query.RelatedLimit ?? DefaultRelatedLimit);
    }
}
```

### src/ResourceExpansion.Api/Features/Orders/GetOrder/GetOrderQueryValidator.cs

```csharp
using FluentValidation;

namespace ResourceExpansion.Api.Features.Orders.GetOrder;

public sealed class GetOrderQueryValidator : AbstractValidator<GetOrderQuery>
{
    public GetOrderQueryValidator()
    {
        // Report only the first failing rule for each parameter.
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(query => query.Expand)
            .Must(expand => TooDeep(expand).Length == 0)
            .WithMessage(query => $"Expansion depth cannot exceed {OrderExpand.MaxDepth}: " +
                                  $"{string.Join(", ", TooDeep(query.Expand))}.")
            .Must(expand => Unsupported(expand).Length == 0)
            .WithMessage(query => $"Unsupported expansion: {string.Join(", ", Unsupported(query.Expand))}. " +
                                  $"Supported: {string.Join(", ", OrderExpand.Allowed)}.")
            .OverridePropertyName("expand");

        RuleFor(query => query.RelatedLimit)
            .InclusiveBetween(1, OrderExpand.MaxRelatedLimit)
            .WithMessage($"relatedLimit must be between 1 and {OrderExpand.MaxRelatedLimit}.")
            .OverridePropertyName("relatedLimit");
    }

    private static string[] TooDeep(string? expand) => OrderExpand.SplitPaths(expand)
        .Where(path => path.Split('.').Length > OrderExpand.MaxDepth)
        .ToArray();

    private static string[] Unsupported(string? expand) => OrderExpand.SplitPaths(expand)
        .Except(OrderExpand.Allowed, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
```

### src/ResourceExpansion.Api/Features/Orders/GetOrder/OrderQuery.cs

```csharp
using Microsoft.EntityFrameworkCore;
using ResourceExpansion.Api.Domain;

namespace ResourceExpansion.Api.Features.Orders.GetOrder;

public static class OrderQuery
{
    public static IQueryable<Order> WithExpansions(this IQueryable<Order> query, OrderExpand expand)
    {
        // Fetch one extra item so the response can report hasMore without a COUNT query.
        var take = expand.RelatedLimit + 1;

        if (expand.Customer)
            query = query.Include(order => order.Customer);

        if (expand.Items)
        {
            var withItems = query.Include(order => order.Items.OrderBy(item => item.Id).Take(take));
            query = expand.ItemProducts ? withItems.ThenInclude(item => item.Product) : withItems;
        }

        return query;
    }
}
```

### src/ResourceExpansion.Api/Features/Orders/GetOrder/Response.cs

```csharp
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
```

### src/ResourceExpansion.Api/Features/Orders/GetOrder/Endpoint.cs

```csharp
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
```

### src/ResourceExpansion.Api/appsettings.json

```json
{
  "ConnectionStrings": {
    "Orders": "Host=localhost;Port=54329;Database=expansion_demo;Username=expansion;Password=local-demo-password"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Command": "Information"
    }
  },
  "AllowedHosts": "localhost;127.0.0.1"
}
```

### src/ResourceExpansion.Api/Properties/launchSettings.json

```json
{
  "$schema": "https://json.schemastore.org/launchsettings.json",
  "profiles": {
    "http": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "launchBrowser": false,
      "applicationUrl": "http://localhost:5080",
      "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development" }
    }
  }
}
```
