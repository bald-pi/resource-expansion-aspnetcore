## Performance: Expanded Requests vs. Separate Requests

This page measures what `?expand=` saves, and what it costs, compared with fetching the same data through standalone endpoints. All numbers come from one BenchmarkDotNet run of `benchmarks/ResourceExpansion.Benchmarks`.

Reproduce (Docker must be running; the benchmark starts and removes its own containers):

```sh
dotnet run -c Release --project benchmarks/ResourceExpansion.Benchmarks -- --filter '*'
```

### Compared approaches

Each client ends up with the same data: one membership, its member, and the latest `N` visits (`N` = `RelatedLimit`) with the club of each visit. Every client reads and parses every response body with `System.Text.Json`.

| Client | Requests it makes | Round trips on the critical path |
|---|---|---|
| **Expanded** (baseline) | `GET /api/memberships/1?expand=member,visits.club&relatedLimit=N` | 1 |
| **SeparateParallel** | `GET /api/memberships/1`, then `GET /api/members/{memberId}` and `GET /api/memberships/1/visits?limit=N` in parallel, then `GET /api/clubs/{id}` for every distinct club in parallel | 3 |
| **SeparateSequential** | The same requests, one after another | 3 + distinct clubs |

The expanded request is served by one SQL query: EF Core `Include(member)` plus a filtered `Include(visits).ThenInclude(club)` that takes `N + 1` rows to report `hasMore`. Its response embeds the full club object inside every visit, so a club visited five times appears five times.

The separate endpoints return the same shapes without nesting: `/visits` returns the same bounded, identically ordered preview with `clubId` only, and the client then fetches each distinct club once. The separate client depends on the order of calls: it needs the membership for `memberId` and the visits for the club ids, so the dependency chain is three levels deep whatever it parallelizes.

The two paths are checked to return equivalent data by `Separate_requests_return_the_same_data_as_expansion` in `tests/ResourceExpansion.Tests/GetMembershipTests.cs`. It compares the expanded member with `/api/members/{id}`, the visit ids and `hasMore` with `/api/memberships/{id}/visits`, and each embedded club with `/api/clubs/{id}`, byte for byte.

### Dataset, environment, and request execution strategy

**Dataset.** The benchmark truncates the demo data and seeds one membership, one member, 20 clubs, and 1,000 visits assigned to clubs round-robin. A preview of `N` visits therefore references `min(N, 20)` distinct clubs: 1, 10, and 20 clubs for `RelatedLimit` 1, 10, and 50.

**Environment** (from the BenchmarkDotNet header):

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
13th Gen Intel Core i7-13620H 2.40GHz, 1 CPU, 16 logical and 10 physical cores
.NET SDK 10.0.103
  [Host] : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v3
```

The API (ASP.NET Core 10, EF Core with Npgsql) runs in Docker Desktop on the same laptop, with PostgreSQL 17 and Toxiproxy, all started from `benchmarks/docker/compose.yaml`:

```
benchmark client (host, BenchmarkDotNet)
   |  HTTP, localhost:18080
   v
toxiproxy "api" proxy ----- latency: NetworkLatencyMs (0 or 20 ms) per response
   |
   v
api container (:8080) ----- ASP.NET Core 10 + EF Core
   |  TCP, toxiproxy:15432
   v
toxiproxy "postgres" proxy - latency: 1 ms per response
   |
   v
