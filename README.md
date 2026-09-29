# Resource expansion with ASP.NET Core

A runnable blog companion using **.NET 10 Minimal APIs, FluentValidation, EF Core, and PostgreSQL**.

One endpoint, `GET /api/orders/{id}`, supports a single expansion (`customer`) and a nested expansion (`items.product`).

## Run

With the .NET 10 SDK and Docker running:

```sh
docker compose up -d --wait
dotnet run --project src/ResourceExpansion.Api
```

The API listens at `http://localhost:5080`. Startup creates the demo schema and seeds order `1001` (three items) and order `1002` (no items). PostgreSQL listens on localhost port `54329`.

```sh
curl "http://localhost:5080/api/orders/1001"
curl "http://localhost:5080/api/orders/1001?expand=customer"
curl "http://localhost:5080/api/orders/1001?expand=customer,items.product&relatedLimit=3"
```

Use `curl.exe` in Windows PowerShell. More examples are in [requests.http](examples/requests.http).

## Blog material

- [Walkthrough and complete implementation](docs/blog-walkthrough.md)
- [Use cases](docs/use-cases.md)
- [Captured request/response examples](examples/responses)

## Structure

```text
src/ResourceExpansion.Api/
  Features/Orders/GetOrder/
    OrderExpand.cs              Query record, allowlist, typed expansion plan
    GetOrderQueryValidator.cs   FluentValidation rules for expand and relatedLimit
    OrderQuery.cs               EF Core Include calls for requested relationships
    Response.cs                 DTOs and conditional mapping
    Endpoint.cs                 validate -> load -> map
  Domain/Order.cs
  Infrastructure/
    OrdersDbContext.cs
    DemoData.cs
  Program.cs
tests/ResourceExpansion.Tests/
```

## Expansion contract

| Setting | Behavior |
|---|---|
| Allowed paths | `customer`, `items`, `items.product` |
| Nested selection | `items.product` also selects `items` |
| Normalization | Case-insensitive, trimmed, deduplicated; empty entries ignored |
| Missing/blank expand | Baseline response |
| Invalid input | 400 validation Problem Details |
| Missing order | 404 |
| Unrequested relationship | JSON `null` |
| Expanded items | `{ "data": [...], "limit": 2, "hasMore": true }` |
| Item bounds | Default 2, maximum 50; database fetches one extra row |
| Depth | 2 segments |

Expanded items are a bounded preview. The sample does not implement pagination. Prices and totals use integer cents.

## Verify

```sh
dotnet test
```

Integration tests use their own disposable PostgreSQL container. For parser tests without Docker:

```sh
dotnet test --filter FullyQualifiedName~OrderExpandTests
```

To refresh captured responses and the blog's source appendix while the API runs:

```sh
python scripts/refresh-blog-examples.py
```

Python is optional; it is only used for refreshing documentation.

Stop PostgreSQL with `docker compose stop`. The sample uses `EnsureCreated` for disposable databases; use migrations when evolving a deployed schema.
