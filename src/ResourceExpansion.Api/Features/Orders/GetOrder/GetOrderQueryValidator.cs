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
