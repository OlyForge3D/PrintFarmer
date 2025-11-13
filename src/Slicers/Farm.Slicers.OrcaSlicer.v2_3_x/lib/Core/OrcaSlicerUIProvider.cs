using Farm.Web.Shared.Contracts.Slicing.Libraries;

namespace Farm.Slicers.OrcaSlicer.v2_3_x;

/// <summary>
/// OrcaSlicer v2.3.1 UI provider.
/// Exposes UI capabilities and metadata for this slicer version.
/// </summary>
public class OrcaSlicerUIProvider_v2_3_x : ISlicerUIProvider
{
    public string SlicerName => "OrcaSlicer";
    public string SlicerVersion => "2.3.1";
    public bool HasBundleSupport => true;  // OrcaSlicer has bundle import/export
    public bool HasAssetCustomization => true;  // OrcaSlicer has specific bed texture formats
    public bool HasEngineSpecificSettings => true;  // OrcaSlicer has jitter and other settings

    // TODO: Update these to actual OrcaSlicer-specific types when available
    public Type ProfileConfigType => typeof(object);
    public Type SettingsType => typeof(object);

    public string GetDescription() => "OrcaSlicer v2.3.1 - Supports bundle import/export, custom assets, and engine-specific settings.";
}
