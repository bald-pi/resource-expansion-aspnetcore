# Implementing Resource Expansion in ASP.NET Core

A gym's membership card needs only the plan, status, and end date. The front-desk screen also needs the member's contact details and the clubs of their most recent check-ins. Resource expansion lets both clients use the same endpoint and ask only for what they need:

```http
GET /api/memberships/1001
GET /api/memberships/1001?expand=member
GET /api/memberships/1001?expand=member,visits.club
```

The sample uses ASP.NET Core 10 Minimal APIs, FluentValidation, EF Core, and PostgreSQL. It supports two kinds of expansion:

- **Single:** `member`, a reference loaded with one join.
- **Nested:** `visits.club`, a bounded collection of recent check-ins whose elements carry their own reference.

The expansion mechanism is generic. It lives in a shared `Expansion` folder and knows nothing about memberships:

| File | Job |
|---|---|
| `IExpandableRequest.cs` | Any request with `Expand` and `RelatedLimit` |
| `ExpandPlan.cs` | Parse `expand`, add implied parent paths, hold the limits |
| `ExpandRequestValidator.cs` | FluentValidation rules for `expand` and `relatedLimit` |
| `ExpansionRules.cs` | Allowlist: each path maps to a typed EF Core `Include` |
| `Related.cs` | Bounded collection preview: `{ data, limit, hasMore }` |

The membership feature in `Features/Memberships/GetMembership` only declares what is specific to it:

| File | Job |
|---|---|
| `GetMembershipRequest.cs` | Route and query string record |
| `MembershipExpansions.cs` | The expandable paths and their `Include` calls |
| `Response.cs` | DTOs and mapping |
| `Endpoint.cs` | Validate, load, map |

## 1. Define the entities, response DTOs, and baseline endpoint

A `Membership` belongs to one `Member` and has a list of `Visit`s (check-ins). Each visit points to the `Club` where it happened. Fees are stored in integer cents.

```csharp
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

public sealed class Visit
{
    public int Id { get; set; }
    public int MembershipId { get; set; }
    public DateTimeOffset CheckedInAt { get; set; }
    public int ClubId { get; set; }
    public Club Club { get; set; } = null!;
}
```

The response always contains the membership's own fields plus `memberId`. Each relationship is nullable and filled only when the client requests it:

```csharp
public sealed record MembershipResponse(
    int Id, string Number, string Plan, string Status, DateOnly StartsOn, DateOnly EndsOn,
    long MonthlyFeeCents, int MemberId,
    MemberResponse? Member,
    Related<VisitResponse>? Visits);

public sealed record VisitResponse(int Id, DateTimeOffset CheckedInAt, int ClubId, ClubResponse? Club);
```

The baseline request `GET /api/memberships/1001` runs one `SELECT` with no joins and returns `member` and `visits` as `null`.

## 2. Parse and validate the expand parameter using an allowlist

The route and the query string bind to one record through `[AsParameters]`. `Id` comes from the `{id:int}` route segment; `Expand` and `RelatedLimit` come from the query string. The record implements `IExpandableRequest`, so the shared code can read those two values from any resource's request:

```csharp
public interface IExpandableRequest
{
    string? Expand { get; }
    int? RelatedLimit { get; }
}

public sealed record GetMembershipRequest(int Id, string? Expand, int? RelatedLimit) : IExpandableRequest;
```

One helper splits the value on commas, trims each path, lowercases it, and removes duplicates, so `Member` and `member` are the same path:

```csharp
public static HashSet<string> SplitPaths(string? expand) => (expand ?? "")
    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
    .Select(path => path.ToLowerInvariant())
    .ToHashSet();
```

A generic FluentValidation validator owns every rule and error message. It receives the allowlist, so every resource reuses it:

```csharp
public sealed class ExpandRequestValidator<TRequest> : AbstractValidator<TRequest>
    where TRequest : IExpandableRequest
{
    public ExpandRequestValidator(IReadOnlyList<string> allowed)
    {
        // Report only the first failing rule for each parameter.
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(request => request.Expand)
            .Must(expand => TooDeep(expand).Length == 0)
            .WithMessage(request => $"Expansion depth cannot exceed {ExpandPlan.MaxDepth}: " +
                                    $"{string.Join(", ", TooDeep(request.Expand))}.")
            .Must(expand => Unsupported(expand, allowed).Length == 0)
            .WithMessage(request => $"Unsupported expansion: {string.Join(", ", Unsupported(request.Expand, allowed))}. " +
                                    $"Supported: {string.Join(", ", allowed)}.")
            .OverridePropertyName("expand");

        RuleFor(request => request.RelatedLimit)
            .InclusiveBetween(1, ExpandPlan.MaxRelatedLimit)
            .WithMessage($"relatedLimit must be between 1 and {ExpandPlan.MaxRelatedLimit}.")
            .OverridePropertyName("relatedLimit");
    }

    private static string[] TooDeep(string? expand) => ExpandPlan.SplitPaths(expand)
        .Where(path => path.Split('.').Length > ExpandPlan.MaxDepth)
        .ToArray();

    private static string[] Unsupported(string? expand, IReadOnlyList<string> allowed) => ExpandPlan.SplitPaths(expand)
        .Except(allowed)
        .ToArray();
}
```

