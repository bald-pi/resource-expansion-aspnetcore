using System.Net;
using System.Text.Json;

namespace ResourceExpansion.Tests;

[Trait("Category", "Integration")]
public sealed class GetMembershipTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Baseline_returns_null_relationships_without_joins()
    {
        fixture.Sql.Commands.Clear();
        var json = await GetOk("/api/memberships/1001");

        Assert.Equal("Premium", json.GetProperty("plan").GetString());
        Assert.Equal("2026-12-31", json.GetProperty("endsOn").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("member").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("visits").ValueKind);
        Assert.DoesNotContain("JOIN", Assert.Single(fixture.Sql.Commands));
    }

    [Fact]
    public async Task Single_expansion_loads_member_only()
    {
        var json = await GetOk("/api/memberships/1001?expand=member");

        Assert.Equal("Alice Morgan", json.GetProperty("member").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("visits").ValueKind);
    }

    [Fact]
    public async Task Visits_without_clubs_are_most_recent_first_and_bounded_by_related_limit()
    {
        var visits = (await GetOk("/api/memberships/1001?expand=visits&relatedLimit=1")).GetProperty("visits");
        var visit = Assert.Single(visits.GetProperty("data").EnumerateArray());

        Assert.Equal(3, visit.GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Null, visit.GetProperty("club").ValueKind);
        Assert.True(visits.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task Nested_expansion_loads_member_and_visits_with_clubs_in_one_query()
    {
        fixture.Sql.Commands.Clear();
        var json = await GetOk("/api/memberships/1001?expand=member,visits.club&relatedLimit=3");
        var visits = json.GetProperty("visits");

        Assert.Equal("Alice Morgan", json.GetProperty("member").GetProperty("name").GetString());
        Assert.Equal(3, visits.GetProperty("data").GetArrayLength());
        Assert.False(visits.GetProperty("hasMore").GetBoolean());
        Assert.Equal("Downtown", visits.GetProperty("data")[0].GetProperty("club").GetProperty("name").GetString());
        Assert.Single(fixture.Sql.Commands);
    }

    [Fact]
    public async Task Parent_and_nested_path_together_load_visits_once()
    {
        fixture.Sql.Commands.Clear();
        var visits = (await GetOk("/api/memberships/1001?expand=visits,visits.club")).GetProperty("visits");

        Assert.Equal(2, visits.GetProperty("data").GetArrayLength());
        Assert.Equal("Downtown", visits.GetProperty("data")[0].GetProperty("club").GetProperty("name").GetString());
        Assert.Single(fixture.Sql.Commands);
    }

    [Fact]
    public async Task Expanded_empty_collection_is_distinct_from_unrequested()
    {
        var visits = (await GetOk("/api/memberships/1002?expand=visits")).GetProperty("visits");

        Assert.Empty(visits.GetProperty("data").EnumerateArray());
        Assert.False(visits.GetProperty("hasMore").GetBoolean());
    }

    [Theory]
    [InlineData("?expand=payments")]
    [InlineData("?expand=member.memberships")]
    [InlineData("?expand=visits.club.address")]
    [InlineData("?relatedLimit=51")]
    [InlineData("?relatedLimit=abc")]
    public async Task Invalid_request_returns_400_before_database_access(string query)
    {
        fixture.Sql.Commands.Clear();
        using var response = await fixture.Factory.CreateClient().GetAsync("/api/memberships/1001" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fixture.Sql.Commands);
    }

    [Fact]
    public async Task Missing_membership_returns_404()
    {
        using var response = await fixture.Factory.CreateClient().GetAsync("/api/memberships/9999?expand=member,visits.club");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Separate_requests_return_the_same_data_as_expansion()
    {
        var expanded = await GetOk("/api/memberships/1001?expand=member,visits.club&relatedLimit=2");

        var membership = await GetOk("/api/memberships/1001");
        var member = await GetOk($"/api/members/{membership.GetProperty("memberId").GetInt32()}");
        var visits = await GetOk("/api/memberships/1001/visits?limit=2");

        Assert.Equal(expanded.GetProperty("member").GetRawText(), member.GetRawText());
        Assert.Equal(expanded.GetProperty("visits").GetProperty("hasMore").GetBoolean(),
            visits.GetProperty("hasMore").GetBoolean());
        foreach (var (expandedVisit, visit) in expanded.GetProperty("visits").GetProperty("data").EnumerateArray()
                     .Zip(visits.GetProperty("data").EnumerateArray()))
        {
            Assert.Equal(expandedVisit.GetProperty("id").GetInt32(), visit.GetProperty("id").GetInt32());
            var club = await GetOk($"/api/clubs/{visit.GetProperty("clubId").GetInt32()}");
            Assert.Equal(expandedVisit.GetProperty("club").GetRawText(), club.GetRawText());
        }
    }

    [Theory]
    [InlineData("/api/members/9999")]
    [InlineData("/api/clubs/9999")]
    public async Task Missing_standalone_resource_returns_404(string url)
    {
        using var response = await fixture.Factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Visits_endpoint_rejects_out_of_range_limit()
    {
        using var response = await fixture.Factory.CreateClient().GetAsync("/api/memberships/1001/visits?limit=51");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<JsonElement> GetOk(string url)
    {
        using var response = await fixture.Factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
