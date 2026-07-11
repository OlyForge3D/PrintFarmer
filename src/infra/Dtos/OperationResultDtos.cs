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
/// Discriminates why a filament-unload failed so the HTTP layer maps to the correct status
/// code WITHOUT brittle message substring matching (GitHub issue OlyForge3D/PrintFarmer#710
/// low-severity fix): a missing printer is 404, an invalid toolhead index is 400.
/// </summary>
public enum FilamentUnloadFailureKind
{
    /// <summary>No failure, or a generic backend failure that maps to 400.</summary>
    None = 0,

    /// <summary>The printer id did not resolve. Maps to HTTP 404.</summary>
    PrinterNotFound = 1,

    /// <summary>The requested toolhead / lane index is invalid. Maps to HTTP 400.</summary>
    InvalidToolhead = 2,
}

/// <summary>
/// Result of a filament-unload command. Extends the generic <see cref="CommandResult"/> shape
/// with the residual weight of the spool that was just unloaded so the guided swap flow
/// (and mobile "return to shelf" workflow) can record inventory without an extra Spoolman
/// round-trip on the client.
/// </summary>
/// <param name="Success">Whether the unload command completed successfully.</param>
/// <param name="Message">Optional descriptive message.</param>
/// <param name="SpoolId">Spoolman spool ID that was loaded on the primary toolhead prior to unload, if any.</param>
/// <param name="Material">Material family (e.g., "PLA") captured from the outgoing spool, if any.</param>
/// <param name="ResidualWeightG">Remaining filament weight in grams reported by Spoolman for the outgoing spool.</param>
/// <param name="FailureKind">
/// Non-serialized failure discriminator used by the controller to select 404 vs 400 without
/// substring matching. Not part of the wire contract.
/// </param>
public record FilamentUnloadResult(
    bool Success,
    string? Message = null,
    int? SpoolId = null,
    string? Material = null,
    double? ResidualWeightG = null,
    [property: JsonIgnore] FilamentUnloadFailureKind FailureKind = FilamentUnloadFailureKind.None);

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