- `RuleLevelCascadeMode = CascadeMode.Stop` stops at the first failing rule for each parameter. A path that is too deep gets only the depth error, not a second "unsupported" error.
- Both `expand` and `relatedLimit` are still checked, so the client sees every problem in one response.
- `OverridePropertyName` keeps the error keys identical to the query parameter names (`expand`, not `Expand`).
- `InclusiveBetween` skips `null`, so an omitted `relatedLimit` falls back to the default.

Register it once per resource, with that resource's allowlist:

```csharp
builder.Services.AddSingleton<IValidator<GetMembershipRequest>>(
    new ExpandRequestValidator<GetMembershipRequest>(MembershipExpansions.Rules.Allowed));
```

Once the request is valid, it becomes an `ExpandPlan`: the set of requested paths plus every implied parent, so `visits.club` also selects `visits`. This step has no error handling, because the validator has already accepted the input:

```csharp
public sealed record ExpandPlan(IReadOnlySet<string> Paths, int RelatedLimit)
{
    public bool Has(string path) => Paths.Contains(path);

    public static ExpandPlan From(IExpandableRequest request)
    {
        var paths = new HashSet<string>();
        foreach (var path in SplitPaths(request.Expand))
        {
            var segments = path.Split('.');
            for (var length = 1; length <= segments.Length; length++)
                paths.Add(string.Join('.', segments[..length]));
        }

        return new ExpandPlan(paths, request.RelatedLimit ?? DefaultRelatedLimit);
    }
}
```

The client's string is only compared with the allowlist and never reaches EF Core's string-based `Include("...")`, so a client cannot load a navigation you did not choose to expose.

A missing or blank `expand` returns the baseline response.

## 3. Load the requested relationships with EF Core

Each resource declares its expandable paths once. Every path maps to a typed `Include`; the rules pass in how many related rows to fetch:

```csharp
public static class MembershipExpansions
{
    public static readonly ExpansionRules<Membership> Rules = new ExpansionRules<Membership>()
        .Allow("member", (query, _) => query.Include(membership => membership.Member))
        .Allow("visits", IncludeVisits)
        .Allow("visits.club", (query, take) => IncludeVisits(query, take).ThenInclude(visit => visit.Club));

    // Most recent visits first; Id breaks ties so the preview is stable.
    private static IIncludableQueryable<Membership, IEnumerable<Visit>> IncludeVisits(IQueryable<Membership> query, int take) =>
        query.Include(membership => membership.Visits
            .OrderByDescending(visit => visit.CheckedInAt).ThenByDescending(visit => visit.Id).Take(take));
}
```

The generic `ExpansionRules` applies the includes for the requested paths:

```csharp
public IQueryable<TEntity> Apply(IQueryable<TEntity> query, ExpandPlan plan)
{
    // Fetch one extra related row so the response can report hasMore without a COUNT query.
    var take = plan.RelatedLimit + 1;

    foreach (var (path, include) in rules)
    {
        // Skip a parent when a selected nested path's include already loads it.
        if (plan.Has(path) && !plan.Paths.Any(other => other.StartsWith(path + '.')))
            query = include(query, take);
    }

    return query;
}
```

The endpoint uses it like any other LINQ operator:

```csharp
var membership = await db.Memberships
    .AsNoTracking()
    .Where(membership => membership.Id == request.Id)
    .WithExpansions(MembershipExpansions.Rules, plan)
    .SingleOrDefaultAsync(cancellationToken);
```

