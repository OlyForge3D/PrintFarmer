using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Farm.Slicer.Module.Api.Filters;

/// <summary>
/// Requires a specific permission for the authenticated user.
/// The host application must register an <see cref="IPermissionValidator"/>
/// to provide actual authorization logic.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequirePermissionAttribute(string permission) : Attribute, IAsyncActionFilter
{
    /// <summary>Gets the required permission name.</summary>
    public string Permission { get; } = permission;

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var validator = context.HttpContext.RequestServices.GetService<IPermissionValidator>();
        if (validator is null)
        {
            // No validator registered — pass through
            await next();
            return;
        }

        if (!await validator.HasPermissionAsync(context.HttpContext, Permission, context.HttpContext.RequestAborted))
        {
            context.Result = new ForbidResult();
            return;
        }

        await next();
    }
}

/// <summary>
/// Abstraction for validating user permissions.
/// The host application provides the implementation.
/// </summary>
public interface IPermissionValidator
{
    /// <summary>Check whether the current user has the specified permission.</summary>
    Task<bool> HasPermissionAsync(HttpContext httpContext, string permission, CancellationToken ct = default);
}
