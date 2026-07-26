using Microsoft.AspNetCore.Mvc.Filters;

namespace Farm.Slicer.Module.Api.Filters;

/// <summary>
/// Marks a slicing route as superseded by the canonical <c>/api/slice</c> contract.
/// </summary>
/// <remarks>
/// The route keeps working so existing non-calibration callers are not broken. It advertises its
/// replacement through RFC 8594 <c>Deprecation</c>/<c>Sunset</c> headers plus a successor
/// <c>Link</c>, which is how clients discover the migration before the route is removed.
/// </remarks>
/// <param name="successorRoute">The canonical route that replaces this one.</param>
/// <param name="sunsetUtc">
/// RFC 1123 UTC date after which the route may be removed. Supplied as a constant so the attribute
/// remains usable in an attribute argument position.
/// </param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class DeprecatedSliceRouteAttribute(string successorRoute, string sunsetUtc)
    : ActionFilterAttribute
{
    /// <summary>The canonical route callers should migrate to.</summary>
    public string SuccessorRoute { get; } = successorRoute;

    /// <summary>The advertised sunset date in RFC 1123 form.</summary>
    public string SunsetUtc { get; } = sunsetUtc;

    /// <inheritdoc/>
    public override void OnResultExecuting(ResultExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IHeaderDictionary headers = context.HttpContext.Response.Headers;
        headers["Deprecation"] = "true";
        headers["Sunset"] = SunsetUtc;
        headers["Link"] = $"<{SuccessorRoute}>; rel=\"successor-version\"";

        base.OnResultExecuting(context);
    }
}
