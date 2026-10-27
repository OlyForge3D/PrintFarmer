using Farm.Infrastructure.Services.Printers;

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

    /// <summary>Parses bounded source regions without inferring counts from a partial command body.</summary>
    GcodeMetadataExtractionResult ExtractBoundedMetadata(
        string gcodeContent, bool captureThumbnail, CancellationToken ct = default);
}

/// <summary>Metadata and image outcomes remain independent when one bounded extraction is incomplete.</summary>
public sealed record GcodeMetadataExtractionResult(
    GcodeMetadataExtracted Metadata,
    PrinterFileEnrichmentState MetadataState,
    PrinterFileEnrichmentState ThumbnailState,
    string? MetadataReason = null,
    string? ThumbnailReason = null);

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

    public string? InfillPattern { get; set; }

    public double? PrintTemperature { get; set; } // First layer print/hotend temperature

    public double? BedTemperature { get; set; } // First layer bed temperature

    public int? Perimeters { get; set; } // Number of perimeter/wall loops (e.g., 2 for OrcaSlicer, perimeters for PrusaSlicer)

    public byte[]? ThumbnailData { get; set; } // PNG image data extracted from gcode

    public string? PrinterModel { get; set; } // Printer model file was sliced for (e.g., "Phrozen Arco 0.4")

    public string? PrintSettingsId { get; set; } // Slicer process profile ID used (e.g., from OrcaSlicer)

    // --- High Value: Print Management ---
    public int? TotalLayers { get; set; } // Total number of layers in the print

    public double? FirstLayerHeight { get; set; } // First layer height (mm), often differs from layer_height

    public bool? SupportEnabled { get; set; } // Whether support material is enabled

    public string? SupportType { get; set; }

    public string? SupportStyle { get; set; }

    public PrinterFilamentTool[]? Tools { get; set; }

    public bool? FilamentWeightIsDerived { get; set; }

    public int? ToolChangesCount { get; set; } // Number of tool/extruder changes (multi-color indicator)

    // --- Medium Value: Cost & Planning ---
    public double? ObjectDimensionX { get; set; } // Bounding box X dimension (mm)

    public double? ObjectDimensionY { get; set; } // Bounding box Y dimension (mm)

    public double? ObjectDimensionZ { get; set; } // Bounding box Z dimension (mm)

    public int? ObjectCount { get; set; } // Number of distinct objects/meshes on the plate

    public double? RetractionLength { get; set; } // Retraction distance (mm)

    public double? RetractionSpeed { get; set; } // Retraction speed (mm/s)

    // --- Medium Value: Quality & Compatibility ---
    public int? TopSolidLayers { get; set; } // Number of top solid layers

    public int? BottomSolidLayers { get; set; } // Number of bottom solid layers

    public double? MaxVolumetricSpeed { get; set; } // Maximum volumetric extrusion speed (mm³/s)

    public bool? IroningEnabled { get; set; } // Whether ironing is enabled for top surfaces

    // --- Multi-extruder: Per-Extruder Filament Data ---

    /// <summary>
    /// Filament weight in grams per extruder (index = extruder number). Null for single-extruder files.
    /// </summary>
    public double[]? FilamentPerExtruderWeightG { get; set; }

    /// <summary>
    /// Filament length in mm per extruder (index = extruder number). Null for single-extruder files.
    /// </summary>
    public double[]? FilamentPerExtruderLengthMm { get; set; }

    /// <summary>
    /// Filament color hex values per extruder (index = extruder number). Null when unavailable.
    /// </summary>
    public string[]? FilamentPerExtruderColorHex { get; set; }

    /// <summary>
    /// Filament material/type values per extruder (index = extruder number). Null when unavailable.
    /// </summary>
    public string[]? FilamentPerExtruderType { get; set; }

    /// <summary>
    /// Detected number of extruders in the gcode file. Null when not parsed.
    /// </summary>
    public int? ExtruderCount { get; set; }

    /// <summary>Projects source-supported facts into the bounded printer catalog contract.</summary>
    public PrinterPrintMetadata ToPrinterPrintMetadata()
    {
        PrinterFilamentTool[]? tools = Tools is { Length: > 0 and <= PrinterPrintMetadata.MaximumTools }
            && Tools.All(tool => tool.IsValid()) ? Tools : null;
        return new()
        {
            SlicerName = Text(SlicerName),
            SlicerVersion = Text(SlicerVersion),
            EstimatedPrintTimeSeconds = Number(EstimatedPrintTimeMinutes * 60),
            LayerHeightMm = Number(LayerHeight),
            LayerCount = TotalLayers is >= 0 ? TotalLayers : null,
            NozzleDiameterMm = Number(NozzleDiameter),
            NozzleTemperatureC = Number(PrintTemperature),
            BedTemperatureC = Number(BedTemperature),
            InfillPercentage = InfillPercentage is <= 100 ? Number(InfillPercentage) : null,
            InfillPattern = Text(InfillPattern),
            SupportEnabled = SupportEnabled,
            SupportType = Text(SupportType),
            SupportStyle = Text(SupportStyle),
            PrinterModel = Text(PrinterModel),
            Profile = Text(PrintSettingsId),
            FilamentType = Text(Material),
            FilamentUsedG = Number(FilamentWeightGrams),
            FilamentUsedGIsDerived = Number(FilamentWeightGrams).HasValue ? FilamentWeightIsDerived : null,
            FilamentUsedMm = Number(FilamentLengthMm),
            FilamentUsedPerToolG = Numbers(FilamentPerExtruderWeightG),
            FilamentUsedPerToolMm = Numbers(FilamentPerExtruderLengthMm),
            FilamentTypePerTool = FilamentPerExtruderType is { Length: > 0 and <= PrinterPrintMetadata.MaximumTools }
                && FilamentPerExtruderType.All(PrinterPrintMetadata.IsValidText) ? FilamentPerExtruderType : null,
            Tools = tools,
        };

        static string? Text(string? value) => PrinterPrintMetadata.IsValidText(value) ? value : null;
        static double? Number(double? value) =>
            value.HasValue && PrinterPrintMetadata.IsValidNumber(value.Value) ? value : null;
        static double[]? Numbers(double[]? values) =>
            values is { Length: > 0 and <= PrinterPrintMetadata.MaximumTools } &&
            values.All(PrinterPrintMetadata.IsValidNumber) ? values : null;
    }
}
