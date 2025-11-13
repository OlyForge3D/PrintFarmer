namespace Farm.Web.Shared.Contracts.Slicing.Libraries;

/// <summary>
/// Provides UI metadata for a slicer library version.
/// Allows core API to understand what UI capabilities each slicer version exposes.
/// </summary>
public interface ISlicerUIProvider
{
    /// <summary>
    /// Name of the slicer (e.g., "OrcaSlicer", "PrusaSlicer").
    /// </summary>
    string SlicerName { get; }

    /// <summary>
    /// Version of this slicer (e.g., "2.3.1").
    /// </summary>
    string SlicerVersion { get; }

    /// <summary>
    /// Indicates whether this slicer supports bundle import/export (e.g., OrcaSlicer).
    /// </summary>
    bool HasBundleSupport { get; }

    /// <summary>
    /// Indicates whether this slicer has custom asset preferences (bed texture formats, etc).
    /// </summary>
    bool HasAssetCustomization { get; }

    /// <summary>
    /// Indicates whether this slicer has slicer-engine-specific settings (like jitter, template args).
    /// </summary>
    bool HasEngineSpecificSettings { get; }

    /// <summary>
    /// The .NET type representing this slicer's profile configuration.
    /// Used for runtime type resolution when deserializing slicer-specific profiles.
    /// </summary>
    Type ProfileConfigType { get; }

    /// <summary>
    /// The .NET type representing this slicer's engine-specific settings.
    /// </summary>
    Type SettingsType { get; }

    /// <summary>
    /// Gets a human-readable description of this slicer version.
    /// </summary>
    string GetDescription();
}