- **Single expansion:** `Include(membership => membership.Member)` adds a join.
- **Nested expansion:** `Include(...).ThenInclude(visit => visit.Club)` loads the visits and each visit's club in the same query. There is no extra query per club. A nested path's include loads its parent too, so `Apply` skips the parent's own include. `?expand=visits,visits.club` therefore includes the visits once.
- **Filtered include:** `OrderByDescending(...).Take(take)` makes PostgreSQL return only the most recent visits. The `Id` tie-breaker keeps the preview stable when two check-ins share a timestamp. See [EF Core eager loading](https://learn.microsoft.com/en-us/ef/core/querying/related-data/eager).
- **One query:** every combination runs as a single `SELECT`. If you later add a second collection (for example payments), add `AsSplitQuery()` so the two collections do not multiply each other's rows. See [single vs. split queries](https://learn.microsoft.com/en-us/ef/core/querying/single-split-queries).

Adding an expansion is one `Allow` call plus a field in the response. The parser, validator, and loading code do not change.

## 4. Build the response and define how unrequested relationships are represented

The response records are plain data. A collection uses the generic `Related<T>` wrapper:

```csharp
public sealed record Related<T>(IReadOnlyList<T> Data, int Limit, bool HasMore);

public static class Related
{
    public static Related<TResult> From<TSource, TResult>(IReadOnlyCollection<TSource> rows, int limit,
        Func<TSource, TResult> map) => new(
        Data: rows.Take(limit).Select(map).ToList(),
        Limit: limit,
        HasMore: rows.Count > limit);
}
```

One extension method maps a membership. It reads the plan, not whether a navigation happens to be loaded:

```csharp
public static MembershipResponse ToResponse(this Membership membership, ExpandPlan plan) => new(
    membership.Id, membership.Number, membership.Plan, membership.Status,
    membership.StartsOn, membership.EndsOn, membership.MonthlyFeeCents, membership.MemberId,
    Member: plan.Has("member")
        ? new MemberResponse(membership.Member.Id, membership.Member.Name, membership.Member.Email)
        : null,
    Visits: plan.Has("visits")
        ? Related.From(membership.Visits, plan.RelatedLimit, visit => new VisitResponse(
            visit.Id, visit.CheckedInAt, visit.ClubId,
            Club: plan.Has("visits.club") ? new ClubResponse(visit.Club.Id, visit.Club.Name, visit.Club.City) : null))
        : null);
```

The contract has three states:

| State | JSON |
|---|---|
| Not requested | `"visits": null` |
| Requested, empty | `"visits": { "data": [], "limit": 2, "hasMore": false }` |
| Requested, more rows exist | `"visits": { "data": [ ... ], "limit": 2, "hasMore": true }` |

The same rule applies one level down. With `?expand=visits`, each visit's `club` is `null`. With `?expand=visits.club`, it is an object.

System.Text.Json writes `null` properties by default. Keep that default, or clients cannot tell "not requested" apart from "missing". Because the visit list is cut short, clients must not treat its length as the member's total number of visits.

## 5. Handle unsupported expansions and enforce authorization

An unknown path fails the `Unsupported` rule from section 2. `validation.ToDictionary()` groups the failures by parameter name, and `Results.ValidationProblem` turns them into standard Problem Details:

```json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "expand": ["Unsupported expansion: payments. Supported: member, visits, visits.club."]
  }
}
```

The endpoint validates before it touches the database, so an invalid request never runs a query:

```csharp
private static async Task<IResult> HandleAsync([AsParameters] GetMembershipRequest request,
    IValidator<GetMembershipRequest> validator, MembershipsDbContext db, CancellationToken cancellationToken)
{
    // 1. Validate the requested expansions against the allowlist.
    var validation = validator.Validate(request);
    if (!validation.IsValid)
        return Results.ValidationProblem(validation.ToDictionary());

    var plan = ExpandPlan.From(request);

    // 2. Load only the requested relationships.
    var membership = await db.Memberships
        .AsNoTracking()
        .Where(membership => membership.Id == request.Id)
        .WithExpansions(MembershipExpansions.Rules, plan)
        .SingleOrDefaultAsync(cancellationToken);

    // 3. Map to the response.
    return membership is null
        ? Results.Problem(statusCode: 404, title: "Membership not found")
        : Results.Ok(membership.ToResponse(plan));
}
```

**Authorization.** Expansion is available to everyone, but each relationship still has to follow the rules that would apply if the client fetched it directly. If `GET /api/payments` needs billing staff, then `?expand=payments` needs them too; otherwise `expand` becomes a way around your authorization. In this sample, the member, visits, and clubs are visible to anyone who can read the membership, so no extra check is needed. When you add a protected relationship, check its policy right after validation and before the query. If the check fails, return 403 for the whole request. Do not silently drop the field, because the client would read the missing data as "none".

## 6. Limit expansion depth and related collection sizes

The limits are constants on `ExpandPlan`, shared by every resource:

```csharp
public const int DefaultRelatedLimit = 2;
public const int MaxRelatedLimit = 50;

// There is no universal depth limit. OData's ASP.NET Core default is 2 and Stripe allows 4.
// Choose the smallest depth your clients actually need.
public const int MaxDepth = 2;
```

The validator enforces them with the depth rule on `expand` (section 2) and this rule on `relatedLimit`:

```csharp
RuleFor(request => request.RelatedLimit)
    .InclusiveBetween(1, ExpandPlan.MaxRelatedLimit)
    .WithMessage($"relatedLimit must be between 1 and {ExpandPlan.MaxRelatedLimit}.")
    .OverridePropertyName("relatedLimit");
```

| Control | Value | Why |
|---|---|---|
| Allowlist | 3 paths | Only relationships you chose to expose |
| Max depth | 2 segments | `visits.club` is allowed, `visits.club.address` gets a clear error |
| Default `relatedLimit` | 2 | Small by default, and it makes `hasMore` easy to see in the demo |
| Max `relatedLimit` | 50 | Caps the rows loaded and serialized |
| Fetch size | limit + 1 | Tells the client whether more rows exist, without a `COUNT` |

The allowlist already stops paths deeper than `visits.club`. The explicit depth check gives a clearer error, and it still protects the API if someone adds a deeper path later.

`RelatedLimit` is an `int?`, so ASP.NET Core rejects a non-integer such as `abc` with 400 while binding, before the validator runs.

These are bounded previews, not pagination. When a client needs the full visit history, add a paginated endpoint such as `GET /api/memberships/{id}/visits?cursor=...`.

## 7. Complete implementation with example requests and responses

Run it:

```sh
docker compose up -d --wait
dotnet run --project src/ResourceExpansion.Api
```

```sh
curl "http://localhost:5080/api/memberships/1001?expand=member"
curl "http://localhost:5080/api/memberships/1001?expand=member,visits.club&relatedLimit=3"
```

[requests.http](../examples/requests.http) contains every scenario. `dotnet test` starts an isolated PostgreSQL container and checks the response shapes and the SQL each request runs.

How one expanded request compares with separate requests for the same data, measured with BenchmarkDotNet: [Performance: expanded vs. separate requests](performance.md).

<!-- generated examples and source below -->

## Example requests and responses

Captured from the running PostgreSQL-backed API. Dynamic trace IDs, if present, are omitted. All 11 captured cases are available in [examples/responses](../examples/responses); the manifest records request paths, tokens, and statuses.

### Baseline

```http
GET /api/memberships/1001
```

HTTP 200

```json
{
  "id": 1001,
  "number": "MEM-1001",
  "plan": "Premium",
  "status": "active",
  "startsOn": "2026-01-01",
  "endsOn": "2026-12-31",
  "monthlyFeeCents": 4900,
  "memberId": 1,
  "member": null,
  "visits": null
}
```

### Member

```http
GET /api/memberships/1001?expand=member
```

HTTP 200

```json
{
  "id": 1001,
  "number": "MEM-1001",
  "plan": "Premium",
  "status": "active",
  "startsOn": "2026-01-01",
  "endsOn": "2026-12-31",
  "monthlyFeeCents": 4900,
  "memberId": 1,
  "member": {
    "id": 1,
    "name": "Alice Morgan",
    "email": "alice@example.com"
  },
  "visits": null
}
```

### Visits

```http
GET /api/memberships/1001?expand=visits
```

HTTP 200

```json
{
  "id": 1001,
  "number": "MEM-1001",
  "plan": "Premium",
  "status": "active",
  "startsOn": "2026-01-01",
  "endsOn": "2026-12-31",
  "monthlyFeeCents": 4900,
  "memberId": 1,
  "member": null,
  "visits": {
    "data": [
      {
        "id": 3,
        "checkedInAt": "2026-09-27T06:45:00+00:00",
        "clubId": 1,
        "club": null
      },
      {
        "id": 2,
        "checkedInAt": "2026-09-24T18:15:00+00:00",
        "clubId": 2,
        "club": null
      }
    ],
    "limit": 2,
    "hasMore": true
  }
}
```

### Nested

```http
GET /api/memberships/1001?expand=member,visits.club&relatedLimit=3
```

HTTP 200

```json
{
  "id": 1001,
  "number": "MEM-1001",
  "plan": "Premium",
  "status": "active",
  "startsOn": "2026-01-01",
  "endsOn": "2026-12-31",
  "monthlyFeeCents": 4900,
  "memberId": 1,
  "member": {
    "id": 1,
    "name": "Alice Morgan",
    "email": "alice@example.com"
  },
  "visits": {
    "data": [
      {
        "id": 3,
        "checkedInAt": "2026-09-27T06:45:00+00:00",
        "clubId": 1,
        "club": {
          "id": 1,
          "name": "Downtown",
          "city": "Seattle"
        }
      },
      {
        "id": 2,
        "checkedInAt": "2026-09-24T18:15:00+00:00",
        "clubId": 2,
        "club": {
          "id": 2,
          "name": "Riverside",
          "city": "Bellevue"
        }
      },
      {
        "id": 1,
        "checkedInAt": "2026-09-20T07:30:00+00:00",
        "clubId": 1,
        "club": {
          "id": 1,
          "name": "Downtown",
          "city": "Seattle"
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
GET /api/memberships/1001?expand=visits&relatedLimit=1
```

HTTP 200

```json
{
  "id": 1001,
  "number": "MEM-1001",
  "plan": "Premium",
  "status": "active",
  "startsOn": "2026-01-01",
  "endsOn": "2026-12-31",
  "monthlyFeeCents": 4900,
  "memberId": 1,
  "member": null,
  "visits": {
    "data": [
      {
        "id": 3,
        "checkedInAt": "2026-09-27T06:45:00+00:00",
        "clubId": 1,
        "club": null
      }
    ],
    "limit": 1,
    "hasMore": true
  }
}
```

### Empty

```http
GET /api/memberships/1002?expand=visits
```

HTTP 200

```json
{
  "id": 1002,
  "number": "MEM-1002",
  "plan": "Premium",
  "status": "pending",
  "startsOn": "2027-01-01",
  "endsOn": "2027-12-31",
  "monthlyFeeCents": 4900,
  "memberId": 1,
  "member": null,
  "visits": {
    "data": [],
    "limit": 2,
    "hasMore": false
  }
}
```

### Unsupported

```http
GET /api/memberships/1001?expand=payments
```

HTTP 400

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "expand": [
      "Unsupported expansion: payments. Supported: member, visits, visits.club."
    ]
  }
}
```

### Depth

```http
GET /api/memberships/1001?expand=visits.club.address
```

HTTP 400

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "expand": [
      "Expansion depth cannot exceed 2: visits.club.address."
    ]
  }
}
```

