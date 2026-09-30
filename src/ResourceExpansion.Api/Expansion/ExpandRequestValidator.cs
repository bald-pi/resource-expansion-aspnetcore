using FluentValidation;

namespace ResourceExpansion.Api.Expansion;

public sealed class ExpandRequestValidator<TRequest> : AbstractValidator<TRequest>
    where TRequest : ExpandableRequest
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
