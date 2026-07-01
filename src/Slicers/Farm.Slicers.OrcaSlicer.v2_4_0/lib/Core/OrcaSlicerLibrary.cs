using Farm.Slicer.Module.Contracts.Libraries;

namespace Farm.Slicers.OrcaSlicer.v2_4_0;

/// <summary>
/// OrcaSlicer v2.4.0 slicer library implementation.
/// </summary>
#pragma warning disable S101 // Class name required to match version numbering for plugin discovery
public class OrcaSlicerLibrary_v2_4_0 : ISlicerLibrary, IDisposable
#pragma warning restore S101
{
    private readonly ISlicerProfilesProvider _profilesProvider;
    private readonly ISlicerAssetRegistry _assetRegistry;

    public string SlicerName => "OrcaSlicer";

    public string SlicerVersion => "2.4.0";

    public string SlicerType => "OrcaSlicer";

    public ISlicerProfilesProvider ProfilesProvider => _profilesProvider;

    public ISlicerAssetRegistry AssetRegistry => _assetRegistry;

    public OrcaSlicerLibrary_v2_4_0()
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

    public void Dispose()
    {
        (_assetRegistry as IDisposable)?.Dispose();
        GC.SuppressFinalize(this);
    }
}
