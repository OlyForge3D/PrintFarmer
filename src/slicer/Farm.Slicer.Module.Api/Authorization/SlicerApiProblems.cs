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

    /// <summary>
    /// The presented lease no longer matches the persisted claim (expired, replaced or re-fenced).
    /// </summary>
    /// <param name="controller">The responding controller.</param>
    /// <param name="code">A stable machine-readable reason such as <c>lease_expired</c>.</param>
    /// <returns>A <c>409</c> problem-details result.</returns>
    public static ObjectResult LeaseConflict(ControllerBase controller, string code) =>
        Create(
            controller.HttpContext,
            StatusCodes.Status409Conflict,
            "Worker lease conflict",
            code);

    /// <summary>
    /// A registration attempted to claim an <c>InstanceId</c> whose existing worker is not
    /// Offline (issue #1860): the shared registration key does not prove possession of that
    /// worker's identity, so the claim is rejected rather than silently overwriting credentials.
    /// </summary>
    /// <param name="controller">The responding controller.</param>
    /// <param name="code">A stable machine-readable reason such as <c>instance_id_worker_online</c>.</param>
    /// <returns>A <c>409</c> problem-details result.</returns>
    public static ObjectResult InstanceIdConflict(ControllerBase controller, string code) =>
        Create(
            controller.HttpContext,
            StatusCodes.Status409Conflict,
            "Worker instance conflict",
            code);

    /// <summary>
    /// The request body carried a value the canonical contract refuses to coerce.
    /// </summary>
    /// <param name="controller">The responding controller.</param>
    /// <param name="code">A stable machine-readable reason such as <c>invalid_slicer_engine</c>.</param>
    /// <param name="detail">Non-sensitive explanation safe to return to the caller.</param>
    /// <returns>A <c>400</c> problem-details result.</returns>
    public static ObjectResult InvalidRequest(ControllerBase controller, string code, string detail)
    {
        ObjectResult result = Create(
            controller.HttpContext,
            StatusCodes.Status400BadRequest,
            "Invalid request",
            code);
        if (result.Value is ProblemDetails problem)
        {
            problem.Detail = detail;
        }

        return result;
    }

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
