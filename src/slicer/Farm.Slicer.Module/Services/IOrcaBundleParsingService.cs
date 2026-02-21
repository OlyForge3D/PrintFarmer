using Farm.Slicer.Module.Models;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Service for parsing OrcaSlicer config bundles (JSON format with printer, filament, process presets).
/// </summary>
public interface IOrcaBundleParsingService
{
    /// <summary>Parses an OrcaSlicer config bundle JSON and returns a preview of all detected presets.</summary>
    /// <param name="bundleJson">Raw OrcaSlicer config bundle JSON string.</param>
    /// <returns>Preview DTO with extracted printer, filament, and process presets.</returns>
    /// <exception cref="ArgumentException">Thrown when bundleJson is invalid or empty.</exception>
    /// <exception cref="FormatException">Thrown when JSON structure doesn't match expected Orca format.</exception>
    OrcaBundlePreviewDto ParseBundle(string bundleJson);

    /// <summary>Validates whether the provided JSON matches OrcaSlicer bundle format.</summary>
    /// <param name="bundleJson">Raw JSON to validate.</param>
    /// <returns>True if valid Orca bundle format, false otherwise.</returns>
    bool IsValidOrcaBundle(string bundleJson);
}
