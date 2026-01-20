namespace Farm.Infrastructure;

// 3D Model Management DTOs
public class Model3DDto
{
    public Guid Id { get; set; }

    public string FileName { get; set; } = string.Empty; // GUID-based filename for internal storage

    public string? Name { get; set; } // Original filename uploaded by user (for display and editing)

    public long FileSize { get; set; }

    public string FileType { get; set; } = string.Empty; // stl, 3mf, obj, ply

    public DateTime UploadedAt { get; set; }

    /// <summary>
    /// URL to download the file. Auto-generated from Id if not explicitly set.
    /// </summary>
    public string Url { get; init; } = string.Empty;

    public string? ThumbnailUrl { get; set; }

    public string? Description { get; set; }

    public double? DimensionX { get; set; } // in mm

    public double? DimensionY { get; set; } // in mm

    public double? DimensionZ { get; set; } // in mm

    public int? TriangleCount { get; set; }

    public bool IsValid { get; set; } = true;

    public string? ValidationErrors { get; set; }

    public TagDto[]? Tags { get; set; }
}

/// <summary>
/// Entry in a hierarchical model file listing (file or directory)
/// </summary>
public record Model3DEntryDto(
    string Path,
    string FileName,
    long FileSize,
    DateTime UploadedAt,
    bool IsDirectory,
    string? ThumbnailUrl = null,
    string? Id = null,  // Include model ID for efficient file lookups
    string? DirectoryId = null,  // Include directory ID for efficient directory lookups
    string? Name = null,  // Original filename for display (not GUID)
    string? FileType = null);  // File extension: stl, 3mf, obj, ply

/// <summary>
/// Response envelope for hierarchical model file listing
/// </summary>
public record Model3DListResponse(
    IReadOnlyList<Model3DEntryDto> Files,
    int TotalFiles,
    long TotalSize,
    int Page,
    int PageSize,
    int TotalPages,
    int TotalItems);

/// <summary>
/// Request to update 3D model properties
/// </summary>
public class UpdateModel3DDto
{
    public string? Name { get; set; }
}

/// <summary>
/// Search/filter parameters for 3D models
/// </summary>
public class Model3DSearchRequestDto
{
    public string? Query { get; set; } // Search in name/description

    public Guid[]? TagIds { get; set; } // Filter by tags (AND logic - must have all specified tags)

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 20;

    public string? SortBy { get; set; } = "uploadedAt"; // uploadedAt, name, size

    public bool Descending { get; set; } = true;
}

/// <summary>
/// Paginated search results for 3D models
/// </summary>
public class Model3DSearchResultDto
{
    public Model3DDto[] Models { get; set; } = [];

    public int TotalCount { get; set; }

    public int Page { get; set; }

    public int PageSize { get; set; }

    public int TotalPages { get; set; }
}

public class Model3DUploadResultDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public long FileSize { get; set; }

    public string FileType { get; set; } = string.Empty;

    public DateTime UploadedAt { get; set; }

    public string Url { get; set; } = string.Empty;
}

public class Model3DValidationResultDto
{
    public bool Valid { get; set; }

    public string[]? Issues { get; set; }
}