### Missing Membership

```http
GET /api/memberships/9999
```

HTTP 404

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.5",
  "title": "Membership not found",
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
      - memberships-data:/var/lib/postgresql/data
    # Lets `docker compose up --wait` return only when the database accepts connections.
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U expansion -d expansion_demo"]
      interval: 2s
      timeout: 5s
      retries: 20

volumes:
  memberships-data:
```

### src/ResourceExpansion.Api/ResourceExpansion.Api.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
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
using ResourceExpansion.Api.Expansion;
using ResourceExpansion.Api.Features.Clubs.GetClub;
using ResourceExpansion.Api.Features.Members.GetMember;
using ResourceExpansion.Api.Features.Memberships.GetMembership;
using ResourceExpansion.Api.Features.Memberships.GetMembershipVisits;
using ResourceExpansion.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddSingleton<IValidator<GetMembershipRequest>>(
    new ExpandRequestValidator<GetMembershipRequest>(MembershipExpansions.Rules.Allowed));
builder.Services.AddDbContext<MembershipsDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Memberships")));

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapGetMembership();

// Standalone endpoints for the separate-request alternative in the performance comparison.
app.MapGetMember();
app.MapGetMembershipVisits();
app.MapGetClub();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MembershipsDbContext>();
    // A disposable demo schema. Use migrations for a real application.
    await db.Database.EnsureCreatedAsync();
    await DemoData.SeedAsync(db);
}

app.Run();

public partial class Program;
```

