using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Request payload for importing a subset of discovered harvested files into the library.
/// </summary>
public class ImportSelectedGcodeFilesDto
{
    public Guid HarvestOperationId { get; set; }

    public Guid[] FileIds { get; set; } = [];

    public bool AddToLibraryOnly { get; set; } = true; // If false, also create print jobs

    public bool AutoDetectCapabilities { get; set; } = true;

    public string[]? DefaultTags { get; set; }
}
