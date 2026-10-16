using Farm.Infrastructure.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Farm.Modules.Devices.Filters;

/// <summary>
/// Converts unexpected failures of the camera config read into a generic, correlated
/// problem response. The config endpoint returns stored camera targets, so exception
/// type, message and inner-exception text must never reach the client. The exception is
/// still logged server-side. Client aborts stay unhandled so the global middleware keeps
/// treating them as cancellations rather than errors.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CameraConfigErrorFilterAttribute : ExceptionFilterAttribute
{
    public const string ProblemCode = "camera_config_read_failed";

    public override void OnException(ExceptionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        HttpContext httpContext = context.HttpContext;
        if (context.Exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        string correlationId = httpContext.Items["CorrelationId"] as string ?? httpContext.TraceIdentifier;
        ILogger logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger<CameraConfigErrorFilterAttribute>();
        logger.LogError(
            context.Exception,
            "Camera config read failed for {Method} {Path}. CorrelationId: {CorrelationId}",
            LogSanitizer.Sanitize(httpContext.Request.Method),
            LogSanitizer.Sanitize(httpContext.Request.Path),
            LogSanitizer.Sanitize(correlationId));

        ProblemDetails problem = new()
        {
            Type = $"https://printfarmer.dev/problems/{ProblemCode}",
            Title = "The camera configuration could not be read.",
            Status = StatusCodes.Status500InternalServerError,
        };
        problem.Extensions["code"] = ProblemCode;
        problem.Extensions["correlationId"] = correlationId;

        context.Result = new ObjectResult(problem) { StatusCode = StatusCodes.Status500InternalServerError };
        context.ExceptionHandled = true;
    }
}
