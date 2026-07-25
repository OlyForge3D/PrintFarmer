using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Slicer.Module.Api.Authorization;

/// <summary>
/// Produces stable authentication errors when the standalone slicer host enforces authorization.
/// </summary>
public sealed class SlicerAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (!authorizeResult.Challenged && !authorizeResult.Forbidden)
        {
            await _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        int status = authorizeResult.Challenged
            ? StatusCodes.Status401Unauthorized
            : StatusCodes.Status403Forbidden;
        string code = authorizeResult.Challenged
            ? "authentication_required"
            : "permission_denied";
        var problem = new ProblemDetails
        {
            Status = status,
            Title = authorizeResult.Challenged ? "Authentication required" : "Permission denied",
            Type = $"https://printfarmer.dev/problems/{code}",
            Instance = context.Request.Path,
        };
        problem.Extensions["code"] = code;

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(problem, context.RequestAborted);
    }
}
