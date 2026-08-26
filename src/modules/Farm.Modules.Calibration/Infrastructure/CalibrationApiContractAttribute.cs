using Microsoft.AspNetCore.Mvc.Filters;

namespace Farm.Web.Api.Infrastructure;

/// <summary>
/// Applies the additive Desktop API contract negotiation to calibration
/// persistence endpoints while preserving behavior for clients that omit it.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class CalibrationApiContractAttribute : Attribute, IResourceFilter
{
    /// <inheritdoc />
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ApiContractNegotiation.AddResponseHeaders(context.HttpContext.Response);
        context.Result = ApiContractNegotiation.Negotiate(context.HttpContext.Request);
    }

    /// <inheritdoc />
    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
