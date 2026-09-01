using System.Diagnostics;
using Farm.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Routing;

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
        string endpoint = GetEndpointTag(context);
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
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client disconnected/cancelled the request (issue #2348). Don't record
            // this as a 500 in the API-call metric/SLO - it's not a server-side error.
            stopwatch.Stop();
            _ = activity?.SetTag("http.status_code", 499);
            _ = activity?.SetTag("client_aborted", true);

            _telemetryService.RecordApiCall(endpoint, method, 499, stopwatch.Elapsed);
            throw;
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

    /// <summary>
    /// Resolves the telemetry "endpoint" dimension from the matched route TEMPLATE
    /// (e.g. <c>/api/printers/{id}</c>) rather than the raw request path, so metric and
    /// span cardinality stays bounded to the number of route templates instead of growing
    /// per entity instance (printer/job/spool GUID, etc.) — see issue #2327.
    ///
    /// ASP.NET Core's minimal hosting model implicitly wraps the entire middleware
    /// pipeline in endpoint routing: route matching happens before any middleware added
    /// via <c>app.Use...</c> runs, regardless of where <c>MapControllers()</c> is called
    /// relative to this middleware. That means <c>HttpContext.GetEndpoint()</c>
    /// already reflects the matched route by the time this middleware executes, with no
    /// explicit UseRouting/UseEndpoints reordering required.
    /// </summary>
    private static string GetEndpointTag(HttpContext context)
    {
        if (context.GetEndpoint() is RouteEndpoint routeEndpoint)
        {
            string? routeTemplate = routeEndpoint.RoutePattern.RawText;
            if (!string.IsNullOrEmpty(routeTemplate))
            {
                // Controller attribute routes (e.g. [Route("api/printers")]) produce a
                // RawText with no leading slash, while minimal-API/hub route templates
                // are registered with one (e.g. "/hubs/printers"). Normalize so the
                // "endpoint" dimension has a consistent shape regardless of which
                // routing style produced the match.
                return routeTemplate.StartsWith('/') ? routeTemplate : $"/{routeTemplate}";
            }
        }

        // No endpoint matched (404) or the matched endpoint has no route pattern (e.g. a
        // fallback/non-route endpoint). Bucket all of these under a single value instead
        // of the per-instance raw path to keep cardinality bounded.
        return "unknown";
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
