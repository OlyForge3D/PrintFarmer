using Farm.Infrastructure.Dtos;

namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Identifies the backing store for a unified file result.
/// </summary>
public enum UnifiedFileSource
{
    /// <summary>A 3D model stored by the slicer module.</summary>
    Model,

    /// <summary>A G-code file stored by the core application.</summary>
    Gcode,
}

/// <summary>
/// Selects which file categories are included in a unified files query.
/// </summary>
public enum UnifiedFileTypeFilter
{
    /// <summary>Include every supported source and category.</summary>
    All,

    /// <summary>Include canonical 3D model formats.</summary>
    Models,

    /// <summary>Include canonical G-code formats.</summary>
    Gcode,

    /// <summary>Include uncategorized formats from either source.</summary>
    Other,
}

/// <summary>
/// Selects the primary global ordering for a unified files query.
/// </summary>
public enum UnifiedFileSortBy
{
    /// <summary>Order by display name.</summary>
    Name,

    /// <summary>Order by file size.</summary>
    Size,

    /// <summary>Order by upload date.</summary>
    Date,
}

/// <summary>
/// Selects the direction of the global ordering.
/// </summary>
public enum UnifiedFileSortOrder
{
    /// <summary>Order from the lowest value to the highest.</summary>
    Asc,

    /// <summary>Order from the highest value to the lowest.</summary>
    Desc,
}

/// <summary>
/// Request contract for the globally merged Files page query.
/// </summary>
public sealed class UnifiedFilesQueryRequestDto
{
    /// <summary>Gets or sets the requested one-based page.</summary>
    public int Page { get; set; } = 1;

    /// <summary>Gets or sets the requested number of records per page.</summary>
    public int PageSize { get; set; } = 50;

    /// <summary>Gets or sets the optional display-name search term.</summary>
    public string? Search { get; set; }

    /// <summary>Gets or sets the primary global sort field.</summary>
    public UnifiedFileSortBy SortBy { get; set; } = UnifiedFileSortBy.Date;

    /// <summary>Gets or sets the global sort direction.</summary>
    public UnifiedFileSortOrder SortOrder { get; set; } = UnifiedFileSortOrder.Desc;

    /// <summary>Gets or sets the requested file category.</summary>
    public UnifiedFileTypeFilter Filter { get; set; } = UnifiedFileTypeFilter.All;

    /// <summary>Gets or sets an optional harvest operation filter for G-code records.</summary>
    public Guid? HarvestId { get; set; }

    /// <summary>Gets or sets an optional source-printer filter for G-code records.</summary>
    public Guid? PrinterId { get; set; }
}

/// <summary>
/// A single source-discriminated record in the unified Files page result.
/// </summary>
/// <param name="Source">The backing store for the record.</param>
/// <param name="Id">The source record identifier.</param>
/// <param name="Path">The stored directory path.</param>
/// <param name="Name">The user-facing display name.</param>
/// <param name="FileName">The internal stored filename.</param>
/// <param name="FileSize">The file size in bytes.</param>
/// <param name="FileType">The normalized file extension.</param>
/// <param name="UploadedAt">The upload timestamp.</param>
/// <param name="Url">The file download or viewer URL.</param>
/// <param name="ThumbnailUrl">The thumbnail URL when a thumbnail exists.</param>
/// <param name="Tags">Tags attached to the record.</param>
/// <param name="RequiredMaterial">The material required by a G-code record.</param>
/// <param name="ExtractedSlicerName">The slicer name extracted from G-code.</param>
/// <param name="ExtractedSlicerVersion">The slicer version extracted from G-code.</param>
/// <param name="ExtractedPrintTime">The estimated print time in minutes.</param>
/// <param name="ExtractedFilamentLength">The estimated filament length in millimetres.</param>
/// <param name="ExtractedNozzleDiameter">The required nozzle diameter in millimetres.</param>
/// <param name="ExtractedMaterial">The material extracted from G-code.</param>
/// <param name="ExtractedPrinterModel">The resolved printer model name.</param>
/// <param name="ExtractedPrinterModelName">The raw printer model name extracted from G-code.</param>
/// <param name="ExtractedLayerHeight">The layer height in millimetres.</param>
/// <param name="ExtractedInfill">The infill percentage.</param>
/// <param name="ExtractedPerimeters">The perimeter count.</param>
/// <param name="ExtractedHotendTemp">The hotend temperature in Celsius.</param>
/// <param name="ExtractedBedTemp">The bed temperature in Celsius.</param>
public sealed record UnifiedFileDto(
    UnifiedFileSource Source,
    Guid Id,
    string Path,
    string Name,
    string FileName,
    long FileSize,
    string FileType,
    DateTime UploadedAt,
    string Url,
    string? ThumbnailUrl,
    IReadOnlyList<TagDto>? Tags = null,
    string? RequiredMaterial = null,
    string? ExtractedSlicerName = null,
    string? ExtractedSlicerVersion = null,
    double? ExtractedPrintTime = null,
    double? ExtractedFilamentLength = null,
    double? ExtractedNozzleDiameter = null,
    string? ExtractedMaterial = null,
    string? ExtractedPrinterModel = null,
    string? ExtractedPrinterModelName = null,
    double? ExtractedLayerHeight = null,
    double? ExtractedInfill = null,
    int? ExtractedPerimeters = null,
    double? ExtractedHotendTemp = null,
    double? ExtractedBedTemp = null);

/// <summary>
/// Authoritative globally paginated response for the Files page.
/// </summary>
/// <param name="Items">The records on the requested global page.</param>
/// <param name="TotalItems">The true filtered count across both sources.</param>
/// <param name="TotalSize">The true filtered size in bytes across both sources.</param>
/// <param name="Page">The resolved one-based page.</param>
/// <param name="PageSize">The validated page size.</param>
/// <param name="TotalPages">The total number of global pages.</param>
public sealed record UnifiedFilesQueryResponse(
    IReadOnlyList<UnifiedFileDto> Items,
    int TotalItems,
    long TotalSize,
    int Page,
    int PageSize,
    int TotalPages);
