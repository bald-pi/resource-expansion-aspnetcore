# Resource Expansion in ASP.NET Core

Recently, my team wanted one endpoint that could return an entity together with its related data. Our standard approach was a separate endpoint per resource, but the extra HTTP round trips added up on the client. Inspired by how [Stripe](https://docs.stripe.com/expand) and [Atlassian](https://developer.atlassian.com/cloud/jira/platform/rest/v3/intro/#expansion) handle this in their APIs, we built an expand mechanism of our own.

This post walks through that mechanism with a small gym-membership API built on .NET 10 Minimal APIs, FluentValidation, EF Core, and PostgreSQL. You will see how to parse and validate `?expand=`, load only the requested relationships, keep the response contract predictable, and cap how much a single request can load. At the end, a benchmark compares one expanded request against separate requests for the same data.

## What is resource expansion?

Resource expansion lets the client ask for related data inside the main resource's response. The server includes a relationship only when the client explicitly requests it.

Say part of the application you are building shows a gym membership. A request for the membership returns its own fields:

```http
GET /api/memberships/1001
```

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

That is enough for a membership card. The front-desk screen needs more: the member's name and email, and the clubs where they checked in most recently. With separate endpoints, the client fetches the membership, reads `memberId`, calls `/api/members/1`, then calls `/api/memberships/1001/visits`, and finally `/api/clubs/{id}` for each club. It then stitches the responses into one view model.

With expansion, the client names the relationships it wants in a query parameter:

```text
client                                   API
  | GET /api/memberships/1001              |
  |--------------------------------------->|  membership only
  |                                        |
  | GET /api/memberships/1001              |
  |     ?expand=member,visits.club         |
  |--------------------------------------->|  membership + member
  |<---------------------------------------|  + recent visits, each with its club
```

```http
GET /api/memberships/1001?expand=member,visits.club&relatedLimit=3
```

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
  "member": { "id": 1, "name": "Alice Morgan", "email": "alice@example.com" },
  "visits": {
    "data": [
      { "id": 3, "checkedInAt": "2026-09-27T06:45:00+00:00", "clubId": 1,
        "club": { "id": 1, "name": "Downtown", "city": "Seattle" } },
      { "id": 2, "checkedInAt": "2026-09-24T18:15:00+00:00", "clubId": 2,
        "club": { "id": 2, "name": "Riverside", "city": "Bellevue" } },
      { "id": 1, "checkedInAt": "2026-09-20T07:30:00+00:00", "clubId": 1,
        "club": { "id": 1, "name": "Downtown", "city": "Seattle" } }
    ],
    "limit": 3,
    "hasMore": false
  }
}
```

One HTTP request now carries everything the screen needs. Keep one distinction in mind: fewer HTTP requests do not automatically mean fewer database queries. That depends on how the backend loads the data. In this implementation every expansion combination runs as a single SQL query, but a naive implementation could just as easily run one query per relationship, or one per club.

## Implementing resource expansion in ASP.NET Core

The full sample is on GitHub: [bald-pi/resource-expansion-aspnetcore](https://github.com/bald-pi/resource-expansion-aspnetcore). The expansion code lives in a generic `Expansion` folder and knows nothing about memberships. Each resource only declares its own allowlist.

First, the foundation: entities, request and response, and the endpoint.

### Entities

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

The relationships are straightforward: a member can have multiple memberships, a membership can have multiple visits (check-ins), and each visit belongs to one club. Fees are stored as integer cents.

### Request

The route and the query string bind to one record through [`[AsParameters]`](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/parameter-binding). `Id` comes from the route; `Expand` and `RelatedLimit` come from the query string:

```csharp
public interface IExpandableRequest
{
    string? Expand { get; }
    int? RelatedLimit { get; }
}

public sealed record GetMembershipRequest(int Id, string? Expand, int? RelatedLimit) : IExpandableRequest;
```

`Expand` holds the comma-separated relationships the client wants. `RelatedLimit` caps how many visits come back; more on that in the limits section. Because the request implements `IExpandableRequest`, the shared expansion code can read both values from any resource's request.

### Response

```csharp
// Unrequested relationships are null. Requested but empty visits are { "data": [] }.
public sealed record MembershipResponse(
    int Id, string Number, string Plan, string Status, DateOnly StartsOn, DateOnly EndsOn,
    long MonthlyFeeCents, int MemberId,
    MemberResponse? Member,
    Related<VisitResponse>? Visits);

