namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Represents a plate defined within a 3MF project.
/// </summary>
public record ThreeMfPlateDto
{
    public int Index { get; init; }
    public string Name { get; init; } = string.Empty;
}

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

    public List<ThreeMfPlateDto> Plates { get; init; } = [];
}
