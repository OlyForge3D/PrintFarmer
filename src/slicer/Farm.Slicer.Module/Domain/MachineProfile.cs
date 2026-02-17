using System.ComponentModel.DataAnnotations;

namespace Farm.Slicer.Module.Domain;

/// <summary>
/// Machine/Printer profile from OrcaSlicer.
/// Contains printer-specific configuration like bed size, extruders, etc.
/// Stored separately from process and filament profiles as they have no overlap.
/// </summary>
public class MachineProfile
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Manufacturer { get; set; } = string.Empty;

    public string? Description { get; set; }

    public SlicerType SlicerType { get; set; }

    /// <summary>Soft reference to catalog PrinterModel (no FK constraint).</summary>
    public Guid? PrinterModelId { get; set; }

    /// <summary>
    /// FK to the MachineModelProfile this profile inherits from.
    /// E.g., "Sovol SV08 0.4 nozzle" inherits from "Sovol SV08".
    /// </summary>
    public Guid? MachineModelProfileId { get; set; }

    /// <summary>
    /// Navigation property to the parent machine model profile (slicer-internal relationship).
    /// </summary>
    public MachineModelProfile? MachineModelProfile { get; set; }

    public string? RawJson { get; set; }

    public string? SettingsJson { get; set; }

    public string? Hash { get; set; }

    public bool IsSystem { get; set; }

    public bool IsDefault { get; set; }

    public bool IsPublic { get; set; } = true;

    public string? SlicerVersion { get; set; }

    /// <summary>Soft reference to the user who created this profile (no FK constraint).</summary>
    public Guid? CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