public sealed record MemberResponse(int Id, string Name, string Email);

public sealed record VisitResponse(int Id, DateTimeOffset CheckedInAt, int ClubId, ClubResponse? Club);

public sealed record ClubResponse(int Id, string Name, string City);
```

The response always contains the membership's own fields plus `memberId`. `Member` and `Visits` are nullable, and the API fills them only when the client requests the corresponding relationship. The same applies one level down: each visit's `Club` stays `null` unless the client asks for `visits.club`.

### Endpoint

The endpoint maps `GET /api/memberships/{id}` to a handler:

```csharp
public static void MapGetMembership(this IEndpointRouteBuilder app) =>
    app.MapGet("/api/memberships/{id:int}", HandleAsync);
```

The handler does three things: validate, load, and map. The next sections build each step, and the full handler follows once the pieces are in place.

### Parse and validate the expand parameter using an allowlist

The endpoint supports three expansion paths:

```http
GET /api/memberships/1001?expand=member
GET /api/memberships/1001?expand=visits
GET /api/memberships/1001?expand=visits.club
```

Clients can combine them, for example `?expand=member,visits.club`. `visits.club` is a nested path: it loads the visits and the club of each visit.

Since `expand` is a query parameter, clients can send any value. You need an allowlist of supported relationships and input validation. Each resource declares its allowlist once, and each path maps to a typed EF Core `Include`:

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

The client's string is only compared with these paths. It never reaches EF Core's string-based `Include("...")`, so a client cannot load a navigation you did not choose to expose.

Handling the input takes two steps:

1. **Parsing and normalization.** Split the value into relationship names, trim whitespace, ignore empty entries, lowercase each name, and remove duplicates.
2. **Validation.** Check every parsed name against the allowlist.

Parsing is one helper on `ExpandPlan`:

```csharp
public static HashSet<string> SplitPaths(string? expand) => (expand ?? "")
    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
    .Select(path => path.ToLowerInvariant())
    .ToHashSet();
```

So `?expand= MEMBER ,visits.club,member` means the same as `?expand=member,visits.club`.

I used [FluentValidation](https://docs.fluentvalidation.net/) for the validation step. The validator is generic and receives the allowlist, so every expandable resource reuses it:

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

A few details matter here:

- `CascadeMode.Stop` reports only the first failing rule per parameter, so a path that is too deep gets the depth error and not a second "unsupported" error.
- `expand` and `relatedLimit` are validated independently, so the client sees every problem in one response.
- `OverridePropertyName` keeps the error keys equal to the query parameter names (`expand`, not `Expand`).
- `InclusiveBetween` skips `null`, so an omitted `relatedLimit` falls back to the default.

Register the validator once per resource, with that resource's allowlist:

```csharp
builder.Services.AddSingleton<IValidator<GetMembershipRequest>>(
    new ExpandRequestValidator<GetMembershipRequest>(MembershipExpansions.Rules.Allowed));
```

Once the request passes validation, it becomes an `ExpandPlan`: the requested paths plus every implied parent, so `visits.club` also selects `visits`. This step needs no error handling, because the validator has already accepted the input:

```csharp
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
```

A missing or blank `expand` produces an empty plan and the baseline response.

### Load the requested relationships with EF Core

With a valid plan, you can build the query. The idea is simple: check which relationships the client requested and add the corresponding `Include` calls. The generic `ExpansionRules` does this for any entity:

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

A `WithExpansions` extension method wraps `Apply`, so the endpoint uses it like any other LINQ operator:

```csharp
var membership = await db.Memberships
    .AsNoTracking()
    .Where(membership => membership.Id == request.Id)
    .WithExpansions(MembershipExpansions.Rules, plan)
    .SingleOrDefaultAsync(cancellationToken);
