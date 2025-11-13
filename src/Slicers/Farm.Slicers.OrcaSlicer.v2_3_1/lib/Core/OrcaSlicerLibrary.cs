using Farm.Web.Shared.Contracts.Slicing.Libraries;

namespace Farm.Slicers.OrcaSlicer.v2_3_1;

/// <summary>
/// OrcaSlicer v2.3.1 slicer library implementation.
/// </summary>
public class OrcaSlicerLibrary_v2_3_1 : ISlicerLibrary
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
        _profilesProvider = new OrcaSlicerProfilesProvider();
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
