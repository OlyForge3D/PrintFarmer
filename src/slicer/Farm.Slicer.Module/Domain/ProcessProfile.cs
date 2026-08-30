using System.ComponentModel.DataAnnotations;

namespace Farm.Slicer.Module.Domain;

/// <summary>
/// Process/Quality profile from OrcaSlicer.
/// Contains quality/speed settings like layer height, infill density, print speeds, etc.
/// Machine-specific hardware settings are stored in the separate MachineProfile entity, and
/// dedicated FilamentProfile records remain the source of truth for material catalogs. This
/// entity also carries a simple <see cref="Material"/>/<see cref="NozzleTemperature"/>/
/// <see cref="BedTemperature"/> default for the ad-hoc, user-created profile flow
/// (<c>POST /api/slicer/profiles</c>), where the caller does not necessarily reference a
/// FilamentProfile.
/// </summary>
public class ProcessProfile
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public SlicerType SlicerType { get; set; }

    /// <summary>Soft reference to catalog PrinterModel (no FK constraint).</summary>
    public Guid? PrinterModelId { get; set; }

    /// <summary>Soft reference to a specific printer instance (no FK constraint).</summary>
    public Guid? SpecificPrinterId { get; set; }

    public double LayerHeight { get; set; } = 0.2; // in mm

    public int InfillPercentage { get; set; } = 20; // 0-100%

    public double PrintSpeed { get; set; } = 50; // mm/s

    public bool EnableSupports { get; set; }

    /// <summary>Nozzle temperature in °C for this process profile's default material.</summary>
    public int NozzleTemperature { get; set; } = 210;

    /// <summary>Bed temperature in °C for this process profile's default material.</summary>
    public int BedTemperature { get; set; } = 60;

    /// <summary>Default material name (e.g. "PLA", "PETG") associated with this process profile.</summary>
    public string Material { get; set; } = "PLA";

    public ProfileQuality Quality { get; set; } = ProfileQuality.Standard;

    public string? AdvancedSettings { get; set; } // JSON object with additional slicer-specific settings

    /// <summary>
    /// Version of the slicer this profile is for (e.g., "1.7.0", "2.0.0").
    /// Extracted from the profile metadata during import.
    /// </summary>
    public string? SlicerVersion { get; set; }

    public string? SlicerDistribution { get; set; }

    public string? ProfileFormat { get; set; }

    /// <summary>
    /// Raw slicer profile JSON as imported from OrcaSlicer / PrusaSlicer (sanitized but otherwise unchanged).
    /// </summary>
    public string? RawJson { get; set; }

    /// <summary>
    /// Extracted settings as key-value pairs for all properties in the raw JSON.
    /// Used for quick display and NewSliceJob page configuration without parsing full RawJson.
    /// </summary>
    public string? SettingsJson { get; set; }

    /// <summary>
    /// Stable hash (SHA256) of RawJson used for deduplication and quick matching on import.
    /// </summary>
    public string? Hash { get; set; }

    /// <summary>
    /// Comma-separated list of compatible machine profile names.
    /// Extracted from OrcaSlicer's compatible_printers array during import.
    /// </summary>
    public string? CompatiblePrinters { get; set; }

    public bool IsDefault { get; set; }

    public bool IsPublic { get; set; } = true;

    /// <summary>
    /// Indicates profile shipped by system seeding (immutable for regular users).
    /// </summary>
    public bool IsSystem { get; set; }

    /// <summary>Soft reference to the user who created this profile (no FK constraint).</summary>
    public Guid? CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
