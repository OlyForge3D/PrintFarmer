namespace Farm.Web.Shared.Contracts.Slicing.Libraries;

/// <summary>
/// Represents a versioned slicer library (e.g., OrcaSlicer 2.3.1, PrusaSlicer 2.9.3).
/// Each library encapsulates profiles, assets, and configuration for a specific slicer version.
/// </summary>
public interface ISlicerLibrary
{
    /// <summary>
    /// Name of the slicer (e.g., "OrcaSlicer", "PrusaSlicer").
    /// </summary>
    string SlicerName { get; }

    /// <summary>
    /// Semantic version of this slicer (e.g., "2.3.1").
    /// </summary>
    string SlicerVersion { get; }

    /// <summary>
    /// Slicer type identifier (maps to SlicerEngine enum).
    /// </summary>
    string SlicerType { get; }

    /// <summary>
    /// Provides access to official and system profiles for this slicer version.
    /// </summary>
    ISlicerProfilesProvider ProfilesProvider { get; }

    /// <summary>
    /// Provides access to bed textures, bed models, and printer cover images.
    /// </summary>
    ISlicerAssetRegistry AssetRegistry { get; }

    /// <summary>
    /// Validates that a slicer configuration is compatible with this library version.
    /// </summary>
    Task<SlicerConfigValidationResult> ValidateConfigAsync(
        object config,
        CancellationToken ct = default);
}

/// <summary>
/// Result of slicer configuration validation.
/// </summary>
public record SlicerConfigValidationResult(
    bool IsValid,
    string[] Errors = default!,
    string[] Warnings = default!)
{
    public SlicerConfigValidationResult() : this(true, [], []) { }
}

/// <summary>
/// Provides access to official and system profiles for a slicer.
/// </summary>
public interface ISlicerProfilesProvider
{
    /// <summary>
    /// Lists all official profiles available in this slicer version.
    /// </summary>
    Task<IEnumerable<SlicerProfileMetadata>> ListOfficialProfilesAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the full profile JSON for a specific profile ID.
    /// </summary>
    Task<string?> GetProfileJsonAsync(string profileId, CancellationToken ct = default);

    /// <summary>
    /// Gets the semantic version of the profiles bundled with this library.
    /// </summary>
    string GetProfilesVersion();
}

/// <summary>
/// Metadata about a single slicer profile.
/// </summary>
public record SlicerProfileMetadata(
    string Id,
    string Name,
    string Type,  // "printer", "filament", "process"
    string? Manufacturer = null,
    string? PrinterModel = null,
    string? Material = null,
    string? QualityLevel = null);

/// <summary>
/// Provides access to slicer assets (bed textures, bed models, cover images).
/// </summary>
public interface ISlicerAssetRegistry
{
    /// <summary>
    /// Gets asset metadata for a printer model.
    /// </summary>
    Task<SlicerAsset?> GetAssetAsync(
        string manufacturerName,
        string modelName,
        CancellationToken ct = default);

    /// <summary>
    /// Lists all available assets in this slicer version.
    /// </summary>
    Task<IEnumerable<SlicerAsset>> ListAssetsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the binary stream for a bed model (STL file).
    /// Returns null if not available.
    /// </summary>
    Stream? GetBedModelStream(string manufacturerName, string modelName);

    /// <summary>
    /// Gets the binary stream for a bed texture (SVG or PNG).
    /// Returns null if not available.
    /// </summary>
    Stream? GetBedTextureStream(string manufacturerName, string modelName);

    /// <summary>
    /// Gets the binary stream for a printer cover image.
    /// Returns null if not available.
    /// </summary>
    Stream? GetCoverImageStream(string manufacturerName, string modelName);
}

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
