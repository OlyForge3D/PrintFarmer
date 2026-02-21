using Farm.Slicer.Module.Models;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Printer preset match result with confidence score.
/// </summary>
public class PrinterPresetMatch
{
    /// <summary>Gets or sets the matched printer preset.</summary>
    public OrcaPrinterPresetDto Preset { get; set; } = new();

    /// <summary>Gets or sets the matched printer model identifier.</summary>
    public Guid? MatchedPrinterModelId { get; set; }

    /// <summary>Gets or sets the matched printer model name.</summary>
    public string? MatchedPrinterModelName { get; set; }

    /// <summary>Gets or sets the matched manufacturer name.</summary>
    public string? MatchedManufacturerName { get; set; }

    /// <summary>Gets or sets the confidence score from 0.0 to 1.0.</summary>
    public double ConfidenceScore { get; set; }

    /// <summary>Gets or sets the reasons for the match.</summary>
    public List<string> MatchReasons { get; set; } = new();
}

/// <summary>
/// Filament preset match result with confidence score.
/// </summary>
public class FilamentPresetMatch
{
    /// <summary>Gets or sets the matched filament preset.</summary>
    public OrcaFilamentPresetDto Preset { get; set; } = new();

    /// <summary>Gets or sets the matched material type name.</summary>
    public string? MatchedMaterialType { get; set; }

    /// <summary>Gets or sets the confidence score from 0.0 to 1.0.</summary>
    public double ConfidenceScore { get; set; }

    /// <summary>Gets or sets the reasons for the match.</summary>
    public List<string> MatchReasons { get; set; } = new();
}

/// <summary>
/// Process preset match with quality classification.
/// </summary>
public class ProcessPresetMatch
{
    /// <summary>Gets or sets the matched process preset.</summary>
    public OrcaProcessPresetDto Preset { get; set; } = new();

    /// <summary>Gets or sets the derived quality classification.</summary>
    public string DerivedQuality { get; set; } = "Standard";

    /// <summary>Gets or sets the confidence score from 0.0 to 1.0.</summary>
    public double ConfidenceScore { get; set; }

    /// <summary>Gets or sets the reasons for the match.</summary>
    public List<string> MatchReasons { get; set; } = new();
}

/// <summary>
/// Complete mapping result for an Orca bundle with all matched presets.
/// </summary>
public class OrcaBundleMappingResult
{
    /// <summary>Gets or sets the printer preset matches.</summary>
    public List<PrinterPresetMatch> PrinterMatches { get; set; } = new();

    /// <summary>Gets or sets the filament preset matches.</summary>
    public List<FilamentPresetMatch> FilamentMatches { get; set; } = new();

    /// <summary>Gets or sets the process preset matches.</summary>
    public List<ProcessPresetMatch> ProcessMatches { get; set; } = new();

    /// <summary>Gets or sets the total number of presets in the bundle.</summary>
    public int TotalPresets { get; set; }

    /// <summary>Gets or sets count of high-confidence matches (≥ 0.7).</summary>
    public int HighConfidenceMatches { get; set; }

    /// <summary>Gets or sets count of medium-confidence matches (0.4 – 0.7).</summary>
    public int MediumConfidenceMatches { get; set; }

    /// <summary>Gets or sets count of low-confidence matches (&lt; 0.4).</summary>
    public int LowConfidenceMatches { get; set; }
}

/// <summary>
/// Service for mapping OrcaSlicer bundle presets to catalog entities with confidence scoring.
/// </summary>
public interface IOrcaPresetMappingService
{
    /// <summary>
    /// Maps all presets in an Orca bundle to existing catalog entities with confidence scoring.
    /// </summary>
    /// <param name="preview">Parsed Orca bundle preview.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Mapping result with matches and confidence scores.</returns>
    Task<OrcaBundleMappingResult> MapBundlePresetsAsync(OrcaBundlePreviewDto preview, CancellationToken ct = default);
}
