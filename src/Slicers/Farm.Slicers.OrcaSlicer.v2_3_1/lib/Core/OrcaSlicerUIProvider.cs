using Farm.Slicer.Module.Contracts.Libraries;

namespace Farm.Slicers.OrcaSlicer.v2_3_1;

/// <summary>
/// OrcaSlicer v2.3.1 UI provider.
/// </summary>
#pragma warning disable S101 // Class name required to match version numbering for plugin discovery
public class OrcaSlicerUIProvider_v2_3_1 : ISlicerUIProvider
#pragma warning restore S101
{
    public string SlicerName => "OrcaSlicer";

    public string SlicerVersion => "2.3.1";

    public bool HasBundleSupport => true;

    public bool HasAssetCustomization => true;

    public bool HasEngineSpecificSettings => true;

    public Type ProfileConfigType => typeof(object);

    public Type SettingsType => typeof(object);

#pragma warning disable CA1024
    public string GetDescription() => "OrcaSlicer v2.3.1 - previous-generation engine, kept alongside the current release for reproducible re-slicing of jobs pinned to the older engine.";
#pragma warning restore CA1024
}
