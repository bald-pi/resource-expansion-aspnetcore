using System.Text.Json;

namespace ResourceExpansion.Benchmarks;

// Requests: HTTP calls made. RoundTrips: calls on the critical path, i.e. how many network
// latencies the client waits for. Bytes: response bodies received.
public readonly record struct ScenarioResult(int Requests, int RoundTrips, long Bytes);

// Three clients that end up with the same data: the membership, its member, and the latest
// visits with their clubs. Every client reads and parses every response body.
public static class Scenarios
{
    private const int Id = BenchmarkApp.MembershipId;

    // One request; the server loads everything in one SQL query.
    public static async Task<ScenarioResult> ExpandedAsync(HttpClient client, int limit)
    {
        var membership = await GetAsync(client, $"/api/memberships/{Id}?expand=member,visits.club&relatedLimit={limit}");
        return new ScenarioResult(Requests: 1, RoundTrips: 1, membership.Bytes);
    }

    // Membership first (it carries memberId), then member and visits together, then every
    // distinct club together: three round trips however many clubs there are.
    public static async Task<ScenarioResult> SeparateParallelAsync(HttpClient client, int limit)
    {
        var membership = await GetAsync(client, $"/api/memberships/{Id}");
        var member = GetAsync(client, $"/api/members/{membership.Json.GetProperty("memberId").GetInt32()}");
        var visits = GetAsync(client, $"/api/memberships/{Id}/visits?limit={limit}");
        await Task.WhenAll(member, visits);

        var clubs = await Task.WhenAll(ClubIds(visits.Result.Json).Select(clubId => GetAsync(client, $"/api/clubs/{clubId}")));
        return new ScenarioResult(
            Requests: 3 + clubs.Length,
            RoundTrips: clubs.Length == 0 ? 2 : 3,
            membership.Bytes + member.Result.Bytes + visits.Result.Bytes + clubs.Sum(club => club.Bytes));
    }

    // The same requests one after another, as a straightforward client would write them.
    public static async Task<ScenarioResult> SeparateSequentialAsync(HttpClient client, int limit)
    {
        var membership = await GetAsync(client, $"/api/memberships/{Id}");
        var member = await GetAsync(client, $"/api/members/{membership.Json.GetProperty("memberId").GetInt32()}");
        var visits = await GetAsync(client, $"/api/memberships/{Id}/visits?limit={limit}");

        var bytes = membership.Bytes + member.Bytes + visits.Bytes;
        var requests = 3;
        foreach (var clubId in ClubIds(visits.Json))
        {
            bytes += (await GetAsync(client, $"/api/clubs/{clubId}")).Bytes;
            requests++;
        }

        return new ScenarioResult(requests, RoundTrips: requests, bytes);
    }

    private static int[] ClubIds(JsonElement visits) => visits.GetProperty("data").EnumerateArray()
        .Select(visit => visit.GetProperty("clubId").GetInt32())
        .Distinct()
        .ToArray();

    private static async Task<(long Bytes, JsonElement Json)> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsByteArrayAsync();
        using var document = JsonDocument.Parse(body);
        return (body.Length, document.RootElement.Clone());
    }
}
