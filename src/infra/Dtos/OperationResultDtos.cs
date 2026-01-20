using System.Text.Json.Serialization;

namespace Farm.Infrastructure;

// ===========================================================================
// Operation Result DTOs
// ===========================================================================
// Generic and specific result records for various operations.
// ===========================================================================

/// <summary>
/// Standard command result indicating success or failure with optional message.
/// Used as a generic response for simple operations.
/// </summary>
/// <param name="Success">Whether the operation completed successfully.</param>
/// <param name="Message">Optional message providing details about the result.</param>
public record CommandResult(bool Success, string? Message = null);

/// <summary>
/// Response for folder operations (create, move, delete).
/// </summary>
/// <param name="Success">Whether the folder operation completed successfully.</param>
/// <param name="Message">Description of the operation result.</param>
public record FolderOperationResultDto(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// Result of uploading a G-code file directly to a printer backend.
/// </summary>
/// <param name="Message">Status message from the upload operation.</param>
/// <param name="Filename">Name of the uploaded file on the printer.</param>
public record UploadGcodeResultDto(string Message, string Filename);

/// <summary>
/// Result of a start print command issued to a printer backend.
/// </summary>
/// <param name="Message">Status message from the start print command.</param>
/// <param name="Filename">Name of the file that was started.</param>
public record StartPrintResultDto(string Message, string Filename);

/// <summary>
/// Generic paged result wrapper for list endpoints.
/// </summary>
/// <typeparam name="T">Type of items in the result set.</typeparam>
/// <param name="Items">Collection of items for the current page.</param>
/// <param name="TotalCount">Total number of items across all pages.</param>
/// <param name="Page">Current page number (1-based).</param>
/// <param name="PageSize">Number of items per page.</param>
/// <param name="TotalPages">Total number of pages available.</param>
public record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages);