### src/ResourceExpansion.Api/Domain/Membership.cs

```csharp
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
```

### src/ResourceExpansion.Api/Infrastructure/MembershipsDbContext.cs

```csharp
using Microsoft.EntityFrameworkCore;
using ResourceExpansion.Api.Domain;

namespace ResourceExpansion.Api.Infrastructure;

// EF Core conventions discover the relationships from the navigation and foreign key properties.
public sealed class MembershipsDbContext(DbContextOptions<MembershipsDbContext> options) : DbContext(options)
{
    public DbSet<Membership> Memberships => Set<Membership>();
}
```

### src/ResourceExpansion.Api/Infrastructure/DemoData.cs

```csharp
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
```

### src/ResourceExpansion.Api/Expansion/IExpandableRequest.cs

```csharp
namespace ResourceExpansion.Api.Expansion;

// Implemented by any request that supports ?expand=...&relatedLimit=...
public interface IExpandableRequest
{
    string? Expand { get; }
    int? RelatedLimit { get; }
}
```

### src/ResourceExpansion.Api/Expansion/ExpandPlan.cs

```csharp
namespace ResourceExpansion.Api.Expansion;

// The validated expansion plan. Paths are lowercase and include their implied parents,
// so "visits.club" also selects "visits".
public sealed record ExpandPlan(IReadOnlySet<string> Paths, int RelatedLimit)
{
    public const int DefaultRelatedLimit = 2;
    public const int MaxRelatedLimit = 50;
    // There is no universal depth limit. OData's ASP.NET Core default is 2 and Stripe allows 4.
    // Choose the smallest depth your clients actually need.
    public const int MaxDepth = 2;

    public bool Has(string path) => Paths.Contains(path);

    public static HashSet<string> SplitPaths(string? expand) => (expand ?? "")
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(path => path.ToLowerInvariant())
        .ToHashSet();

    // Call only after ExpandRequestValidator has accepted the request.
    public static ExpandPlan From(IExpandableRequest request)
    {
        var paths = new HashSet<string>();
        foreach (var path in SplitPaths(request.Expand))
        {
            var segments = path.Split('.');
            for (var length = 1; length <= segments.Length; length++)
                paths.Add(string.Join('.', segments[..length]));
        }

        return new ExpandPlan(paths, request.RelatedLimit ?? DefaultRelatedLimit);
    }
}
```