```

What each expansion does to the query:

- **`member`** adds a join through `Include(membership => membership.Member)`.
- **`visits`** uses a [filtered include](https://learn.microsoft.com/en-us/ef/core/querying/related-data/eager#filtered-include): `OrderByDescending(...).Take(take)` makes PostgreSQL return only the most recent visits. The `Id` tie-breaker keeps the preview stable when two check-ins share a timestamp.
- **`visits.club`** adds `ThenInclude(visit => visit.Club)`, which loads each visit's club in the same query. There is no extra query per club. Because this include already loads the visits, `Apply` skips the parent's own include, and `?expand=visits,visits.club` includes the visits once.

Every combination runs as a single `SELECT`. If you later add a second collection, such as payments, add `AsSplitQuery()` so the two collections do not multiply each other's rows. The EF Core docs explain the trade-off in [single vs. split queries](https://learn.microsoft.com/en-us/ef/core/querying/single-split-queries).

Adding a new expansion takes one `Allow` call and a field in the response. The parser, validator, and loading code stay the same.

### Build the response and represent unrequested relationships

Finally, the endpoint maps the loaded entity to the response. The mapping reads the plan, not whether a navigation happens to be loaded:

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

Collections go through a small generic wrapper. The query loaded up to `limit + 1` rows; the extra row only signals `hasMore`:

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

In this response contract, a relationship has three states:

| State | JSON |
|---|---|
| Not requested | `"visits": null` |
| Requested, no items | `"visits": { "data": [], "limit": 2, "hasMore": false }` |
| Requested, more rows exist | `"visits": { "data": [ ... ], "limit": 2, "hasMore": true }` |

System.Text.Json writes `null` properties by default. Keep that default, or clients cannot tell "not requested" apart from "missing". And because the visit list is a bounded preview, clients must not treat its length as the member's total number of visits.

### Handle unsupported expansions and enforce authorization

Now the handler can put the pieces together. It validates before it touches the database, so an invalid request never runs a query:

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

An unknown path fails the allowlist rule. `validation.ToDictionary()` groups the failures by parameter name, and `Results.ValidationProblem` turns them into a standard [Problem Details](https://www.rfc-editor.org/rfc/rfc9457) response:

```http
GET /api/memberships/1001?expand=payments
```

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

The error lists the supported paths, so the client can fix the request without reading the docs.

**Authorization.** Each expanded relationship must follow the same rules that would apply if the client fetched it directly. If `GET /api/payments` requires billing staff, `?expand=payments` requires them too. Otherwise `expand` becomes a way around your authorization. In this sample, anyone who can read the membership can also see the member, visits, and clubs, so no extra check is needed. When you add a protected relationship, check its policy right after validation and before the query, and return 403 for the whole request if the check fails. Do not silently drop the field: the client would read the missing data as "none".

### Limit expansion depth and related collection sizes

Without limits, a single request can turn into a deep, unbounded join. The limits are constants on `ExpandPlan`, shared by every resource:

```csharp
public const int DefaultRelatedLimit = 2;
public const int MaxRelatedLimit = 50;
// There is no universal depth limit. OData's ASP.NET Core default is 2 and Stripe allows 4.
// Choose the smallest depth your clients actually need.
public const int MaxDepth = 2;
```

The validator enforces them with the depth rule on `expand` and the range rule on `relatedLimit`:

| Control | Value | Why |
|---|---|---|
| Allowlist | 3 paths | Only relationships you chose to expose |
| Max depth | 2 segments | `visits.club` is allowed; `visits.club.address` gets a clear error |
| Default `relatedLimit` | 2 | Small by default |
| Max `relatedLimit` | 50 | Caps the rows loaded and serialized |
| Fetch size | limit + 1 | Tells the client whether more rows exist, without a `COUNT` |

The allowlist already rejects paths deeper than `visits.club`. The explicit depth check gives a clearer error, and it still protects the API if someone adds a deeper path later:

```json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "expand": [ "Expansion depth cannot exceed 2: visits.club.address." ]
  }
}
```

A `relatedLimit` of 51 returns `"relatedLimit must be between 1 and 50."`. Because `RelatedLimit` is an `int?`, ASP.NET Core rejects a non-integer such as `abc` with 400 during binding, before the validator runs.

These are bounded previews, not pagination. When a client needs the full visit history, give it a paginated endpoint such as `GET /api/memberships/{id}/visits?cursor=...`.

### The complete implementation in action

Start PostgreSQL and the API from the repository root:

```sh
docker compose up -d --wait
dotnet run --project src/ResourceExpansion.Api
```

The API seeds two memberships: `1001` with three visits, and `1002`, a pending renewal with no visits. Each request below was captured from the running API:

| Request | Result |
|---|---|
| `/api/memberships/1001` | Membership fields only; `member` and `visits` are `null` |
| `/api/memberships/1001?expand=member` | Member object; `visits` is `null` |
| `/api/memberships/1001?expand=visits` | Two most recent visits, `club` is `null`, `hasMore: true` |
| `/api/memberships/1001?expand=member,visits.club&relatedLimit=3` | Member and all three visits with clubs |
| `/api/memberships/1001?expand=visits&relatedLimit=1` | One visit, `hasMore: true` |
| `/api/memberships/1002?expand=visits` | `"data": []`, `hasMore: false` |
| `/api/memberships/1001?expand=payments` | 400, lists supported paths |
| `/api/memberships/1001?expand=visits.club.address` | 400, depth exceeded |
| `/api/memberships/1001?expand=visits&relatedLimit=51` | 400, limit out of range |
| `/api/memberships/9999` | 404 Problem Details |

For example, expanding only `visits` returns the two most recent check-ins without their clubs:

```http
GET /api/memberships/1001?expand=visits
```

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
      { "id": 3, "checkedInAt": "2026-09-27T06:45:00+00:00", "clubId": 1, "club": null },
      { "id": 2, "checkedInAt": "2026-09-24T18:15:00+00:00", "clubId": 2, "club": null }
    ],
    "limit": 2,
    "hasMore": true
  }
}
```

