using Farm.Infrastructure.Services.OperatorFeatures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Infrastructure.OperatorFeatures;

/// <summary>
/// Reusable ProblemDetails factory for the "feature disabled" 404 response required by #725.
///
/// Callers should return the produced <see cref="ProblemDetails"/> as a
/// <see cref="NotFoundObjectResult"/> (status 404). The <c>code</c> extension is the stable
/// machine identifier that clients use to render the disabled-feature affordance; the
/// <c>feature</c> extension carries the canonical camelCase flag name for diagnostics.
///
/// Endpoints that are gated must not perform any writes or emit any SignalR broadcasts before
/// returning this response.
/// </summary>
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
    public static ProblemDetails Create(string flagName, string? detail = null)
    {
        var problem = new ProblemDetails
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
    /// Convenience: build the ProblemDetails for a strongly-typed operator feature using the
    /// canonical flag name from the supplied gate.
    /// </summary>
    public static ProblemDetails Create(IOperatorFeatureGate gate, OperatorFeature feature, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return Create(gate.GetFlagName(feature), detail);
    }

    /// <summary>
    /// Convenience: build a <see cref="NotFoundObjectResult"/> wrapping the ProblemDetails
    /// payload for a disabled operator feature. Use directly from controller actions:
    /// <c>return OperatorFeatureProblemDetails.NotFound(gate, OperatorFeature.Attention);</c>
    /// </summary>
    public static NotFoundObjectResult NotFound(IOperatorFeatureGate gate, OperatorFeature feature, string? detail = null)
        => new(Create(gate, feature, detail));

    /// <summary>
    /// Convenience overload accepting a raw camelCase flag name (for cases where callers
    /// operate without the enum, e.g. dynamic dispatch tests).
    /// </summary>
    public static NotFoundObjectResult NotFound(string flagName, string? detail = null)
        => new(Create(flagName, detail));
}
