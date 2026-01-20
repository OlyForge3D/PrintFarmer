namespace Farm.Infrastructure;

// G-code Library Search/Filter DTOs

/// <summary>
/// Search and filter parameters for querying the G-code library.
/// </summary>
public class GcodeLibrarySearchDto
{
    public string? SearchTerm { get; set; }

    public string[]? Tags { get; set; }

    public string? RequiredMaterial { get; set; }

    public double? NozzleDiameter { get; set; }

    public Guid? TargetPrinterId { get; set; }

    public Guid? TargetModelId { get; set; }

    public DateTime? UploadedAfter { get; set; }

    public DateTime? UploadedBefore { get; set; }

    public int Skip { get; set; }

    public int Take { get; set; } = 50;

    public string SortBy { get; set; } = "UploadedAt";

    public bool SortDescending { get; set; } = true;
}

/// <summary>
/// Result payload for a library search including available facets.
/// </summary>
public record GcodeLibrarySearchResultDto(
    GcodeFileDto[] Files,
    int TotalCount,
    string[] AvailableTags,
    string[] AvailableMaterials);
