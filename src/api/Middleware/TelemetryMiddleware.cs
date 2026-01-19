using System.Diagnostics;
using Farm.Infrastructure.Telemetry;

namespace Farm.Web.Api.Middleware;

public class TelemetryMiddleware(RequestDelegate next, IPrintFarmerTelemetryService telemetryService)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly IPrintFarmerTelemetryService _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));

    public async Task InvokeAsync(HttpContext context)
    {
        // Extract correlationId from header (frontend sends X-Correlation-Id)
        string? correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = context.TraceIdentifier;
        }

        // Store in HttpContext.Items for downstream access
        context.Items["CorrelationId"] = correlationId;

        // Skip telemetry for health checks and static files to reduce noise.
        // Use PathString.StartsWithSegments with OrdinalIgnoreCase to avoid allocating a lower-cased string.
        string? pathValue = context.Request.Path.Value;
        if (pathValue != null && (
            context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Path.StartsWithSegments("/openapi", StringComparison.OrdinalIgnoreCase) ||
            pathValue.Contains('.')))
        {
            await _next(context);
            return;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        string endpoint = context.Request.Path.Value ?? "unknown";
        string method = context.Request.Method;

        using Activity? activity = _telemetryService.StartActivity($"{method} {endpoint}", ActivityKind.Server);
        _ = activity?.SetTag("http.method", method);
        _ = activity?.SetTag("http.route", endpoint);
        _ = activity?.SetTag("http.scheme", context.Request.Scheme);
        _ = activity?.SetTag("correlation.id", correlationId);

        try
        {
            await _next(context);

            stopwatch.Stop();
            int statusCode = context.Response.StatusCode;
            _ = activity?.SetTag("http.status_code", statusCode);

            _telemetryService.RecordApiCall(endpoint, method, statusCode, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _ = activity?.SetTag("http.status_code", 500);
            _ = activity?.SetTag("error", true);
            _ = activity?.SetTag("exception.type", ex.GetType().Name);
            _ = activity?.SetTag("exception.message", ex.Message);

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