### src/ResourceExpansion.Api/Expansion/ExpandRequestValidator.cs

```csharp
using FluentValidation;

namespace ResourceExpansion.Api.Expansion;

public sealed class ExpandRequestValidator<TRequest> : AbstractValidator<TRequest>
    where TRequest : IExpandableRequest
{
    public ExpandRequestValidator(IReadOnlyList<string> allowed)
    {
        // Report only the first failing rule for each parameter.
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(request => request.Expand)
            .Must(expand => TooDeep(expand).Length == 0)
            .WithMessage(request => $"Expansion depth cannot exceed {ExpandPlan.MaxDepth}: " +
                                    $"{string.Join(", ", TooDeep(request.Expand))}.")
            .Must(expand => Unsupported(expand, allowed).Length == 0)
            .WithMessage(request => $"Unsupported expansion: {string.Join(", ", Unsupported(request.Expand, allowed))}. " +
                                    $"Supported: {string.Join(", ", allowed)}.")
            .OverridePropertyName("expand");

        RuleFor(request => request.RelatedLimit)
            .InclusiveBetween(1, ExpandPlan.MaxRelatedLimit)
            .WithMessage($"relatedLimit must be between 1 and {ExpandPlan.MaxRelatedLimit}.")
            .OverridePropertyName("relatedLimit");
    }

    private static string[] TooDeep(string? expand) => ExpandPlan.SplitPaths(expand)
        .Where(path => path.Split('.').Length > ExpandPlan.MaxDepth)
        .ToArray();

    private static string[] Unsupported(string? expand, IReadOnlyList<string> allowed) => ExpandPlan.SplitPaths(expand)
        .Except(allowed)
        .ToArray();
}
```

### src/ResourceExpansion.Api/Expansion/ExpansionRules.cs

```csharp
namespace ResourceExpansion.Api.Expansion;

// The allowlist for one resource: each expandable path maps to a typed EF Core Include.
// Request strings are only compared against these paths; they never reach EF Core.
public sealed class ExpansionRules<TEntity> where TEntity : class
{
    private readonly List<(string Path, Func<IQueryable<TEntity>, int, IQueryable<TEntity>> Include)> rules = [];

    public IReadOnlyList<string> Allowed => rules.Select(rule => rule.Path).ToList();

    // The include receives the number of related rows to fetch. A nested path's include
    // must load its parents too, for example Include(...).ThenInclude(...).
    public ExpansionRules<TEntity> Allow(string path, Func<IQueryable<TEntity>, int, IQueryable<TEntity>> include)
    {
        rules.Add((path, include));
        return this;
    }

    public IQueryable<TEntity> Apply(IQueryable<TEntity> query, ExpandPlan plan)
    {
        // Fetch one extra related row so the response can report hasMore without a COUNT query.
        var take = plan.RelatedLimit + 1;

        foreach (var (path, include) in rules)
        {
            // Skip a parent when a selected nested path's include already loads it.
            if (plan.Has(path) && !plan.Paths.Any(other => other.StartsWith(path + '.')))
                query = include(query, take);
        }

        return query;
    }
}

public static class ExpansionQueryableExtensions
{
    public static IQueryable<TEntity> WithExpansions<TEntity>(this IQueryable<TEntity> query,
        ExpansionRules<TEntity> rules, ExpandPlan plan) where TEntity : class => rules.Apply(query, plan);
}
```

