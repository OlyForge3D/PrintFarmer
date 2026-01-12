using Farm.Infrastructure.Contracts.Slicing.Libraries;

namespace Farm.Slicers.PrusaSlicer.v2_9_x.lib;

/// <summary>
/// PrusaSlicer UI provider for the PrintFarmer dashboard
/// </summary>
public class PrusaSlicerUIProvider_v2_9_x : ISlicerUIProvider
{
    public string SlicerName => "PrusaSlicer";
    public string Version => "2.9.x";

    public bool HasImportUI => true;
    public bool HasSettingsUI => false;
    public bool HasProfileEditorUI => false;

    public string? GetImportUIPath => "import-prusa";
    public string? GetSettingsUIPath => null;
    public string? GetProfileEditorUIPath => null;
}
