namespace Farm.Slicer.Module.Contracts.Libraries;

/// <summary>
/// Provides UI metadata for a slicer library version.
/// </summary>
public interface ISlicerUIProvider
{
    /// <summary>Gets the slicer name.</summary>
    string SlicerName { get; }

    /// <summary>Gets the slicer version.</summary>
    string SlicerVersion { get; }

    /// <summary>Gets a value indicating whether this slicer supports bundle import/export.</summary>
    bool HasBundleSupport { get; }

    /// <summary>Gets a value indicating whether this slicer supports asset customization.</summary>
    bool HasAssetCustomization { get; }

    /// <summary>Gets a value indicating whether this slicer has engine-specific settings.</summary>
    bool HasEngineSpecificSettings { get; }

    /// <summary>Gets the <see cref="Type"/> used for profile configuration.</summary>
    Type ProfileConfigType { get; }

    /// <summary>Gets the <see cref="Type"/> used for slicer settings.</summary>
    Type SettingsType { get; }

    /// <summary>
    /// Gets a human-readable description of this slicer UI provider.
    /// </summary>
    /// <returns>Description string.</returns>
    string GetDescription();
}
