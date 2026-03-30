using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure;

// G-code Library & Job Queue DTOs

/// <summary>
/// Origin of a G-code file stored in the library.
/// </summary>
public enum GcodeSourceDto
{
    Upload = 0,
    Harvested = 1,
    Generated = 2
}

/// <summary>
/// Represents a G-code file stored in the library (uploaded, harvested, or generated).
/// </summary>
public record GcodeFileDto(
    Guid Id,
    string FileName,
    long FileSize,
    DateTime UploadedAt,
    string? ThumbnailUrl = null,
    string? Name = null,  // Original filename uploaded by user (for display)
    GcodeSourceDto Source = GcodeSourceDto.Upload,
    Guid? SourcePrinterId = null,
    string? SourcePrinterName = null,
    string? OriginalPrinterPath = null,
    DateTime? LastSeenOnPrinter = null,
    string? Description = null,
    IEnumerable<TagDto>? Tags = null,
    double? RequiredNozzleDiameter = null,
    string? RequiredMaterial = null,
    double? EstimatedPrintTimeMinutes = null,
    double? EstimatedFilamentLengthMm = null,
    double? EstimatedFilamentWeightG = null,
    Guid? PrinterModelId = null,
    string? PrinterModelName = null,
    string? SlicerName = null,
    string? SlicerVersion = null,
    bool HasThumbnail = false,
    int? TotalLayers = null,
    double? FirstLayerHeight = null,
    bool? SupportEnabled = null,
    int? ToolChangesCount = null,
    double? ObjectDimensionX = null,
    double? ObjectDimensionY = null,
    double? ObjectDimensionZ = null,
    int? ObjectCount = null,
    double? RetractionLength = null,
    double? RetractionSpeed = null,
    int? TopSolidLayers = null,
    int? BottomSolidLayers = null,
    double? MaxVolumetricSpeed = null,
    bool? IroningEnabled = null,
    string? FilamentPerExtruderWeightG = null,
    string? FilamentPerExtruderLengthMm = null,
    int? ExtruderCount = null);

/// <summary>
/// Multipart metadata section for uploading a new G-code file.
/// </summary>
public class CreateGcodeFileDto
{
    public string FileName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string[]? Tags { get; set; }

    public double? RequiredNozzleDiameter { get; set; }

    public string? RequiredMaterial { get; set; }

    public double? EstimatedPrintTimeMinutes { get; set; }

    public double? EstimatedFilamentLengthMm { get; set; }

    public double? EstimatedFilamentWeightG { get; set; }

    public Guid? PrinterModelId { get; set; }

    public string? SlicerName { get; set; }

    public string? SlicerVersion { get; set; }
}

/// <summary>
/// Update payload for modifying G-code library metadata.
/// </summary>
public record UpdateGcodeFileDto(
    string FileName,
    string? Description = null,
    string[]? Tags = null,
    double? RequiredNozzleDiameter = null,
    string? RequiredMaterial = null,
    double? EstimatedPrintTimeMinutes = null,
    double? EstimatedFilamentLengthMm = null,
    double? EstimatedFilamentWeightG = null,
    Guid? PrinterModelId = null,
    string? SlicerName = null,
    string? SlicerVersion = null,
    string? SlicerSettings = null);

/// <summary>
/// Extracted metadata from a parsed G-code file (best-effort heuristics).
/// </summary>
public record GcodeMetadataDto(
    string? SlicerName = null,
    string? SlicerVersion = null,
    double? PrintTimeMinutes = null,
    double? FilamentLengthMm = null,
    double? FilamentWeightG = null,
    double? NozzleDiameter = null,
    string? Material = null,
    double? LayerHeight = null,
    string? InfillPercentage = null,
    double? PrintSpeed = null,
    double? BedTemperature = null,
    double? HotendTemperature = null,
    double? BuildPlateX = null,
    double? BuildPlateY = null,
    double? BuildPlateZ = null,
    string[]? Objects = null,
    Dictionary<string, object>? AdditionalMetadata = null);
