using Farm.Slicer.Module.Contracts.Libraries;

namespace Farm.Slicers.OrcaSlicer.v2_3_1;

/// <summary>
/// OrcaSlicer v2.3.1 slicer library implementation.
/// </summary>
#pragma warning disable S101 // Class name required to match version numbering for plugin discovery
public class OrcaSlicerLibrary_v2_3_1 : ISlicerLibrary
#pragma warning restore S101
{
    private readonly ISlicerProfilesProvider _profilesProvider;
    private readonly ISlicerAssetRegistry _assetRegistry;

    public string SlicerName => "OrcaSlicer";

    public string SlicerVersion => "2.3.1";

    public string SlicerType => "OrcaSlicer";

    public ISlicerProfilesProvider ProfilesProvider => _profilesProvider;

    public ISlicerAssetRegistry AssetRegistry => _assetRegistry;

    public OrcaSlicerLibrary_v2_3_1()
    {
        // Profiles are loaded from the OrcaSlicer worker service (/api/profiles), not from bundled resources
        // The worker parses profiles from the official OrcaSlicer installation at /opt/orcaslicer/resources/profiles/
        _profilesProvider = new NullProfilesProvider();
        _assetRegistry = new OrcaSlicerAssetRegistry();
    }

    public Task<SlicerConfigValidationResult> ValidateConfigAsync(
        object config,
        CancellationToken ct = default)
    {
        // For now, accept any config. This can be enhanced to validate OrcaSlicer-specific properties.
        return Task.FromResult(new SlicerConfigValidationResult());
    }
}
