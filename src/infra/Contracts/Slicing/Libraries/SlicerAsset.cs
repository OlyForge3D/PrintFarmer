namespace Farm.Infrastructure.Contracts.Slicing.Libraries;

/// <summary>
/// Metadata about assets available for a specific printer model.
/// </summary>
public record SlicerAsset(
    string ManufacturerName,
    string ModelName,
    bool HasBedModel,
    bool HasBedTexture,
    string? BedTextureFormat,  // "svg" or "png"
    bool HasCoverImage,
    string SlicerLibraryVersion);
