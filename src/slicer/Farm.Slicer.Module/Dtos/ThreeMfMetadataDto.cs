namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Metadata extracted from a 3MF file's XML content.
/// </summary>
public record ThreeMfMetadataDto
{
    public string? Title { get; init; }

    public string? Designer { get; init; }

    public string? Description { get; init; }

    public string? Application { get; init; }

    public string? CreationDate { get; init; }

    public string? ModificationDate { get; init; }

    public List<string> Materials { get; init; } = [];

    public List<string> AutoTags { get; init; } = [];
}
