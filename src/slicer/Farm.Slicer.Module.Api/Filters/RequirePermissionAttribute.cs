using Farm.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Filters;

/// <summary>
/// Requires a specific permission for the authenticated user.
/// The host application must register an <see cref="IPermissionValidator"/>
/// to provide actual authorization logic.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequirePermissionAttribute(string permission) : Attribute, IAsyncActionFilter
{
    /// <summary>Gets the required permission name.</summary>
    public string Permission { get; } = permission;

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated != true)
        {
            context.Result = CreateProblem(
                context,
                StatusCodes.Status401Unauthorized,
                "Authentication required",
                "authentication_required");
            return;
        }

        var validator = context.HttpContext.RequestServices.GetService<IPermissionValidator>();
        if (validator is null)
        {
            context.Result = CreateProblem(
                context,
                StatusCodes.Status403Forbidden,
                "Permission denied",
                "permission_denied");
            return;
        }

        if (!await validator.HasPermissionAsync(context.HttpContext, Permission, context.HttpContext.RequestAborted))
        {
            context.Result = CreateProblem(
                context,
                StatusCodes.Status403Forbidden,
                "Permission denied",
                "permission_denied");
            return;
        }

        await next();
    }

    private static ObjectResult CreateProblem(
        ActionExecutingContext context,
        int status,
        string title,
        string code)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Type = $"https://printfarmer.dev/problems/{code}",
            Instance = context.HttpContext.Request.Path,
        };
        problem.Extensions["code"] = code;
        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
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

/// <summary>
/// Validates the stable permission claims carried by PrintFarmer JWTs.
/// </summary>
public sealed class ClaimsPermissionValidator(
    ILogger<ClaimsPermissionValidator> logger) : IPermissionValidator
{
    public Task<bool> HasPermissionAsync(
        HttpContext httpContext,
        string permission,
        CancellationToken ct = default)
    {
        _ = ct;
        if (PrintFarmerPermissions.IsFarmAdmin(httpContext.User))
        {
            PrintFarmerPermissions.TryGetUserId(httpContext.User, out Guid userId);
            logger.LogInformation(
                "Audited farm-admin permission bypass for user {UserId}: {Permission}",
                userId,
                permission);
            return Task.FromResult(true);
        }

        bool granted = httpContext.User.HasClaim(PrintFarmerPermissions.ClaimType, permission);
        logger.LogDebug(
            "Permission decision for {Permission}: {Granted}",
            permission,
            granted);
        return Task.FromResult(granted);
    }
}
