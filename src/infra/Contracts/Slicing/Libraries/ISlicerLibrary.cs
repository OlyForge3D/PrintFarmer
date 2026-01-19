namespace Farm.Infrastructure.Contracts.Slicing.Libraries;

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
    /// <param name="config">The slicer configuration to validate</param>
    /// <param name="ct">Cancellation token for async operation</param>
    Task<SlicerConfigValidationResult> ValidateConfigAsync(
        object config,
        CancellationToken ct = default);
}
