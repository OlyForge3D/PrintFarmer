using Farm.Web.Shared.Contracts.Slicing.Libraries;
using System.Reflection;

namespace Farm.Slicers.PrusaSlicer.v2_9_x.lib;

/// <summary>
/// PrusaSlicer 2.9.x library implementation
/// </summary>
public class PrusaSlicerLibrary_v2_9_x : ISlicerLibrary
{
    public string Name => "PrusaSlicer";
    public string Version => "2.9.x";
    public string DisplayName => "PrusaSlicer 2.9.x";

    public ISlicerProfilesProvider ProfilesProvider => new PrusaSlicerProfilesProvider();
    public ISlicerAssetRegistry AssetRegistry => new PrusaSlicerAssetRegistry();
}
