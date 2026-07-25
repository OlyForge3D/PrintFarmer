using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Infrastructure.Authorization;

/// <summary>
/// Produces the stable authentication and permission error contract for endpoint authorization.
/// </summary>
public sealed class AuthorizationProblemDetailsResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Challenged)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "Authentication required",
                "authentication_required");
            return;
        }

        if (authorizeResult.Forbidden)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status403Forbidden,
                "Permission denied",
                "permission_denied");
            return;
        }

        await _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }

    private static async Task WriteProblemAsync(
        HttpContext context,
        int status,
        string title,
        string code)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
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
