using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

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

    public Guid? PrinterModelId { get; set; }

    public PrinterModel? PrinterModel { get; set; }

    /// <summary>
    /// FK to the MachineModelProfile this profile inherits from.
    /// E.g., "Sovol SV08 0.4 nozzle" inherits from "Sovol SV08".
    /// </summary>
    public Guid? MachineModelProfileId { get; set; }

    /// <summary>
    /// Navigation property to the parent machine model profile.
    /// </summary>
    public MachineModelProfile? MachineModelProfile { get; set; }

    public string? RawJson { get; set; } // Full profile JSON

    public string? SettingsJson { get; set; } // Extracted settings as key-value pairs

    public string? Hash { get; set; } // SHA256 for deduplication

    public bool IsSystem { get; set; } // From OrcaSlicer system profiles

    public bool IsDefault { get; set; } // Can be set as default machine

    public bool IsPublic { get; set; } = true; // Can be used by other users

    public string? SlicerVersion { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public User? CreatedByUser { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
