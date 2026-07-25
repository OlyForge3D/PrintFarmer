using Farm.Slicer.Module.Api.Authorization;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

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
    public const string HeaderName = "X-Slicer-Api-Key";

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var validator = context.HttpContext.RequestServices.GetService<ISlicerApiKeyValidator>();
        if (validator is null)
        {
            context.Result = SlicerApiProblems.AuthenticationUnavailable(context.HttpContext);
            return;
        }

        string? apiKey = context.HttpContext.Request.Headers[HeaderName].FirstOrDefault();
        if (!await validator.ValidateSharedKeyAsync(apiKey, context.HttpContext.RequestAborted))
        {
            context.Result = SlicerApiProblems.AuthenticationRequired(context.HttpContext);
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
    public const string HeaderName = "X-Slicer-Service-Api-Key";

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var validator = context.HttpContext.RequestServices.GetService<ISlicerApiKeyValidator>();
        if (validator is null)
        {
            context.Result = SlicerApiProblems.AuthenticationUnavailable(context.HttpContext);
            return;
        }

        string? apiKey = context.HttpContext.Request.Headers[HeaderName].FirstOrDefault();
        if (!context.RouteData.Values.TryGetValue("id", out object? routeId) ||
            !Guid.TryParse(routeId?.ToString(), out Guid serviceId) ||
            !await validator.ValidateServiceKeyAsync(
                serviceId,
                apiKey,
                context.HttpContext.RequestAborted))
        {
            context.Result = SlicerApiProblems.AuthenticationRequired(context.HttpContext);
            return;
        }

        await next();
    }
}

/// <summary>
/// Marks worker endpoints that require both the worker key and registry-issued service identity.
/// Authentication remains in <see cref="IWorkerAuthService"/> so actions can use the resolved worker.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WorkerApiKeySecurityAttribute : Attribute
{
}

/// <summary>
/// Abstraction for validating slicer API keys.
/// The host application provides the implementation.
/// </summary>
public interface ISlicerApiKeyValidator
{
    /// <summary>Validate a shared slicer API key.</summary>
    Task<bool> ValidateSharedKeyAsync(string? apiKey, CancellationToken ct = default);

    /// <summary>Validate a per-service slicer API key for the addressed service.</summary>
    Task<bool> ValidateServiceKeyAsync(
        Guid serviceId,
        string? apiKey,
        CancellationToken ct = default);
}
