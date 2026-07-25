using Microsoft.AspNetCore.Mvc;

namespace Farm.Slicer.Module.Api.Authorization;

/// <summary>
/// Stable problem-details responses used by protected slicer resources.
/// </summary>
public static class SlicerApiProblems
{
    public static ObjectResult AuthenticationRequired(ControllerBase controller) =>
        AuthenticationRequired(controller.HttpContext);

    public static ObjectResult AuthenticationRequired(HttpContext context) =>
        Create(
            context,
            StatusCodes.Status401Unauthorized,
            "Authentication required",
            "authentication_required");

    public static ObjectResult AuthenticationUnavailable(HttpContext context) =>
        Create(
            context,
            StatusCodes.Status503ServiceUnavailable,
            "Authentication service unavailable",
            "authentication_unavailable");

    public static ObjectResult ResourceForbidden(ControllerBase controller) =>
        Create(
            controller.HttpContext,
            StatusCodes.Status403Forbidden,
            "Resource access denied",
            "resource_forbidden");

    public static ObjectResult ResourceNotFound(ControllerBase controller) =>
        Create(
            controller.HttpContext,
            StatusCodes.Status404NotFound,
            "Resource not found",
            "resource_not_found");

    private static ObjectResult Create(
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
        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }
}
