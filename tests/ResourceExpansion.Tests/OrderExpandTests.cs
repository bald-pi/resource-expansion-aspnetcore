using FluentValidation.TestHelper;
using ResourceExpansion.Api.Features.Orders.GetOrder;

namespace ResourceExpansion.Tests;

public sealed class OrderExpandTests
{
    private readonly GetOrderQueryValidator validator = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Missing_or_blank_expand_uses_baseline(string? expand)
    {
        var query = new GetOrderQuery(expand, null);

        validator.TestValidate(query).ShouldNotHaveAnyValidationErrors();
        Assert.Equal(new OrderExpand(false, false, false, OrderExpand.DefaultRelatedLimit), OrderExpand.From(query));
    }

    [Fact]
    public void Normalizes_case_whitespace_and_duplicates()
    {
        var query = new GetOrderQuery(" Customer ,ITEMS,customer", 50);

        validator.TestValidate(query).ShouldNotHaveAnyValidationErrors();
        Assert.Equal(new OrderExpand(true, true, false, 50), OrderExpand.From(query));
    }

    [Fact]
    public void Nested_path_implies_parent()
    {
        var plan = OrderExpand.From(new GetOrderQuery("items.product", null));
        Assert.Equal(new OrderExpand(false, true, true, OrderExpand.DefaultRelatedLimit), plan);
    }

    [Theory]
    [InlineData("unknown", "Unsupported expansion: unknown. Supported: customer, items, items.product.")]
    [InlineData("customer.orders", "Unsupported expansion: customer.orders. Supported: customer, items, items.product.")]
    [InlineData("items.product.supplier", "Expansion depth cannot exceed 2: items.product.supplier.")]
    public void Rejects_paths_outside_the_allowlist(string expand, string message)
    {
        validator.TestValidate(new GetOrderQuery(expand, null))
            .ShouldHaveValidationErrorFor("expand")
            .WithErrorMessage(message)
            .Only();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Rejects_out_of_range_related_limit(int limit)
    {
        validator.TestValidate(new GetOrderQuery(null, limit))
            .ShouldHaveValidationErrorFor("relatedLimit")
            .WithErrorMessage("relatedLimit must be between 1 and 50.")
            .Only();
    }

    [Fact]
    public void Reports_errors_for_both_parameters()
    {
        var result = validator.TestValidate(new GetOrderQuery("unknown", 0));

        result.ShouldHaveValidationErrorFor("expand");
        result.ShouldHaveValidationErrorFor("relatedLimit");
    }
}
