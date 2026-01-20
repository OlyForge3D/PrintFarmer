using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

// Slicer Profile Management System

/// <summary>
/// Process/Quality profile from OrcaSlicer.
/// Contains quality/speed settings like layer height, infill density, print speeds, etc.
/// Does NOT contain material or machine settings - those are stored in separate FilamentProfile and MachineProfile entities.
/// </summary>
public class ProcessProfile
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public SlicerType SlicerType { get; set; }

    public Guid? PrinterModelId { get; set; }

    public PrinterModel? PrinterModel { get; set; }

    public Guid? SpecificPrinterId { get; set; } // Optional: specific printer instance

    public Printer? SpecificPrinter { get; set; }

    public double LayerHeight { get; set; } = 0.2; // in mm

    public int InfillPercentage { get; set; } = 20; // 0-100%

    public double PrintSpeed { get; set; } = 50; // mm/s

    public bool EnableSupports { get; set; }

    public ProfileQuality Quality { get; set; } = ProfileQuality.Standard;

    public string? AdvancedSettings { get; set; } // JSON object with additional slicer-specific settings

    /// <summary>
    /// Version of the slicer this profile is for (e.g., "1.7.0", "2.0.0").
    /// Extracted from the profile metadata during import.
    /// Null indicates version information was not available in the profile.
    /// Used to ensure profiles are only used with compatible slicer versions.
    /// </summary>
    public string? SlicerVersion { get; set; }

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
    /// Different slicer versions will produce different hashes even for the same profile characteristics,
    /// ensuring version-specific profiles are maintained separately in the database.
    /// </summary>
    public string? Hash { get; set; }

    public bool IsDefault { get; set; }

    public bool IsPublic { get; set; } = true; // Can be used by other users

    /// <summary>
    /// Indicates profile shipped by system seeding (immutable for regular users).
    /// System profiles come from the OrcaSlicer worker service and are version-specific.
    /// </summary>
    public bool IsSystem { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public User? CreatedByUser { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
