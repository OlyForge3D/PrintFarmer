using System.ComponentModel.DataAnnotations;
using Farm.Infrastructure.Domain;

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
public class MachineModelProfile : IRevisionedEntity
{
    private string _name = string.Empty;

    public Guid Id { get; set; }

    /// <inheritdoc/>
    public long Revision { get; set; } = 1;

    /// <summary>
    /// The model name from OrcaSlicer (e.g., "Sovol SV08", "Prusa MK4").
    /// </summary>
    [Required]
    [MaxLength(256)]
    public string Name
    {
        get => _name;
        set
        {
            _name = value ?? string.Empty;
            NameNormalized = NormalizeNameKey(_name);
        }
    }

    /// <summary>
    /// Trimmed, Unicode case-folded model-name key used to enforce portable uniqueness.
    /// </summary>
    [Required]
    [MaxLength(256)]
    public string NameNormalized { get; private set; } = string.Empty;

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

    /// <summary>Distribution that owns the pinned source preset, normally OrcaSlicer.</summary>
    [MaxLength(64)]
    public string? SlicerDistribution { get; set; }

    /// <summary>Exact source machine_model name used to generate this family.</summary>
    [MaxLength(256)]
    public string? SourceMachineModelName { get; set; }

    /// <summary>Canonical JSON containing the family-shared native OrcaSlicer overrides.</summary>
    public string? FamilyOverridesJson { get; set; }

    /// <summary>Soft reference to the user who created this farm-wide family.</summary>
    public Guid? CreatedByUserId { get; set; }

    /// <summary>Most recent successful render timestamp.</summary>
    public DateTime? LastRenderedAt { get; set; }

    /// <summary>OrcaSlicer version for which the current derived bundle was rendered.</summary>
    [MaxLength(32)]
    public string? RenderedForOrcaVersion { get; set; }

    /// <summary>Health of the derived worker bundle.</summary>
    public ProfileFamilyRenderStatus RenderStatus { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Navigation property for machine profiles that inherit from this model (slicer-internal relationship).
    /// </summary>
    public ICollection<MachineProfile> MachineProfiles { get; set; } = [];

    internal bool RefreshNormalizedName()
    {
        string normalizedName = NormalizeNameKey(_name);
        if (string.Equals(NameNormalized, normalizedName, StringComparison.Ordinal))
        {
            return false;
        }

        NameNormalized = normalizedName;
        return true;
    }

    /// <summary>
    /// Builds a provider-independent equality key for persisted profile-family names.
    /// This deliberately does not use CatalogNameNormalizer, which formats display casing
    /// without making differently-cased names equal.
    /// </summary>
    internal static string NormalizeNameKey(string value) =>
        value.Trim().ToUpperInvariant();
}
