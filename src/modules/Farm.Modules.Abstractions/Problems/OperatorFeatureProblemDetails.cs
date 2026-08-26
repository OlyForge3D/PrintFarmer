using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Modules.Abstractions.Problems;

/// <summary>
/// Reusable ProblemDetails factory for a "feature disabled" 404 response, for modules that gate
/// an endpoint behind an operator-configurable feature flag.
/// </summary>
/// <remarks>
/// This is a self-contained sibling of
/// <c>Farm.Web.Api.Infrastructure.OperatorFeatures.OperatorFeatureProblemDetails</c> -- same
/// shape, but built only from a raw flag name so it has no dependency on
/// <c>Farm.Infrastructure</c>'s <c>IOperatorFeatureGate</c>/<c>OperatorFeature</c> types. The
/// existing monolith type is untouched by this phase; a future module that adopts this shared
/// shape can migrate off the monolith-specific overload without a behavior change.
/// </remarks>
public static class OperatorFeatureProblemDetails
{
    /// <summary>Stable machine-readable identifier placed under the <c>code</c> extension.</summary>
    public const string CodeExtension = "featureDisabled";

    /// <summary>Well-known type URI so tooling can dedupe/route the error.</summary>
    public const string TypeUri = "https://printfarmer.io/errors/feature-disabled";

    /// <summary>
    /// Builds the ProblemDetails payload for a disabled operator feature.
    /// </summary>
    /// <param name="flagName">Canonical camelCase flag name (e.g. <c>attentionEnabled</c>).</param>
    /// <param name="detail">Optional custom detail message. Defaults to a generic description.</param>
    public static Microsoft.AspNetCore.Mvc.ProblemDetails Create(string flagName, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flagName);

        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status404NotFound,
            Title = "Feature disabled",
            Detail = detail ?? $"The '{flagName}' operator feature is disabled by an administrator.",
            Type = TypeUri,
        };
        problem.Extensions["code"] = CodeExtension;
        problem.Extensions["feature"] = flagName;
        return problem;
    }

    /// <summary>
    /// Convenience: build a <see cref="NotFoundObjectResult"/> wrapping the ProblemDetails
    /// payload for a disabled operator feature.
    /// </summary>
    public static NotFoundObjectResult NotFound(string flagName, string? detail = null)
        => new(Create(flagName, detail));
}
