using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Primitives;

namespace Farm.Web.Api.Infrastructure.Filters;

/// <summary>
/// Action filter that enforces a simple API key header for slicer-related endpoints.
/// If environment variable SLICER_REGISTRATION_KEY is not set, this filter is a no-op.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireSlicerApiKeyAttribute : Attribute, IAsyncActionFilter
{
    private const string HeaderName = "X-Slicer-ApiKey";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        string? required = Environment.GetEnvironmentVariable("SLICER_REGISTRATION_KEY");
        if (string.IsNullOrEmpty(required))
        {
            // not configured - allow through
            await next();
            return;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out StringValues provided) || string.IsNullOrWhiteSpace(provided))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Missing X-Slicer-ApiKey header" });
            return;
        }

        if (!string.Equals(provided.ToString(), required, StringComparison.Ordinal))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid X-Slicer-ApiKey" });
            return;
        }

        await next();
    }
}
