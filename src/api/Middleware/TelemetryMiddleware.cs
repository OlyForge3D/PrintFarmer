using System.Diagnostics;
using Farm.Infrastructure.Telemetry;

namespace Farm.Web.Api.Middleware;

public class TelemetryMiddleware(RequestDelegate next, IPrintFarmerTelemetryService telemetryService)
{
    private readonly RequestDelegate _next = next;
    private readonly IPrintFarmerTelemetryService _telemetryService = telemetryService;

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip telemetry for health checks and static files to reduce noise
        string? path = context.Request.Path.Value?.ToLower();
        if (path != null && (path.StartsWith("/health") || path.StartsWith("/swagger") || path.StartsWith("/openapi") || path.Contains(".")))
        {
            await _next(context);
            return;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        string endpoint = context.Request.Path.Value ?? "unknown";
        string method = context.Request.Method;

        using Activity? activity = _telemetryService.StartActivity($"{method} {endpoint}", ActivityKind.Server);
        activity?.SetTag("http.method", method);
        activity?.SetTag("http.route", endpoint);
        activity?.SetTag("http.scheme", context.Request.Scheme);

        try
        {
            await _next(context);

            stopwatch.Stop();
            int statusCode = context.Response.StatusCode;
            activity?.SetTag("http.status_code", statusCode);

            _telemetryService.RecordApiCall(endpoint, method, statusCode, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            activity?.SetTag("http.status_code", 500);
            activity?.SetTag("error", true);
            activity?.SetTag("exception.type", ex.GetType().Name);
            activity?.SetTag("exception.message", ex.Message);

            _telemetryService.RecordApiCall(endpoint, method, 500, stopwatch.Elapsed);
            throw;
        }
    }
}

// Extension method for easier registration
public static class TelemetryMiddlewareExtensions
{
    public static IApplicationBuilder UseTelemetryMiddleware(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<TelemetryMiddleware>();
    }
}