### src/ResourceExpansion.Api/Expansion/Related.cs

```csharp
namespace ResourceExpansion.Api.Expansion;

// A bounded preview of a related collection. The query loaded up to limit + 1 rows;
// the extra one only signals HasMore.
public sealed record Related<T>(IReadOnlyList<T> Data, int Limit, bool HasMore);

public static class Related
{
    public static Related<TResult> From<TSource, TResult>(IReadOnlyCollection<TSource> rows, int limit,
        Func<TSource, TResult> map) => new(
        Data: rows.Take(limit).Select(map).ToList(),
        Limit: limit,
        HasMore: rows.Count > limit);
}
```

### src/ResourceExpansion.Api/Features/Memberships/GetMembership/GetMembershipRequest.cs

```csharp
using ResourceExpansion.Api.Expansion;

namespace ResourceExpansion.Api.Features.Memberships.GetMembership;

// Route and query string for GET /api/memberships/{id}?expand=...&relatedLimit=...
public sealed record GetMembershipRequest(int Id, string? Expand, int? RelatedLimit) : IExpandableRequest;
```

### src/ResourceExpansion.Api/Features/Memberships/GetMembership/MembershipExpansions.cs

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using ResourceExpansion.Api.Domain;
using ResourceExpansion.Api.Expansion;

namespace ResourceExpansion.Api.Features.Memberships.GetMembership;

// Everything a client may expand on a membership. Adding an expansion is one Allow call
// plus a field in the response.
public static class MembershipExpansions
{
    public static readonly ExpansionRules<Membership> Rules = new ExpansionRules<Membership>()
        .Allow("member", (query, _) => query.Include(membership => membership.Member))
        .Allow("visits", IncludeVisits)
        .Allow("visits.club", (query, take) => IncludeVisits(query, take).ThenInclude(visit => visit.Club));

    // Most recent visits first; Id breaks ties so the preview is stable.
    private static IIncludableQueryable<Membership, IEnumerable<Visit>> IncludeVisits(IQueryable<Membership> query, int take) =>
        query.Include(membership => membership.Visits
            .OrderByDescending(visit => visit.CheckedInAt).ThenByDescending(visit => visit.Id).Take(take));
}
```

### src/ResourceExpansion.Api/Features/Memberships/GetMembership/Response.cs

```csharp
using ResourceExpansion.Api.Domain;
using ResourceExpansion.Api.Expansion;

namespace ResourceExpansion.Api.Features.Memberships.GetMembership;

// Unrequested relationships are null. Requested but empty visits are { "data": [] }.
public sealed record MembershipResponse(
    int Id, string Number, string Plan, string Status, DateOnly StartsOn, DateOnly EndsOn,
    long MonthlyFeeCents, int MemberId,
    MemberResponse? Member,
    Related<VisitResponse>? Visits);

public sealed record MemberResponse(int Id, string Name, string Email);

public sealed record VisitResponse(int Id, DateTimeOffset CheckedInAt, int ClubId, ClubResponse? Club);

public sealed record ClubResponse(int Id, string Name, string City);

// Mapping reads the plan, not whether a navigation happens to be loaded.
public static class MembershipMapping
{
    public static MembershipResponse ToResponse(this Membership membership, ExpandPlan plan) => new(
        membership.Id, membership.Number, membership.Plan, membership.Status,
        membership.StartsOn, membership.EndsOn, membership.MonthlyFeeCents, membership.MemberId,
        Member: plan.Has("member")
            ? new MemberResponse(membership.Member.Id, membership.Member.Name, membership.Member.Email)
            : null,
        Visits: plan.Has("visits")
            ? Related.From(membership.Visits, plan.RelatedLimit, visit => new VisitResponse(
                visit.Id, visit.CheckedInAt, visit.ClubId,
                Club: plan.Has("visits.club") ? new ClubResponse(visit.Club.Id, visit.Club.Name, visit.Club.City) : null))
            : null);
}
```

### src/ResourceExpansion.Api/Features/Memberships/GetMembership/Endpoint.cs

```csharp
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ResourceExpansion.Api.Expansion;
using ResourceExpansion.Api.Infrastructure;

namespace ResourceExpansion.Api.Features.Memberships.GetMembership;

