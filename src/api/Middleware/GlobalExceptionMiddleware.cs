
using System.Net;
using System.Text.Json;
using Farm.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Middleware;

/// <summary>
/// Global exception handling middleware that provides consistent error responses
/// and structured logging for all unhandled exceptions
/// </summary>
public class GlobalExceptionMiddleware(RequestDelegate next)
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    public async Task InvokeAsync(HttpContext context, [FromServices] IUnifiedLoggingService logger)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await _next(context);
        }
        // CA1031: Intentionally catching all exceptions to prevent unhandled exceptions from crashing the application
        // This is the global exception handler that provides consistent error responses
        catch (Exception ex)
        {
            // Use correlationId from HttpContext.Items if available (set by TelemetryMiddleware)
            string correlationId = context.Items["CorrelationId"] as string ?? context.TraceIdentifier;
            logger.LogError(ex, $"Unhandled exception occurred for {context.Request.Method} {context.Request.Path}. CorrelationId: {correlationId}", correlationId);
            await HandleExceptionAsync(context, ex, correlationId);
        }
    }

    private static async Task HandleExceptionAsync(HttpContext context, Exception ex, string correlationId)
    {
        context.Response.ContentType = "application/json";

        (HttpStatusCode statusCode, string? message, string? details) = MapExceptionToResponse(ex);

        context.Response.StatusCode = (int)statusCode;

        var response = new
        {
            Error = message,
            Details = details,
            CorrelationId = correlationId,
            Timestamp = DateTime.UtcNow.ToString("O"),
            Path = context.Request.Path.ToString()
        };

        string jsonResponse = JsonSerializer.Serialize(response, s_jsonOptions);

        await context.Response.WriteAsync(jsonResponse);
    }

    private static (HttpStatusCode StatusCode, string Message, string? Details) MapExceptionToResponse(Exception ex)
    {
        var result = ex switch
        {
            // Domain-specific exceptions
            PrinterNotFoundException => (HttpStatusCode.NotFound, "Printer not found", ex.Message),
            SpoolmanServiceException => (HttpStatusCode.BadGateway, "External service error", ex.Message),

            // Authentication and authorization
            UnauthorizedAccessException => (HttpStatusCode.Unauthorized, "Unauthorized access", null),

            // Validation errors (more specific first)
            ArgumentNullException => (HttpStatusCode.BadRequest, "Missing required parameter", ex.Message),
            ArgumentException => (HttpStatusCode.BadRequest, "Invalid request", ex.Message),

            // Network and external service errors
            HttpRequestException => (HttpStatusCode.BadGateway, "External service unavailable", null),
            TaskCanceledException when ex.InnerException is TimeoutException =>
                (HttpStatusCode.RequestTimeout, "Request timeout", null),
            TimeoutException => (HttpStatusCode.RequestTimeout, "Request timeout", null),

            // Database errors
            InvalidOperationException when ex.Message.Contains("database", StringComparison.OrdinalIgnoreCase) =>
                (HttpStatusCode.ServiceUnavailable, "Database service unavailable", null),

            // Circuit breaker
            Farm.Infrastructure.CircuitBreakerOpenException =>
                (HttpStatusCode.ServiceUnavailable, "Service temporarily unavailable", ex.Message),

            // Default for all other exceptions - include full error chain for debugging
            _ => (HttpStatusCode.InternalServerError, "An unexpected error occurred",
                $"{ex.GetType().Name}: {ex.Message}" + (ex.InnerException != null ? $" -> {ex.InnerException.GetType().Name}: {ex.InnerException.Message}" : ""))
        };

        return result;
    }
}

/// <summary>
/// Custom exception for printer not found scenarios
/// </summary>
public class PrinterNotFoundException : Exception
{
    public PrinterNotFoundException(string message) : base(message) { }
    public PrinterNotFoundException(string message, Exception innerException) : base(message, innerException) { }
    public PrinterNotFoundException()
    {
    }
}

/// <summary>
/// Custom exception for Spoolman service errors
/// </summary>
public class SpoolmanServiceException : Exception
{
    public SpoolmanServiceException(string message) : base(message) { }
    public SpoolmanServiceException(string message, Exception innerException) : base(message, innerException) { }
    public SpoolmanServiceException()
    {
    }
}
