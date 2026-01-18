namespace Farm.Infrastructure.Services.Gcode;

/// <summary>
/// Extracts metadata from G-code files as a fallback when printer API doesn't provide complete info.
/// Parses common comment patterns to extract slicer info, material, nozzle diameter, print time, filament length, etc.
/// </summary>
public interface IGcodeMetadataExtractorService
{
    /// <summary>
    /// Extract metadata from G-code content.
    /// </summary>
    Task<GcodeMetadataExtracted> ExtractMetadataAsync(string gcodeContent);
}

public class GcodeMetadataExtracted
{
    public string? SlicerName { get; set; }

    public string? SlicerVersion { get; set; }

    public double? EstimatedPrintTimeMinutes { get; set; }

    public double? FilamentLengthMm { get; set; }

    public double? FilamentWeightGrams { get; set; }

    public double? NozzleDiameter { get; set; }

    public string? Material { get; set; }

    public double? LayerHeight { get; set; }

    public double? InfillPercentage { get; set; } // Infill density as percentage (e.g., 15 for 15%)

    public double? PrintTemperature { get; set; } // First layer print/hotend temperature

    public double? BedTemperature { get; set; } // First layer bed temperature

    public int? Perimeters { get; set; } // Number of perimeter/wall loops (e.g., 2 for OrcaSlicer, perimeters for PrusaSlicer)

    public byte[]? ThumbnailData { get; set; } // PNG image data extracted from gcode

    public string? PrinterModel { get; set; } // Printer model file was sliced for (e.g., "Phrozen Arco 0.4")

    public string? PrintSettingsId { get; set; } // Slicer process profile ID used (e.g., from OrcaSlicer)
}
