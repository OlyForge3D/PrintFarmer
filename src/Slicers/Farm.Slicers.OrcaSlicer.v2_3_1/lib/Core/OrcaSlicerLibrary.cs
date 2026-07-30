using Farm.Slicer.Module.Contracts.Libraries;

namespace Farm.Slicers.OrcaSlicer.v2_3_1;

/// <summary>
/// OrcaSlicer v2.3.1 slicer library implementation. Ships alongside v2_4_0 to
/// support the "current + previous" engine matrix (issue #578).
/// </summary>
#pragma warning disable S101 // Class name required to match version numbering for plugin discovery
public class OrcaSlicerLibrary_v2_3_1 : ISlicerLibrary, IDisposable
#pragma warning restore S101
{
    private readonly ISlicerProfilesProvider _profilesProvider;
    private readonly ISlicerAssetRegistry _assetRegistry;

    public string SlicerName => "OrcaSlicer";

    // Must parse as System.Version — SlicerRegistry sorts descending by version.
    public string SlicerVersion => "2.3.1";

    public string SlicerType => "OrcaSlicer";

    public ISlicerProfilesProvider ProfilesProvider => _profilesProvider;

    public ISlicerAssetRegistry AssetRegistry => _assetRegistry;

    public OrcaSlicerLibrary_v2_3_1()
    {
        _profilesProvider = new NullProfilesProvider();
        _assetRegistry = new OrcaSlicerAssetRegistry();
    }

    public Task<SlicerConfigValidationResult> ValidateConfigAsync(
        object config,
        CancellationToken ct = default)
    {
        return Task.FromResult(new SlicerConfigValidationResult());
    }

    public void Dispose()
    {
        (_assetRegistry as IDisposable)?.Dispose();
        GC.SuppressFinalize(this);
    }
}
