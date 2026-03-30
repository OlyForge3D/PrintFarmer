using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

public class GcodeFile : StoredFile
{
    /// <summary>
    /// File extension/type derived from FileName (e.g., "gcode", "bgcode").
    /// Computed property - not stored in database.
    /// </summary>
    public string FileType => System.IO.Path.GetExtension(FileName).TrimStart('.').ToLowerInvariant();

    public GcodeSource Source { get; set; }

    public Guid? SourcePrinterId { get; set; } // Printer it was harvested from

    public Printer? SourcePrinter { get; set; }

    public string? OriginalPrinterPath { get; set; } // Original path on the printer

    public DateTime? LastSeenOnPrinter { get; set; } // Last time this file was seen during harvest

    public double? RequiredNozzleDiameter { get; set; } // e.g., 0.4mm

    public string? RequiredMaterial { get; set; } // e.g., "PLA", "PETG"

    public double? EstimatedPrintTimeMinutes { get; set; }

    public double? EstimatedFilamentLengthMm { get; set; }

    public double? EstimatedFilamentWeightG { get; set; }

    public string? ExtractedPrinterModelName { get; set; } // Raw printer model name extracted from gcode (before resolution to PrinterModelId)

    public Guid? PrinterModelId { get; set; } // Printer model this file was sliced for (resolved from extracted name)

    public PrinterModel? PrinterModel { get; set; }

    public string? SlicerName { get; set; } // e.g., "PrusaSlicer", "Cura"

    public string? SlicerVersion { get; set; }

    public string? PrintSettingsId { get; set; } // Slicer process profile name (e.g., "Standard", "Draft") - different from printer model

    public double? LayerHeight { get; set; }

    public double? InfillPercentage { get; set; }

    public int? Perimeters { get; set; } // Number of perimeter/wall loops

    public double? PrintTemperature { get; set; } // First layer print/hotend temperature

    public double? BedTemperature { get; set; } // First layer bed temperature

    public double? PrintSpeed { get; set; }

    // --- High Value: Print Management ---
    public int? TotalLayers { get; set; }

    public double? FirstLayerHeight { get; set; }

    public bool? SupportEnabled { get; set; }

    public int? ToolChangesCount { get; set; }

    // --- Medium Value: Cost & Planning ---
    public double? ObjectDimensionX { get; set; }

    public double? ObjectDimensionY { get; set; }

    public double? ObjectDimensionZ { get; set; }

    public int? ObjectCount { get; set; }

    public double? RetractionLength { get; set; }

    public double? RetractionSpeed { get; set; }

    // --- Medium Value: Quality & Compatibility ---
    public int? TopSolidLayers { get; set; }

    public int? BottomSolidLayers { get; set; }

    public double? MaxVolumetricSpeed { get; set; }

    public bool? IroningEnabled { get; set; }

    public Guid? PrinterGroupId { get; set; } // Optional: restricts dispatch to printers in this group

    public PrinterGroup? PrinterGroup { get; set; }

    /// <summary>
    /// JSON-serialized double array of filament weight in grams per extruder (index = extruder number).
    /// Stored as a string for EF Core compatibility; parse as <c>double[]</c> in code.
    /// Null for single-extruder files or when per-extruder data is unavailable.
    /// </summary>
    public string? FilamentPerExtruderWeightG { get; set; }

    /// <summary>
    /// JSON-serialized double array of filament length in mm per extruder (index = extruder number).
    /// Stored as a string for EF Core compatibility; parse as <c>double[]</c> in code.
    /// Null for single-extruder files or when per-extruder data is unavailable.
    /// </summary>
    public string? FilamentPerExtruderLengthMm { get; set; }

    /// <summary>
    /// Number of extruders detected in the gcode file.
    /// Null when not parsed or for single-extruder files that don't declare extruder count.
    /// </summary>
    public int? ExtruderCount { get; set; }

    // Navigation property to harvest file mappings
    public ICollection<HarvestFileGcodeFileMapping> HarvestFileMappings { get; set; } = new List<HarvestFileGcodeFileMapping>();
}
