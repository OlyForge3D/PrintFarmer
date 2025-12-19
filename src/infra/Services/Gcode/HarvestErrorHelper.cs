using System.Text.Json;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Gcode;

/// <summary>
/// Helper class for categorizing and enriching harvest operation errors
/// </summary>
public static class HarvestErrorHelper
{
    internal record ErrorDetails(
        string ExceptionType,
        string? StackTrace,
        string? InnerException,
        Dictionary<string, string>? AdditionalInfo
    );

    /// <summary>
    /// Categorize an exception into a harvest error type
    /// </summary>
    public static string CategorizeError(Exception ex, string? failedResource = null)
    {
        return ex switch
        {
            HttpRequestException => nameof(HarvestErrorType.ConnectionError),
            TimeoutException => nameof(HarvestErrorType.ConnectionError),
            TaskCanceledException => nameof(HarvestErrorType.ConnectionError),
            UnauthorizedAccessException => nameof(HarvestErrorType.AuthenticationError),
            IOException => nameof(HarvestErrorType.FileSystemError),
            ArgumentException => nameof(HarvestErrorType.ValidationError),
            _ when ex.Message.Contains("401", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("403", StringComparison.OrdinalIgnoreCase) => nameof(HarvestErrorType.AuthenticationError),
            _ when ex.Message.Contains("404", StringComparison.OrdinalIgnoreCase) => nameof(HarvestErrorType.FileSystemError),
            _ when ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase) => nameof(HarvestErrorType.ConnectionError),
            _ when ex.Message.Contains("connection", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("network", StringComparison.OrdinalIgnoreCase) => nameof(HarvestErrorType.ConnectionError),
            _ => nameof(HarvestErrorType.UnknownError)
        };
    }

    /// <summary>
    /// Determine if an error type is retryable
    /// </summary>
    public static bool IsRetryableError(string errorType)
    {
        return errorType switch
        {
            nameof(HarvestErrorType.ConnectionError) => true,
            nameof(HarvestErrorType.FileSystemError) => true,
            nameof(HarvestErrorType.AuthenticationError) => false, // User needs to fix API key
            nameof(HarvestErrorType.ValidationError) => false, // Data issue, not retryable
            _ => false
        };
    }

    /// <summary>
    /// Create detailed error JSON for logging/debugging
    /// </summary>
    private static readonly JsonSerializerOptions ErrorJsonOptions = new()
    {
        WriteIndented = false
    };

    public static string CreateErrorDetailsJson(Exception ex, string? failedResource = null)
    {
        ErrorDetails details = new(
            ExceptionType: ex.GetType().Name,
            StackTrace: ex.StackTrace,
            InnerException: ex.InnerException?.Message,
            AdditionalInfo: failedResource != null
                ? new Dictionary<string, string> { ["FailedResource"] = failedResource }
                : null
        );

        return JsonSerializer.Serialize(details, ErrorJsonOptions);
    }

    /// <summary>
    /// Get user-friendly error message based on error type
    /// </summary>
    public static string GetUserFriendlyMessage(string errorType, string originalMessage)
    {
        string prefix = errorType switch
        {
            nameof(HarvestErrorType.ConnectionError) => "Connection failed: ",
            nameof(HarvestErrorType.AuthenticationError) => "Authentication failed: ",
            nameof(HarvestErrorType.FileSystemError) => "File system error: ",
            nameof(HarvestErrorType.ValidationError) => "Validation failed: ",
            _ => "Error: "
        };

        return prefix + originalMessage;
    }

    /// <summary>
    /// Get helpful suggestion based on error type
    /// </summary>
    public static string? GetErrorSuggestion(string errorType)
    {
        return errorType switch
        {
            nameof(HarvestErrorType.ConnectionError) => "Verify printer is online and URL is correct. Check your network connection.",
            nameof(HarvestErrorType.AuthenticationError) => "Check your API key is valid and has the required permissions.",
            nameof(HarvestErrorType.FileSystemError) => "Ensure the printer has the requested files and folders accessible.",
            nameof(HarvestErrorType.ValidationError) => "Check the harvest operation settings and try again.",
            _ => null
        };
    }

    /// <summary>
    /// Update operation with error details
    /// </summary>
    public static void SetOperationError(
        GcodeHarvestOperation operation,
        Exception ex,
        string phase,
        string? failedResource = null)
    {
        operation.Status = GcodeHarvestStatus.Failed;
        operation.CompletedAt = DateTime.UtcNow;
        operation.ErrorOccurredAt = DateTime.UtcNow;
        operation.ErrorType = CategorizeError(ex, failedResource);
        operation.ErrorPhase = phase;
        operation.ErrorMessage = GetUserFriendlyMessage(operation.ErrorType, ex.Message);
        operation.ErrorDetails = CreateErrorDetailsJson(ex, failedResource);
        operation.FailedResource = failedResource;
        operation.IsRetryable = IsRetryableError(operation.ErrorType);
    }
}
