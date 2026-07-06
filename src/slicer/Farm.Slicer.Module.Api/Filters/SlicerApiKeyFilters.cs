using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Filters;

/// <summary>
/// Requires a valid shared slicer API key for the request.
/// The host application must register an <see cref="ISlicerApiKeyValidator"/>
/// to provide actual validation logic.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireSlicerApiKeyAttribute : Attribute, IAsyncActionFilter
{
    /// <summary>The header name for the slicer API key.</summary>
    public const string HeaderName = "X-Slicer-ApiKey";

    /// <summary>Alternate dashed header name accepted for compatibility.</summary>
    public const string AlternateHeaderName = "X-Slicer-Api-Key";

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var validator = context.HttpContext.RequestServices.GetService<ISlicerApiKeyValidator>();
        if (validator is null)
        {
            if (SlicerApiKeyFilterHelpers.AllowMissingValidatorInDevelopment(context, nameof(RequireSlicerApiKeyAttribute)))
            {
                await next();
                return;
            }

            context.Result = new UnauthorizedObjectResult(new { error = "Slicer API key validation is not configured." });
            return;
        }

        string? apiKey = SlicerApiKeyFilterHelpers.ReadHeader(context, HeaderName, AlternateHeaderName);
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
public sealed class RequireSlicerServiceApiKeyAttribute : Attribute, IAsyncActionFilter
{
    /// <summary>The header name for the per-service API key.</summary>
    public const string HeaderName = "X-Slicer-ApiKey";

    /// <summary>Alternate service-specific header name accepted for compatibility.</summary>
    public const string AlternateHeaderName = "X-Slicer-Service-Api-Key";

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var validator = context.HttpContext.RequestServices.GetService<ISlicerApiKeyValidator>();
        if (validator is null)
        {
            if (SlicerApiKeyFilterHelpers.AllowMissingValidatorInDevelopment(context, nameof(RequireSlicerServiceApiKeyAttribute)))
            {
                await next();
                return;
            }

            context.Result = new UnauthorizedObjectResult(new { error = "Slicer service API key validation is not configured." });
            return;
        }

        string? apiKey = SlicerApiKeyFilterHelpers.ReadHeader(context, HeaderName, AlternateHeaderName, RequireSlicerApiKeyAttribute.AlternateHeaderName);
        Guid? serviceId = TryGetServiceId(context);
        if (!await validator.ValidateServiceKeyAsync(apiKey, serviceId, context.HttpContext.RequestAborted))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid or missing service API key." });
            return;
        }

        await next();
    }

    private static Guid? TryGetServiceId(ActionExecutingContext context)
    {
        if (context.RouteData.Values.TryGetValue("id", out object? routeId)
            && Guid.TryParse(routeId?.ToString(), out Guid serviceId))
        {
            return serviceId;
        }

        return null;
    }
}

internal static class SlicerApiKeyFilterHelpers
{
    public static string? ReadHeader(ActionExecutingContext context, params string[] headerNames)
    {
        foreach (string headerName in headerNames)
        {
            string? value = context.HttpContext.Request.Headers[headerName].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    public static bool AllowMissingValidatorInDevelopment(ActionExecutingContext context, string filterName)
    {
        IHostEnvironment? env = context.HttpContext.RequestServices.GetService<IHostEnvironment>();
        ILogger? logger = context.HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(filterName);

        if (env is not null && (env.IsDevelopment() || env.IsEnvironment("Testing")))
        {
            logger?.LogWarning("{FilterName} has no ISlicerApiKeyValidator registered; bypassing only because environment is {EnvironmentName}.", filterName, env.EnvironmentName);
            return true;
        }

        logger?.LogError("{FilterName} has no ISlicerApiKeyValidator registered; rejecting request fail-closed.", filterName);
        return false;
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
    Task<bool> ValidateServiceKeyAsync(string? apiKey, Guid? serviceId, CancellationToken ct = default);
}
