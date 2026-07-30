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
    /// JSON-serialized string array of filament color hex values per extruder (index = extruder number).
    /// Stored as a string for EF Core compatibility; parse as <c>string[]</c> in code.
    /// Null for single-extruder files or when per-extruder color data is unavailable.
    /// </summary>
    public string? FilamentPerExtruderColorHex { get; set; }

    /// <summary>
    /// JSON-serialized string array of filament material/type values per extruder (index = extruder number).
    /// Stored as a string for EF Core compatibility; parse as <c>string[]</c> in code.
    /// Null for single-extruder files or when per-extruder material data is unavailable.
    /// </summary>
    public string? FilamentPerExtruderType { get; set; }

    /// <summary>
    /// Number of extruders detected in the gcode file.
    /// Null when not parsed or for single-extruder files that don't declare extruder count.
    /// </summary>
    public int? ExtruderCount { get; set; }

    // Navigation property to harvest file mappings
    public ICollection<HarvestFileGcodeFileMapping> HarvestFileMappings { get; set; } = new List<HarvestFileGcodeFileMapping>();

    // --- Promotion lineage: written once when a slicer artifact is promoted into the library ---

    /// <summary>Slicer artifact whose bytes were streamed into this file (soft ref — no FK constraint).</summary>
    public Guid? SourceArtifactId { get; set; }

    /// <summary>Slice job that produced <see cref="SourceArtifactId"/> (soft ref — no FK constraint).</summary>
    public Guid? SourceSliceJobId { get; set; }

    /// <summary>Worker that produced the source artifact (soft ref — no FK constraint).</summary>
    public Guid? SourceWorkerId { get; set; }

    /// <summary>Calibration project the promoted output belongs to.</summary>
    public Guid? CalibrationProjectId { get; set; }

    /// <summary>Calibration attempt the promoted output belongs to.</summary>
    public Guid? CalibrationAttemptId { get; set; }

    /// <summary>Durable calibration orchestration that requested the promotion.</summary>
    public Guid? CalibrationOrchestrationId { get; set; }

    /// <summary>Idempotency operation key of the promotion that created this file.</summary>
    /// <remarks>
    /// Caller-supplied and therefore only unique inside the owner's scope; it is kept for diagnostics
    /// and never used as the persisted identity.
    /// </remarks>
    public string? PromotionOperationId { get; set; }

    /// <summary>
    /// Owner-scoped identity of the promotion that created this file. Uniqueness is enforced on this
    /// column so two owners may reuse the same raw idempotency key without colliding.
    /// </summary>
    public string? PromotionOperationKey { get; set; }

    /// <summary>Correlation identifier carried from the canonical slice submission.</summary>
    public Guid? PromotionCorrelationId { get; set; }

    /// <summary>SHA-256 (hex) of the promoted bytes as verified against the source artifact.</summary>
    public string? ContentSha256 { get; set; }

    /// <summary>SHA-256 (hex) of the canonical calibration specification behind the slice.</summary>
    public string? SpecificationSha256 { get; set; }

    /// <summary>SHA-256 (hex) of the stored model bytes the slice consumed.</summary>
    public string? SourceModelSha256 { get; set; }

    /// <summary>SHA-256 (hex) of the effective native machine profile delivered to the worker.</summary>
    public string? MachineProfileSha256 { get; set; }

    /// <summary>SHA-256 (hex) of the effective native process profile delivered to the worker.</summary>
    public string? ProcessProfileSha256 { get; set; }

    /// <summary>SHA-256 (hex) of the effective native filament profile delivered to the worker.</summary>
    public string? FilamentProfileSha256 { get; set; }

    /// <summary>Canonical slicer engine name recorded at promotion (for example <c>OrcaSlicer</c>).</summary>
    public string? SlicerEngineName { get; set; }

    /// <summary>Slicer distribution recorded at promotion (for example <c>upstream</c>).</summary>
    public string? SlicerDistribution { get; set; }

    /// <summary>Pinned slicer version the producing job required.</summary>
    public string? PinnedSlicerVersion { get; set; }

    /// <summary>Pinned slicer container digest the producing job required, when the deployment supplies one.</summary>
    public string? SlicerContainerDigest { get; set; }

    /// <summary>Firmware family the promoted G-code targets (for example <c>Klipper</c>).</summary>
    public string? FirmwareFamily { get; set; }

    /// <summary>G-code dialect the promoted output was generated for (for example <c>Klipper</c>).</summary>
    public string? GcodeDialect { get; set; }

    /// <summary>Name of the generator that produced the promoted output.</summary>
    public string? GeneratorName { get; set; }

    /// <summary>Version of the generator that produced the promoted output.</summary>
    public string? GeneratorVersion { get; set; }

    /// <summary>
    /// Server-built calibration manifest describing the promoted output. Contains identifiers, hashes
    /// and versions only — never paths, private URLs or credentials.
    /// </summary>
    public string? CalibrationManifestJson { get; set; }

    /// <summary>SHA-256 (hex) of the sibling calibration-manifest artifact, when the job produced one.</summary>
    public string? CalibrationManifestSha256 { get; set; }

    /// <summary>
    /// Marks content and lineage as immutable. Promoted files are never re-stamped, so a replayed or
    /// deduplicated promotion returns the original record rather than rewriting it.
    /// </summary>
    public bool IsImmutable { get; set; }

    /// <summary>UTC timestamp when the promotion completed.</summary>
    public DateTime? PromotedAtUtc { get; set; }
}
