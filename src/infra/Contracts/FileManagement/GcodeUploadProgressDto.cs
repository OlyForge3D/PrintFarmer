using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.FileManagement;

/// <summary>
/// Represents progress information for an ongoing or completed GCode multi-file upload operation.
/// Emitted via SignalR to the upload modal for real-time progress feedback.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Array properties are acceptable for DTO serialization")]
public record GcodeUploadProgressDto(
    [property: JsonPropertyName("sessionId")]
    string SessionId,

    [property: JsonPropertyName("totalFiles")]
    int TotalFiles,

    [property: JsonPropertyName("processedCount")]
    int ProcessedCount,

    [property: JsonPropertyName("currentFileName")]
    string? CurrentFileName,

    [property: JsonPropertyName("successfulFiles")]
    List<string>? SuccessfulFiles,

    [property: JsonPropertyName("failedFiles")]
    List<GcodeUploadFailureSummary>? FailedFiles,

    [property: JsonPropertyName("errorMessage")]
    string? ErrorMessage);

/// <summary>
/// Represents a single file that failed during upload.
/// </summary>
public record GcodeUploadFailureSummary(
    [property: JsonPropertyName("fileName")]
    string FileName,

    [property: JsonPropertyName("error")]
    string Error);
