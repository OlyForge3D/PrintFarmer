using Farm.Web.Shared;

namespace Farm.Web.Api.Services.Slicing;

/// <summary>
/// Printer preset match result with confidence score.
/// </summary>
public class PrinterPresetMatch
{
    public OrcaPrinterPresetDto Preset { get; set; } = new();
    public Guid? MatchedPrinterModelId { get; set; }
    public string? MatchedPrinterModelName { get; set; }
    public string? MatchedManufacturerName { get; set; }
    public double ConfidenceScore { get; set; } // 0.0 to 1.0
    public List<string> MatchReasons { get; set; } = new();
}

/// <summary>
/// Filament preset match result with confidence score.
/// </summary>
public class FilamentPresetMatch
{
    public OrcaFilamentPresetDto Preset { get; set; } = new();
    public string? MatchedMaterialType { get; set; }
    public double ConfidenceScore { get; set; }
    public List<string> MatchReasons { get; set; } = new();
}

/// <summary>
/// Process preset match with quality classification.
/// </summary>
public class ProcessPresetMatch
{
    public OrcaProcessPresetDto Preset { get; set; } = new();
    public string DerivedQuality { get; set; } = "Standard";
    public double ConfidenceScore { get; set; }
    public List<string> MatchReasons { get; set; } = new();
}

/// <summary>
/// Complete mapping result for an Orca bundle with all matched presets.
/// </summary>
public class OrcaBundleMappingResult
{
    public List<PrinterPresetMatch> PrinterMatches { get; set; } = new();
    public List<FilamentPresetMatch> FilamentMatches { get; set; } = new();
    public List<ProcessPresetMatch> ProcessMatches { get; set; } = new();
    public int TotalPresets { get; set; }
    public int HighConfidenceMatches { get; set; } // Confidence >= 0.7
    public int MediumConfidenceMatches { get; set; } // 0.4 <= Confidence < 0.7
    public int LowConfidenceMatches { get; set; } // Confidence < 0.4
}

/// <summary>
/// Service for mapping OrcaSlicer bundle presets to PrintFarmer catalog entities (printer models, materials, etc.).
/// </summary>
public interface IOrcaPresetMappingService
{
    /// <summary>
    /// Maps all presets in an Orca bundle to existing catalog entities with confidence scoring.
    /// </summary>
    /// <param name="preview">Parsed Orca bundle preview</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Mapping result with matches and confidence scores</returns>
    Task<OrcaBundleMappingResult> MapBundlePresetsAsync(OrcaBundlePreviewDto preview, CancellationToken ct = default);
}
