using System.ComponentModel.DataAnnotations;

namespace Farm.Slicer.Module.Domain;

/// <summary>
/// Machine model profile from OrcaSlicer's machine_model_list.
/// These are base/template profiles that define the printer model (e.g., "Sovol SV08")
/// and are NOT directly instantiatable - they serve as parents for actual machine profiles
/// with specific nozzle sizes (e.g., "Sovol SV08 0.4 nozzle").
/// </summary>
/// <remarks>
/// OrcaSlicer bundles have two distinct lists:
/// - machine_model_list: Base printer models (stored here)
/// - machine_list: Nozzle variant profiles (stored in MachineProfiles)
///
/// The machine profiles in machine_list inherit from these base models via the "inherits" field.
/// </remarks>
public class MachineModelProfile
{
    public Guid Id { get; set; }

    /// <summary>
    /// The model name from OrcaSlicer (e.g., "Sovol SV08", "Prusa MK4").
    /// </summary>
    [Required]
    [MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The manufacturer name (e.g., "Sovol", "Prusa").
    /// </summary>
    [Required]
    [MaxLength(128)]
    public string Manufacturer { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string? Description { get; set; }

    public SlicerType SlicerType { get; set; }

    /// <summary>Soft reference to catalog PrinterModel (no FK constraint).</summary>
    public Guid? PrinterModelId { get; set; }

    public string? RawJson { get; set; }

    [MaxLength(64)]
    public string? Hash { get; set; }

    public bool IsSystem { get; set; }

    public bool IsPublic { get; set; }

    [MaxLength(32)]
    public string? SlicerVersion { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Navigation property for machine profiles that inherit from this model (slicer-internal relationship).
    /// </summary>
    public ICollection<MachineProfile> MachineProfiles { get; set; } = [];
}