The repository's `examples/requests.http` contains every scenario, and `examples/responses` holds the captured JSON. The integration tests start a disposable PostgreSQL container with Testcontainers and check both the response shapes and the SQL each request runs.

## Performance: expanded requests vs. separate requests

Expansion reduces HTTP calls, but does it improve performance? I tested two sets of calls that return the same data: one membership, its member, and the latest `N` visits with the club of each visit.

Separate requests:

```http
GET /api/memberships/1
GET /api/members/{memberId}
GET /api/memberships/1/visits?limit=N
GET /api/clubs/{clubId}        (once per distinct club)
```

Expansion:

```http
GET /api/memberships/1?expand=member,visits.club&relatedLimit=N
```

The separate-request client ran in two variants. **SeparateParallel** fetches the membership, then the member and visits in parallel, then all clubs in parallel, which makes three dependent round trips. **SeparateSequential** awaits each request before sending the next, as a straightforward client would. An integration test verifies that both paths return the same data.

### Test setup

The tests ran with [BenchmarkDotNet](https://benchmarkdotnet.org/) 0.15.8 on my laptop: 13th Gen Intel Core i7-13620H (10 cores, 16 logical), Windows 11, .NET SDK 10.0.103.

The API (ASP.NET Core 10, EF Core with Npgsql 10.0.0) and PostgreSQL 17 run in Docker Desktop on the same laptop, with no container resource limits. [Toxiproxy](https://github.com/Shopify/toxiproxy) sits in front of both to simulate network latency:

- API to database: 1 ms per response, like a database in the same data center.
- Client to API: 0 ms (loopback) or 20 ms (a nearby region).

The benchmark client runs on the host and uses one shared `HttpClient` with HTTP/1.1 keep-alive and no response compression. Neither approach uses caching. The dataset has one membership, one member, 20 clubs, and 1,000 visits assigned to clubs round-robin, so a preview of 1, 10, or 50 visits references 1, 10, or 20 distinct clubs.

### What is measured?

For separate requests, I measured the time from sending the first request until the client has received and parsed every response body. For expansion, the time until the client has received and parsed the expanded response. Before collecting results, BenchmarkDotNet ran 3 warm-up iterations, followed by 15 measured iterations per scenario. The benchmark also warmed up all clients for 20 seconds before the first case, so JIT compilation and connection setup do not skew the numbers.

The comparison covers:

- **Mean and P95 latency:** the average end-to-end time, and the time within which 95% of operations complete.
- **Response size:** response body bytes per operation, summed across separate requests.
- **Database query count:** SQL `SELECT` statements executed per operation, read from `pg_stat_statements`.

### Results

With 20 ms of client latency:

| Visits (`N`) | Approach | Requests | SQL queries | Mean | P95 | Response bytes |
|---|---|---:|---:|---:|---:|---:|
| 1 | Expanded | 1 | 1 | 34.5 ms | 41.9 ms | 372 |
| 1 | SeparateParallel | 4 | 4 | 73.9 ms | 82.2 ms | 384 |
| 1 | SeparateSequential | 4 | 4 | 103.8 ms | 124.4 ms | 384 |
| 10 | Expanded | 1 | 1 | 24.2 ms | 24.5 ms | 1,417 |
| 10 | SeparateParallel | 13 | 13 | 76.7 ms | 81.1 ms | 1,465 |
| 10 | SeparateSequential | 13 | 13 | 306.0 ms | 314.7 ms | 1,465 |
| 50 | Expanded | 1 | 1 | 24.8 ms | 25.4 ms | 6,003 |
| 50 | SeparateParallel | 23 | 23 | 81.9 ms | 91.2 ms | 4,941 |
| 50 | SeparateSequential | 23 | 23 | 556.1 ms | 587.3 ms | 4,941 |

The expanded request pays one network latency plus a few milliseconds of server work, about 24 to 25 ms. The 1-visit case measured 34.5 ms because of a noisy stretch on the host; its first iterations also took 24 ms. SeparateParallel pays three latencies and lands at 74 to 82 ms, about 3x slower. SeparateSequential pays one latency per request: 104 ms for 4 requests and 556 ms for 23, up to 22x slower.

On loopback (0 ms), the gaps shrink in absolute terms but remain: 3.4 to 3.8 ms expanded, against 10 to 16 ms parallel and 12 to 61 ms sequential.

The expanded request always ran exactly one SQL query. The separate clients ran one query per request, and parallelism did not change that: SeparateParallel executed as many queries as SeparateSequential. Parallel requests reduce waiting, not work.

Response size tells a different story. For 1 and 10 visits, the bodies are nearly equal. With 50 visits, the expanded response is 21% larger, because 50 visits share 20 clubs and the expanded response embeds a club object in every visit. The separate client downloads each club once. The more a related object repeats across a collection, the faster the expanded payload grows.

Fewer HTTP requests do not automatically mean fewer database queries. The expanded endpoint still has to retrieve all requested data, and its query strategy decides how much work reaches the database. Here, a single query with a filtered include kept that cost flat.

So the verdict: expansion gives clients a convenient way to fetch related data together. Whether it improves performance depends on network latency, the database queries behind it, and how much data the client requests. In this benchmark, round trips multiplied by latency dominated everything else.

Expansion can also add overhead:

- **Duplicated nested objects**, as the 50-visit case shows. A de-duplicated `included` section, as in [JSON:API compound documents](https://jsonapi.org/format/#document-compound-documents), avoids it.
- **Wider SQL rows.** Joins repeat parent columns on every child row. That did not hurt at this size, but it grows with large parents or several joined collections.
- **Over-fetching.** A client that already caches the clubs re-downloads them with every expanded response.
- **HTTP caching.** `/api/clubs/{id}` is a stable, cacheable resource. An expanded response mixes stable and volatile data, so caches must invalidate it whenever any part changes.

## When to use resource expansion?

Honestly, I don't have a straightforward answer. The approach comes with trade-offs:

| Advantages | Disadvantages |
|---|---|
| Fewer HTTP round trips to retrieve related resources | Expanded responses can become large and expensive to generate |
| Clients choose which supported relationships to include | Each expansion combination needs validation and testing |
| Less client-side work combining separate responses | Database queries grow more complex as you add relationships |
| One endpoint serves different data needs | More response variations to document and maintain |

Optional expansion pays off for relationships that clients almost always load together, especially when the dependency chain is more than one level deep or the client sits far from the API, such as a mobile app. That is where the 3x to 22x differences above came from.

Keep separate or dedicated endpoints when clients already cache the related data, when the related resource is stable and benefits from HTTP caching, or when a client needs a full, paginated collection rather than a preview. The standalone endpoints in the sample stay in place for exactly those cases.

Expansion also carries a maintenance cost. Every new relationship adds another response shape to document, test, and authorize, and every combination is a query shape you have to keep fast.

To sum up: keep expansions focused on what clients actually need, bound them with an allowlist, a depth limit, and a collection limit, and measure the impact before assuming fewer requests mean better performance.
