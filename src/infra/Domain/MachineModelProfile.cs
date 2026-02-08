using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

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
    /// This matches the "name" field in machine_model_list.
    /// </summary>
    [Required]
    [MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The manufacturer name (e.g., "Sovol", "Prusa").
    /// Derived from the bundle/folder name.
    /// </summary>
    [Required]
    [MaxLength(128)]
    public string Manufacturer { get; set; } = string.Empty;

    /// <summary>
    /// Optional description for the machine model.
    /// </summary>
    [MaxLength(1024)]
    public string? Description { get; set; }

    /// <summary>
    /// The slicer type this profile belongs to.
    /// </summary>
    public SlicerType SlicerType { get; set; }

    /// <summary>
    /// FK to the catalog PrinterModel for linking slicer profiles to business catalog.
    /// Resolved via PrinterModelAliases using the Name field.
    /// </summary>
    public Guid? PrinterModelId { get; set; }

    /// <summary>
    /// Navigation property to the catalog PrinterModel.
    /// </summary>
    public PrinterModel? PrinterModel { get; set; }

    /// <summary>
    /// The full raw JSON from the machine model profile file.
    /// Contains bed size, build volume, default settings, etc.
    /// </summary>
    public string? RawJson { get; set; }

    /// <summary>
    /// SHA256 hash of RawJson for deduplication and change detection.
    /// </summary>
    [MaxLength(64)]
    public string? Hash { get; set; }

    /// <summary>
    /// Whether this is a system profile from OrcaSlicer (vs user-created).
    /// </summary>
    public bool IsSystem { get; set; }

    /// <summary>
    /// Whether this profile is publicly visible to all users.
    /// </summary>
    public bool IsPublic { get; set; }

    /// <summary>
    /// The OrcaSlicer version this profile was imported from.
    /// </summary>
    [MaxLength(32)]
    public string? SlicerVersion { get; set; }

    /// <summary>
    /// When this profile was first imported.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When this profile was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Navigation property for machine profiles that inherit from this model.
    /// </summary>
    public ICollection<MachineProfile> MachineProfiles { get; set; } = [];
}
