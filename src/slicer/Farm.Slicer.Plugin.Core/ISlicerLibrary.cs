namespace Farm.Slicer.Module.Contracts.Libraries;

/// <summary>
/// Represents a versioned slicer library (e.g., OrcaSlicer 2.3.1, PrusaSlicer 2.9.3).
/// </summary>
public interface ISlicerLibrary
{
    /// <summary>Gets the slicer name (e.g., "OrcaSlicer").</summary>
    string SlicerName { get; }

    /// <summary>Gets the slicer version (e.g., "2.3.1").</summary>
    string SlicerVersion { get; }

    /// <summary>Gets the slicer type identifier.</summary>
    string SlicerType { get; }

    /// <summary>Gets the profiles provider for this slicer library.</summary>
    ISlicerProfilesProvider ProfilesProvider { get; }

    /// <summary>Gets the asset registry for this slicer library.</summary>
    ISlicerAssetRegistry AssetRegistry { get; }

    /// <summary>
    /// Validates a slicer configuration object.
    /// </summary>
    /// <param name="config">The configuration object to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The validation result indicating success or failure details.</returns>
    Task<SlicerConfigValidationResult> ValidateConfigAsync(object config, CancellationToken ct = default);
}
