using System.Net;
using System.Text.Json;

namespace Farm.Web.Api.Middleware;

/// <summary>
/// Global exception handling middleware that provides consistent error responses
/// and structured logging for all unhandled exceptions
/// </summary>
public class GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var correlationId = context.TraceIdentifier;

            logger.LogError(ex, "Unhandled exception occurred for {Method} {Path}. CorrelationId: {CorrelationId}",
                context.Request.Method, context.Request.Path, correlationId);

            await HandleExceptionAsync(context, ex, correlationId);
        }
    }

    private static async Task HandleExceptionAsync(HttpContext context, Exception ex, string correlationId)
    {
        context.Response.ContentType = "application/json";

        var (statusCode, message, details) = MapExceptionToResponse(ex);

        context.Response.StatusCode = (int)statusCode;

        var response = new
        {
            Error = message,
            Details = details,
            CorrelationId = correlationId,
            Timestamp = DateTime.UtcNow.ToString("O"),
            Path = context.Request.Path.ToString()
        };

        var jsonResponse = JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });

        await context.Response.WriteAsync(jsonResponse);
    }

    private static (HttpStatusCode StatusCode, string Message, string? Details) MapExceptionToResponse(Exception ex)
    {
        return ex switch
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
            InvalidOperationException when ex.Message.Contains("database") =>
                (HttpStatusCode.ServiceUnavailable, "Database service unavailable", null),

            // Circuit breaker
            Farm.Web.Api.Infrastructure.CircuitBreakerOpenException =>
                (HttpStatusCode.ServiceUnavailable, "Service temporarily unavailable", ex.Message),

            // Default for all other exceptions
            _ => (HttpStatusCode.InternalServerError, "An unexpected error occurred", null)
        };
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