public static class Endpoint
{
    public static void MapGetMembership(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/memberships/{id:int}", HandleAsync);

    private static async Task<IResult> HandleAsync([AsParameters] GetMembershipRequest request,
        IValidator<GetMembershipRequest> validator, MembershipsDbContext db, CancellationToken cancellationToken)
    {
        // 1. Validate the requested expansions against the allowlist.
        var validation = validator.Validate(request);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var plan = ExpandPlan.From(request);

        // 2. Load only the requested relationships.
        var membership = await db.Memberships
            .AsNoTracking()
            .Where(membership => membership.Id == request.Id)
            .WithExpansions(MembershipExpansions.Rules, plan)
            .SingleOrDefaultAsync(cancellationToken);

        // 3. Map to the response.
        return membership is null
            ? Results.Problem(statusCode: 404, title: "Membership not found")
            : Results.Ok(membership.ToResponse(plan));
    }
}
```

### src/ResourceExpansion.Api/Features/Members/GetMember/Endpoint.cs

```csharp
using Microsoft.EntityFrameworkCore;
using ResourceExpansion.Api.Domain;
using ResourceExpansion.Api.Features.Memberships.GetMembership;
using ResourceExpansion.Api.Infrastructure;

namespace ResourceExpansion.Api.Features.Members.GetMember;

// A standalone resource endpoint. The performance comparison uses it as the separate-request
// alternative to ?expand=member.
public static class Endpoint
{
    public static void MapGetMember(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/members/{id:int}", HandleAsync);

    private static async Task<IResult> HandleAsync(int id, MembershipsDbContext db, CancellationToken cancellationToken)
    {
        var member = await db.Set<Member>()
            .AsNoTracking()
            .SingleOrDefaultAsync(member => member.Id == id, cancellationToken);

        return member is null
            ? Results.Problem(statusCode: 404, title: "Member not found")
            : Results.Ok(new MemberResponse(member.Id, member.Name, member.Email));
    }
}
```

### src/ResourceExpansion.Api/Features/Memberships/GetMembershipVisits/Endpoint.cs

```csharp
using Microsoft.EntityFrameworkCore;
using ResourceExpansion.Api.Domain;
using ResourceExpansion.Api.Expansion;
using ResourceExpansion.Api.Features.Memberships.GetMembership;
using ResourceExpansion.Api.Infrastructure;

namespace ResourceExpansion.Api.Features.Memberships.GetMembershipVisits;

// A standalone sub-resource endpoint. The performance comparison uses it as the separate-request
// alternative to ?expand=visits. It returns the same bounded preview, with clubs as ids only.
public static class Endpoint
{
    public static void MapGetMembershipVisits(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/memberships/{id:int}/visits", HandleAsync);

    private static async Task<IResult> HandleAsync(int id, int? limit, MembershipsDbContext db,
        CancellationToken cancellationToken)
    {
        var take = limit ?? ExpandPlan.DefaultRelatedLimit;
        if (take is < 1 or > ExpandPlan.MaxRelatedLimit)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["limit"] = [$"limit must be between 1 and {ExpandPlan.MaxRelatedLimit}."]
            });

        // Same ordering as the visits expansion, and one extra row to report hasMore.
        var visits = await db.Set<Visit>()
            .AsNoTracking()
            .Where(visit => visit.MembershipId == id)
            .OrderByDescending(visit => visit.CheckedInAt).ThenByDescending(visit => visit.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken);

        return Results.Ok(Related.From(visits, take,
            visit => new VisitResponse(visit.Id, visit.CheckedInAt, visit.ClubId, Club: null)));
    }
}
```

### src/ResourceExpansion.Api/Features/Clubs/GetClub/Endpoint.cs

```csharp
using Microsoft.EntityFrameworkCore;
using ResourceExpansion.Api.Domain;
using ResourceExpansion.Api.Features.Memberships.GetMembership;
using ResourceExpansion.Api.Infrastructure;

namespace ResourceExpansion.Api.Features.Clubs.GetClub;

// A standalone resource endpoint. The performance comparison uses it as the separate-request
// alternative to ?expand=visits.club.
public static class Endpoint
{
    public static void MapGetClub(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/clubs/{id:int}", HandleAsync);

    private static async Task<IResult> HandleAsync(int id, MembershipsDbContext db, CancellationToken cancellationToken)
    {
        var club = await db.Set<Club>()
            .AsNoTracking()
            .SingleOrDefaultAsync(club => club.Id == id, cancellationToken);

        return club is null
            ? Results.Problem(statusCode: 404, title: "Club not found")
            : Results.Ok(new ClubResponse(club.Id, club.Name, club.City));
    }
}
```

### src/ResourceExpansion.Api/appsettings.json

```json
{
  "ConnectionStrings": {
    "Memberships": "Host=localhost;Port=54329;Database=expansion_demo;Username=expansion;Password=local-demo-password"
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
