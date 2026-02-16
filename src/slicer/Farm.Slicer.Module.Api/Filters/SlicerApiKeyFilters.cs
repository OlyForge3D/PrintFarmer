using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Farm.Slicer.Module.Api.Filters;

/// <summary>
/// Requires a valid shared slicer API key for the request.
/// The host application must register an <see cref="ISlicerApiKeyValidator"/>
/// to provide actual validation logic.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireSlicerApiKeyAttribute : Attribute, IAsyncActionFilter
{
    /// <summary>The header name for the slicer API key.</summary>
    public const string HeaderName = "X-Slicer-Api-Key";

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var validator = context.HttpContext.RequestServices.GetService<ISlicerApiKeyValidator>();
        if (validator is null)
        {
            // No validator registered — pass through (development mode)
            await next();
            return;
        }

        string? apiKey = context.HttpContext.Request.Headers[HeaderName].FirstOrDefault();
        if (!await validator.ValidateSharedKeyAsync(apiKey, context.HttpContext.RequestAborted))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid or missing slicer API key." });
            return;
        }

        await next();
    }
}

/// <summary>
/// Requires a valid per-service slicer API key for the request.
/// The host application must register an <see cref="ISlicerApiKeyValidator"/>
/// to provide actual validation logic.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireSlicerServiceApiKeyAttribute : Attribute, IAsyncActionFilter
{
    /// <summary>The header name for the per-service API key.</summary>
    public const string HeaderName = "X-Slicer-Service-Api-Key";

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var validator = context.HttpContext.RequestServices.GetService<ISlicerApiKeyValidator>();
        if (validator is null)
        {
            await next();
            return;
        }

        string? apiKey = context.HttpContext.Request.Headers[HeaderName].FirstOrDefault();
        if (!await validator.ValidateServiceKeyAsync(apiKey, context.HttpContext.RequestAborted))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid or missing service API key." });
            return;
        }

        await next();
    }
}

/// <summary>
/// Abstraction for validating slicer API keys.
/// The host application provides the implementation.
/// </summary>
public interface ISlicerApiKeyValidator
{
    /// <summary>Validate a shared slicer API key.</summary>
    Task<bool> ValidateSharedKeyAsync(string? apiKey, CancellationToken ct = default);

    /// <summary>Validate a per-service slicer API key.</summary>
    Task<bool> ValidateServiceKeyAsync(string? apiKey, CancellationToken ct = default);
}