postgres container (:5432) - PostgreSQL 17 with pg_stat_statements
```

**Latency injection.** Toxiproxy adds a fixed `latency` toxic (no jitter) on the downstream direction of each link:

- API to database: always 1 ms, for a database on another host in the same data center.
- Client to API: `NetworkLatencyMs` = 0 (loopback) or 20 ms (a nearby region). Every HTTP response pays it once.

**Execution strategy.** One shared `HttpClient` with the default handler: HTTP/1.1 with keep-alive, no response compression, and no limit on connections per server. SeparateParallel issues the member and visits requests together with `Task.WhenAll`, then all club requests together, so it waits for three latencies however many clubs there are. SeparateSequential awaits each request before sending the next, as a straightforward client would. The unlimited connection pool favors SeparateParallel: a browser opens at most six HTTP/1.1 connections per host, so 20 parallel club requests would take several waves there. Toxiproxy delays data, not TCP handshakes, and every case runs each client once before BenchmarkDotNet's pilot and warmup, so new connections are not part of the measured iterations.

**Job settings.** `InProcessEmitToolchain`, 3 warmup iterations, 15 measured iterations, `MemoryDiagnoser`, and a P95 column. Parameters: `RelatedLimit` in {1, 10, 50} and `NetworkLatencyMs` in {0, 20}. Before the first case the benchmark runs `VACUUM ANALYZE` on the seeded data and then all three clients for 20 seconds, so the API's JIT has tiered up, EF Core has compiled its queries, and both connection pools are filled.

**Metrics.**

- **Mean / P95**: end-to-end client time for one operation, from the first request to the last parsed response, including JSON parsing.
- **Requests / Round trips**: HTTP calls made, and the calls on the critical path (the network latencies the client waits for).
- **SQL queries**: `SELECT` statements PostgreSQL executed during one operation, read from `pg_stat_statements` after a reset. Npgsql's `DISCARD ALL` on pooled connections and the benchmark's own statistics queries are excluded.
- **Response bytes**: response body bytes only; HTTP headers are not counted.
- **Allocated**: managed allocations in the benchmark client only. The API runs in its own container, so its allocations are not included.

Requests, round trips, queries, and bytes are recorded once per case in `GlobalSetup` and are deterministic.

### Results

| Method             | RelatedLimit | NetworkLatencyMs | Mean       | Error      | StdDev     | P95        | Ratio | RatioSD | Requests | Round trips | SQL queries | Response bytes | Allocated | Alloc Ratio |
|------------------- |------------- |----------------- |-----------:|-----------:|-----------:|-----------:|------:|--------:|---------:|------------:|------------:|---------------:|----------:|------------:|
| **Expanded**           | **1**            | **0**                |   **3.416 ms** |  **0.4505 ms** |  **0.3994 ms** |   **4.012 ms** |  **1.01** |    **0.16** |        **1** |           **1** |           **1** |            **372** |   **4.74 KB** |        **1.00** |
| SeparateParallel   | 1            | 0                |   9.920 ms |  3.5628 ms |  3.3327 ms |  16.573 ms |  2.94 |    1.01 |        4 |           3 |           4 |            384 |  13.53 KB |        2.86 |
| SeparateSequential | 1            | 0                |  11.738 ms |  2.6990 ms |  2.2538 ms |  15.212 ms |  3.48 |    0.74 |        4 |           4 |           4 |            384 |  12.91 KB |        2.72 |
|                    |              |                  |            |            |            |            |       |         |          |             |             |                |           |             |
| **Expanded**           | **1**            | **20**               |  **34.529 ms** |  **7.1548 ms** |  **6.6926 ms** |  **41.893 ms** |  **1.04** |    **0.30** |        **1** |           **1** |           **1** |            **372** |   **4.89 KB** |        **1.00** |
| SeparateParallel   | 1            | 20               |  73.869 ms |  5.0099 ms |  4.4412 ms |  82.155 ms |  2.23 |    0.50 |        4 |           3 |           4 |            384 |  14.13 KB |        2.89 |
| SeparateSequential | 1            | 20               | 103.768 ms | 11.0654 ms | 10.3506 ms | 124.415 ms |  3.13 |    0.74 |        4 |           4 |           4 |            384 |  13.69 KB |        2.80 |
|                    |              |                  |            |            |            |            |       |         |          |             |             |                |           |             |
| **Expanded**           | **10**           | **0**                |   **3.677 ms** |  **0.6141 ms** |  **0.5745 ms** |   **4.628 ms** |  **1.02** |    **0.21** |        **1** |           **1** |           **1** |          **1,417** |   **9.63 KB** |        **1.00** |
| SeparateParallel   | 10           | 0                |  14.456 ms |  3.7227 ms |  3.1086 ms |  18.566 ms |  4.02 |    1.01 |       13 |           3 |          13 |          1,465 |  42.82 KB |        4.45 |
| SeparateSequential | 10           | 0                |  40.012 ms | 12.1235 ms | 11.3404 ms |  58.473 ms | 11.11 |    3.46 |       13 |          13 |          13 |          1,465 |  41.59 KB |        4.32 |
|                    |              |                  |            |            |            |            |       |         |          |             |             |                |           |             |
| **Expanded**           | **10**           | **20**               |  **24.217 ms** |  **0.2480 ms** |  **0.2198 ms** |  **24.526 ms** |  **1.00** |    **0.01** |        **1** |           **1** |           **1** |          **1,417** |   **9.77 KB** |        **1.00** |
| SeparateParallel   | 10           | 20               |  76.694 ms |  2.7107 ms |  2.4030 ms |  81.077 ms |  3.17 |    0.10 |       13 |           3 |          13 |          1,465 |  43.48 KB |        4.45 |
| SeparateSequential | 10           | 20               | 306.038 ms |  6.0062 ms |  5.0155 ms | 314.742 ms | 12.64 |    0.23 |       13 |          13 |          13 |          1,465 |  44.07 KB |        4.51 |
|                    |              |                  |            |            |            |            |       |         |          |             |             |                |           |             |
| **Expanded**           | **50**           | **0**                |   **3.783 ms** |  **0.7190 ms** |  **0.6726 ms** |   **5.003 ms** |  **1.03** |    **0.24** |        **1** |           **1** |           **1** |          **6,003** |  **31.05 KB** |        **1.00** |
| SeparateParallel   | 50           | 0                |  15.717 ms |  2.5488 ms |  2.3841 ms |  19.288 ms |  4.27 |    0.91 |       23 |           3 |          23 |          4,941 |  85.64 KB |        2.76 |
| SeparateSequential | 50           | 0                |  60.530 ms |  9.5786 ms |  8.4912 ms |  78.086 ms | 16.43 |    3.39 |       23 |          23 |          23 |          4,941 |  84.31 KB |        2.72 |
|                    |              |                  |            |            |            |            |       |         |          |             |             |                |           |             |
| **Expanded**           | **50**           | **20**               |  **24.782 ms** |  **0.4932 ms** |  **0.3851 ms** |  **25.369 ms** |  **1.00** |    **0.02** |        **1** |           **1** |           **1** |          **6,003** |  **31.16 KB** |        **1.00** |
| SeparateParallel   | 50           | 20               |  81.919 ms |  5.8782 ms |  5.4985 ms |  91.152 ms |  3.31 |    0.22 |       23 |           3 |          23 |          4,941 |  86.46 KB |        2.77 |
| SeparateSequential | 50           | 20               | 556.050 ms | 19.1612 ms | 17.9234 ms | 587.250 ms | 22.44 |    0.78 |       23 |          23 |          23 |          4,941 |   88.7 KB |        2.85 |

**End-to-end latency.** With 20 ms of client latency, the expanded request takes 24–25 ms for the 10- and 50-visit previews: one network latency plus a few milliseconds of server and database work. The 1-visit case measured 34.5 ms because of a noisy stretch on the host (see Noise); its first iterations took 24 ms like the others. SeparateParallel takes 74–82 ms, which is about three latencies plus server work (2.2–3.3× the expanded time). SeparateSequential grows with the number of requests: 104 ms for 4 requests, 306 ms for 13, and 556 ms for 23, which is 24–26 ms per request (3.1× to 22×).

At 0 ms, on loopback, the gaps are smaller in absolute terms but still clear: 3.4–3.8 ms expanded against 10–16 ms parallel and 12–61 ms sequential. Each separate request still costs an HTTP exchange, an EF Core query, and a 1 ms database round trip.

**Response size.** Body bytes are nearly equal for small previews: 372 against 384 bytes for one visit, and 1,417 against 1,465 for ten. With 50 visits the expanded body is larger, 6,003 against 4,941 bytes (+21%), because it repeats club objects (see the next section).

**Database queries.** The expanded request always runs exactly 1 SQL query. The separate clients run one query per request: 4, 13, and 23. Parallelism reduces waiting, not work: SeparateParallel executes the same number of queries as SeparateSequential.

**Client allocations.** The expanded client allocates 5–31 KB per operation; the separate clients allocate 2.7–4.5× more, in line with making 4–23 requests instead of one.

**Noise.** This ran on a shared laptop with Docker Desktop, so treat single-digit millisecond differences with care:

- The benchmark warms the environment for 20 seconds before the first case. In the previous run, without that warm-up, the first case (Expanded, `RelatedLimit` 1, 0 ms) measured 5.7 ± 2.4 ms; here it measured 3.4 ± 0.4 ms, in line with the other expanded loopback cases.
- Expanded, `RelatedLimit` 1, 20 ms is 34.5 ± 6.7 ms: its first three iterations took 24 ms, then about ten iterations took 31–42 ms before it came back to 27 ms. BenchmarkDotNet flagged it as possibly multimodal (mValue = 2.89). The other two 20 ms expanded cases measured 24.2 ± 0.2 and 24.8 ± 0.4 ms, so its ratios (2.2× and 3.1×) understate the gap.
- The 0 ms cases have high relative spread for the separate clients (for example 40.0 ± 11.3 ms for SeparateSequential at 10 visits), and BenchmarkDotNet removed 1–3 outliers in several cases.

The other 20 ms cases are steadier (StdDev 1–10% of the mean), and the gaps between approaches there are far larger than the spread, so they carry the main conclusion.

### Larger related collections

Raising `RelatedLimit` from 1 to 10 to 50 raises the number of distinct clubs from 1 to 10 to 20.

| RelatedLimit | Distinct clubs | Expanded: requests / queries / bytes | Separate: requests / queries / bytes | Expanded vs. separate bytes |
|---|---|---|---|---|
| 1 | 1 | 1 / 1 / 372 | 4 / 4 / 384 | −3% |
| 10 | 10 | 1 / 1 / 1,417 | 13 / 13 / 1,465 | −3% |
| 50 | 20 | 1 / 1 / 6,003 | 23 / 23 / 4,941 | +21% |

- **Expanded** stays at 1 request and 1 query. Its time at 20 ms client latency is flat apart from the noisy 1-visit case (34.5, 24.2, 24.8 ms), and at 0 ms it rises only from 3.4 to 3.8 ms between 1 and 50 visits.
- **SeparateParallel** stays at 3 round trips, but requests and queries grow with distinct clubs (4 → 13 → 23). Its time rises modestly (74 → 77 → 82 ms at 20 ms; 10 → 14 → 16 ms at 0 ms), because more parallel requests and queries compete for connections and server time.
- **SeparateSequential** grows linearly with requests, because every club is another full round trip: 104 → 306 → 556 ms at 20 ms.
- **Payload.** From 1 to 50 visits the expanded body grows 16× (372 → 6,003 bytes), while the separate bodies grow 13× (384 → 4,941 bytes). Up to 10 visits every visit has a different club, so the expanded response contains each club once and saves the separate responses' overhead. Beyond 20 visits, clubs repeat: the 50-visit expanded response embeds 50 club objects where the separate client downloads 20, which is why it is 1,062 bytes (21%) larger. The more a related object is shared across a collection, the faster the expanded payload grows.

### When expansion helps and when it adds overhead

**Round trips × latency dominates.** Server and database work for these requests is a few milliseconds; each network latency is 20 ms. Expanded pays one latency, SeparateParallel three, SeparateSequential one per request. That arithmetic explains nearly the whole 20 ms table: about 25 ms, 74–82 ms, and 24–26 ms per sequential request. Any client that sits more than a few milliseconds away from the API (mobile apps, browsers, services in another region) benefits most from collapsing dependent requests into one.

**Parallelism helps but is capped by dependency depth.** Parallel requests cut the 50-visit case from 556 ms to 82 ms, but they cannot go below the depth of the dependency chain: membership → visits → clubs is three levels, so it stays around 3× the expanded time at 20 ms. It also does not reduce work: the server still handled 23 requests and 23 queries, where the expanded request needed 1.

**Where expansion adds overhead.**

- **Duplicate nested objects.** When many items share a related object, the expanded response repeats it. Here that made the 50-visit response 21% larger than the separate responses combined. With bigger related objects or more repetition the gap widens; a response that lists related ids and a de-duplicated `included` section (as in JSON:API) avoids it.
- **Wider SQL rows.** One query with joins returns the membership and member columns on every visit row, plus club columns on each row. This benchmark did not measure database time separately, and at these sizes the single query was still fastest, but with large parents or many joined collections the repeated columns and row multiplication grow. EF Core's split queries trade that for extra database round trips.
- **Over-fetching.** If a client already has the member or clubs, or caches them, expansion re-downloads and re-serializes data it did not need. Separate requests let it fetch only the missing parts, and with 20 cached clubs SeparateParallel would drop to two round trips.
- **HTTP caching.** `/api/clubs/{id}` is a stable resource that browsers, CDNs, and reverse proxies can cache with `ETag` or `Cache-Control`. An expanded response mixes volatile data (visits) with stable data (clubs), so it is much harder to cache and is invalidated whenever any part changes.
- **One slow expansion delays everything.** The expanded response is ready only when its slowest part is. With separate requests a client can render the membership while slower data is still loading.

**Practical guidance.**

- Offer expansion for relationships clients almost always load together, especially when the chain is more than one level deep or the client is far from the API. This is where the 3× to 22× differences above come from.
- Keep collections bounded (`relatedLimit`, a maximum depth, an allowlist) so a single request cannot turn into an unbounded join.
- Keep the standalone endpoints too. They serve clients that already have or cache related data, and they are the cacheable path for stable resources.
- Measure with realistic latency. On loopback the differences look small in absolute terms; with 20 ms per round trip they dominate.
