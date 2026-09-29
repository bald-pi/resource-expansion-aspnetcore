using System.Net;
using System.Text.Json;

namespace ResourceExpansion.Tests;

[Trait("Category", "Integration")]
public sealed class GetOrderTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Baseline_returns_null_relationships_without_joins()
    {
        fixture.Sql.Commands.Clear();
        var json = await GetOk("/api/orders/1001");

        Assert.Equal(JsonValueKind.Null, json.GetProperty("customer").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("items").ValueKind);
        Assert.DoesNotContain("JOIN", Assert.Single(fixture.Sql.Commands));
    }

    [Fact]
    public async Task Single_expansion_loads_customer_only()
    {
        var json = await GetOk("/api/orders/1001?expand=customer");

        Assert.Equal("Alice Morgan", json.GetProperty("customer").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("items").ValueKind);
    }

    [Fact]
    public async Task Items_without_products_are_bounded_by_related_limit()
    {
        var items = (await GetOk("/api/orders/1001?expand=items&relatedLimit=1")).GetProperty("items");

        Assert.Single(items.GetProperty("data").EnumerateArray());
        Assert.True(items.GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, items.GetProperty("data")[0].GetProperty("product").ValueKind);
    }

    [Fact]
    public async Task Nested_expansion_loads_items_with_products_in_one_query()
    {
        fixture.Sql.Commands.Clear();
        var json = await GetOk("/api/orders/1001?expand=customer,items.product&relatedLimit=3");
        var items = json.GetProperty("items");

        Assert.Equal(3, items.GetProperty("data").GetArrayLength());
        Assert.False(items.GetProperty("hasMore").GetBoolean());
        Assert.Equal("Mechanical keyboard", items.GetProperty("data")[0].GetProperty("product").GetProperty("name").GetString());
        Assert.Single(fixture.Sql.Commands);
    }

    [Fact]
    public async Task Expanded_empty_collection_is_distinct_from_unrequested()
    {
        var items = (await GetOk("/api/orders/1002?expand=items")).GetProperty("items");

        Assert.Empty(items.GetProperty("data").EnumerateArray());
        Assert.False(items.GetProperty("hasMore").GetBoolean());
    }

    [Theory]
    [InlineData("?expand=payments")]
    [InlineData("?expand=items.product.supplier")]
    [InlineData("?relatedLimit=51")]
    [InlineData("?relatedLimit=abc")]
    public async Task Invalid_request_returns_400_before_database_access(string query)
    {
        fixture.Sql.Commands.Clear();
        using var response = await fixture.Factory.CreateClient().GetAsync("/api/orders/1001" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fixture.Sql.Commands);
    }

    [Fact]
    public async Task Missing_order_returns_404()
    {
        using var response = await fixture.Factory.CreateClient().GetAsync("/api/orders/9999?expand=items.product");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<JsonElement> GetOk(string url)
    {
        using var response = await fixture.Factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
