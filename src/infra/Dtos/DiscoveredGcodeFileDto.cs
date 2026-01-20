using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// A file discovered during a harvest operation prior to optional import.
/// </summary>
public record DiscoveredGcodeFileDto(
    Guid Id,
    Guid HarvestOperationId,
    string PrinterPath,
    string FileName,
    long FileSizeBytes,
    DateTime? ModifiedAt = null,
    string? FileHash = null,
    bool IsSelected = false,
    bool AlreadyInLibrary = false,
    Guid? ExistingLibraryFileId = null,
    bool ProcessingFailed = false,
    string? ErrorMessage = null,
    string? ThumbnailUrl = null,
    string? ExtractedSlicerName = null,
    string? ExtractedSlicerVersion = null,
    double? ExtractedPrintTime = null,
    double? ExtractedFilamentLength = null,
    double? ExtractedNozzleDiameter = null,
    string? ExtractedMaterial = null,
    string? ExtractedLayerHeight = null,
    string? ExtractedInfill = null,
    HarvestFileStatus? Status = null);
