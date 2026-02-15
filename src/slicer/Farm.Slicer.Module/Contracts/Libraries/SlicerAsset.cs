namespace Farm.Slicer.Module.Contracts.Libraries;

/// <summary>
/// Metadata about assets available for a specific printer model.
/// </summary>
public record SlicerAsset(
    string ManufacturerName,
    string ModelName,
    bool HasBedModel,
    bool HasBedTexture,
    string? BedTextureFormat,
    bool HasCoverImage,
    string SlicerLibraryVersion);
