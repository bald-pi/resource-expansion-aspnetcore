# Resource expansion with ASP.NET Core

Companion code for a blog post on resource expansion (`?expand=`) in ASP.NET Core. It uses .NET 10 Minimal APIs, FluentValidation, EF Core, and PostgreSQL.

`GET /api/memberships/{id}` returns a gym membership. By default, related data is left out. Clients can ask for it with `?expand=`:

- `member`: the member who owns the membership (a single reference).
- `visits`: a bounded preview of recent check-ins (a collection).
- `visits.club`: those check-ins, each with the club it happened at (nested expansion).

The expansion code in `src/ResourceExpansion.Api/Expansion` is generic. It knows nothing about memberships, and each resource declares its own allowlist of paths.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker](https://docs.docker.com/get-docker/) (runs PostgreSQL for the app, the tests, and the benchmark)

## Quick start

From the repository root:

```sh
docker compose up -d --wait
dotnet run --project src/ResourceExpansion.Api
```

`docker compose` starts PostgreSQL 17 on `127.0.0.1:54329`. The API listens on `http://localhost:5080`. On startup it creates the schema and seeds two memberships:

- `1001`: active, with three visits.
- `1002`: pending renewal, with no visits.

```sh
curl "http://localhost:5080/api/memberships/1001"
curl "http://localhost:5080/api/memberships/1001?expand=member"
curl "http://localhost:5080/api/memberships/1001?expand=member,visits.club&relatedLimit=3"
curl "http://localhost:5080/api/memberships/1001?expand=payments"   # 400
```

In Windows PowerShell, type `curl.exe` instead of `curl`, because `curl` is an alias for `Invoke-WebRequest` there. [examples/requests.http](examples/requests.http) contains every case and runs in Visual Studio, Rider, or the VS Code REST Client.

When you are done:

```sh
docker compose stop      # stop PostgreSQL and keep its data
docker compose down -v   # remove the container and its data
```

The sample creates its schema with `EnsureCreated` instead of migrations. If you change the model, run `docker compose down -v` to reset the database.

## Tests

```sh
dotnet test tests/ResourceExpansion.Tests
```

The integration tests start their own disposable PostgreSQL container with Testcontainers, so Docker has to be running. The compose database is not needed. The parser and validator unit tests don't need Docker:

```sh
dotnet test tests/ResourceExpansion.Tests --filter "FullyQualifiedName~ExpansionTests"
```

## Benchmark

The benchmark compares one expanded request with fetching the same data through separate endpoints (`/api/members/{id}`, `/api/memberships/{id}/visits`, `/api/clubs/{id}`). Docker has to be running:

```sh
dotnet run -c Release --project benchmarks/ResourceExpansion.Benchmarks -- --filter '*'
```

It builds the API image and starts the API, PostgreSQL, and Toxiproxy (for simulated network latency) from `benchmarks/docker/compose.yaml`. It removes those containers when it finishes. A full run takes about 5 minutes, plus the first image build.

## Project structure

```text
src/ResourceExpansion.Api/
  Expansion/                          Generic, reusable by any resource
    ExpandableRequest.cs              Base record: Expand + RelatedLimit
    ExpandPlan.cs                     Parsing, implied parents, limits
    ExpandRequestValidator.cs         FluentValidation rules for expand and relatedLimit
    ExpansionRules.cs                 Allowlist: path -> typed EF Core Include
    Related.cs                        { data, limit, hasMore } collection preview
  Features/
    Memberships/GetMembership/        GET /api/memberships/{id}?expand=...
      GetMembershipRequest.cs         Route and query string
      MembershipExpansions.cs         Expandable paths for a membership
      Response.cs                     DTOs and mapping
      Endpoint.cs                     Validate -> load -> map
    Members/GetMember/                GET /api/members/{id}            standalone endpoints used by
    Memberships/GetMembershipVisits/  GET /api/memberships/{id}/visits the benchmark's separate-
    Clubs/GetClub/                    GET /api/clubs/{id}              request clients
  Domain/Membership.cs                Membership, Member, Visit, Club
  Infrastructure/                     MembershipsDbContext, DemoData (seed)
  Program.cs
  Dockerfile                          API image used by the benchmark
tests/ResourceExpansion.Tests/        xUnit: parser/validator unit tests + integration tests (Testcontainers)
benchmarks/
  ResourceExpansion.Benchmarks/       BenchmarkDotNet: expanded vs. separate requests
  docker/                             compose.yaml (API + PostgreSQL + Toxiproxy), toxiproxy.json
examples/requests.http                Every request case, runnable from the IDE
compose.yaml                          Local PostgreSQL for dotnet run
```

## Expansion contract

| Topic | Behavior |
|---|---|
| Allowed paths | `member`, `visits`, `visits.club` |
| Nested selection | `visits.club` implies `visits` |
| Normalization | Lowercased, trimmed, and deduplicated; empty entries are ignored |
| Missing or blank `expand` | Baseline response with no related data |
| Unrequested relationship | JSON `null` |
| Expanded visits | `{ "data": [...], "limit": 2, "hasMore": true }`, most recent first; `"data": []` when there are none |
| `relatedLimit` | Default 2, allowed range 1-50. The query fetches one extra row to compute `hasMore` without a `COUNT` |
| Depth | At most 2 segments (`visits.club.address` is rejected) |
| Unsupported path, too deep, or `relatedLimit` out of range | `400` validation Problem Details |
| Unknown membership id | `404` Problem Details |
| Adding an expansion | One `Allow(path, include)` call in `MembershipExpansions` plus a response field |

Expanded visits are a bounded preview, not a paginated list. Fees are stored as integer cents.
