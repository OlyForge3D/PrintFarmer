using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Farm.Slicer.Module.Domain;

namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Requests creation of a farm-wide OrcaSlicer profile family from a stock machine model.
/// </summary>
public sealed class CloneProfileFamilyRequestDto
{
    [Required]
    [MaxLength(256)]
    public string FamilyName { get; set; } = string.Empty;

    public Guid TargetPrinterModelId { get; set; }

    [Required]
    [MaxLength(128)]
    public string SourceManufacturer { get; set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string SourceMachineModelName { get; set; } = string.Empty;

    public IReadOnlyList<double> NozzleDiameters { get; set; } = [];

    public Dictionary<string, JsonElement> FamilyOverrides { get; set; } = new(StringComparer.Ordinal);

    [MaxLength(32)]
    public string? SlicerEngineVersion { get; set; }

    [MaxLength(64)]
    public string SlicerDistribution { get; set; } = "OrcaSlicer";
}

/// <summary>
/// Describes a machine variant created as part of a custom family.
/// </summary>
public sealed record ProfileFamilyMachineVariantDto(
    Guid Id,
    string Name,
    double NozzleDiameter,
    string SourceSystemPresetName);

/// <summary>
/// Result returned after a profile family is persisted, rendered, and installed.
/// </summary>
public sealed record CloneProfileFamilyResponseDto(
    Guid FamilyId,
    string FamilyName,
    Guid TargetPrinterModelId,
    ProfileFamilyRenderStatus RenderStatus,
    DateTime? LastRenderedAt,
    IReadOnlyList<ProfileFamilyMachineVariantDto> MachineProfiles,
    int ProcessProfileCount,
    int FilamentProfileCount);

/// <summary>
/// One variant row of a persisted profile family, as surfaced by list/get.
/// </summary>
/// <param name="MachineProfileId">Identity of the derived <c>MachineProfile</c> variant row.</param>
/// <param name="Name">Variant display name, e.g. <c>"Sovol SV08 0.4 nozzle"</c>.</param>
/// <param name="NozzleDiameter">
/// Nozzle diameter in millimetres. This is not a persisted column; it is recovered from the
/// variant name suffix (see <c>ProfileFamilyRenderer.BuildMachineName</c>). <see langword="null"/>
/// when it cannot be parsed — never a fabricated <c>0</c>.
/// </param>
/// <param name="SourceSystemPresetName">The pinned upstream preset the variant was cloned from.</param>
public sealed record ProfileFamilyVariantSummaryDto(
    Guid MachineProfileId,
    string Name,
    double? NozzleDiameter,
    string? SourceSystemPresetName);

/// <summary>
/// Read model describing one persisted custom OrcaSlicer profile family, returned by the
/// list and get-by-id endpoints. All wire members are camelCase and enums serialize as strings.
/// </summary>
/// <param name="FamilyId">Family (machine model profile) identity.</param>
/// <param name="FamilyName">Family display name.</param>
/// <param name="TargetPrinterModelId">Soft reference to the bound catalog printer model.</param>
/// <param name="RenderStatus">Health of the derived worker bundle.</param>
/// <param name="LastRenderedAt">Most recent successful render timestamp, if any.</param>
/// <param name="RenderedForOrcaVersion">OrcaSlicer version the current bundle was rendered for.</param>
/// <param name="SourceManufacturer">
/// Deliberately <see langword="null"/>: the source manufacturer is not persisted (the family's
/// <c>Manufacturer</c> column is the literal <c>"Custom"</c>) and cannot be recovered without a
/// schema column, which is out of scope for this slice (no migration). Use
/// <paramref name="SourceMachineModelName"/> instead.
/// </param>
/// <param name="SourceMachineModelName">Exact upstream machine-model name the family was cloned from.</param>
/// <param name="SlicerDistribution">Distribution owning the pinned source preset (normally OrcaSlicer).</param>
/// <param name="Variants">The derived nozzle variants.</param>
/// <param name="ProcessProfileCount">
/// Deliberately <see langword="null"/>: derived process-profile counts are produced only by the
/// renderer at create time and live solely inside the worker bundle — they are not persisted.
/// Reporting them here would force a worker round-trip on a read endpoint that must degrade, so a
/// nullable value communicates "not tracked post-render" rather than a fabricated <c>0</c>.
/// </param>
/// <param name="FilamentProfileCount">
/// Deliberately <see langword="null"/> for the same reason as <paramref name="ProcessProfileCount"/>.
/// </param>
public sealed record ProfileFamilySummaryDto(
    Guid FamilyId,
    string FamilyName,
    Guid? TargetPrinterModelId,
    ProfileFamilyRenderStatus RenderStatus,
    DateTime? LastRenderedAt,
    string? RenderedForOrcaVersion,
    string? SourceManufacturer,
    string? SourceMachineModelName,
    string? SlicerDistribution,
    IReadOnlyList<ProfileFamilyVariantSummaryDto> Variants,
    int? ProcessProfileCount,
    int? FilamentProfileCount);

/// <summary>
/// One generated OrcaSlicer JSON file, relative to the worker's Custom directory.
/// </summary>
public sealed record RenderedProfileFileDto(string RelativePath, string Content);

/// <summary>
/// A family-scoped manifest fragment and its generated source files.
/// The worker atomically installs this as one family-specific custom bundle.
/// </summary>
public sealed record ProfileFamilyBundleDto(
    Guid FamilyId,
    string FamilyName,
    string ManifestJson,
    IReadOnlyList<RenderedProfileFileDto> Files);

/// <summary>
/// Internal description of a rendered machine variant used for persistence.
/// </summary>
public sealed record RenderedMachineVariant(
    string Name,
    double NozzleDiameter,
    string SourceSystemPresetName,
    string OverridesJson);

/// <summary>
/// Complete renderer output used by the family application service.
/// </summary>
public sealed record ProfileFamilyRenderResult(
    ProfileFamilyBundleDto Bundle,
    string CanonicalFamilyOverridesJson,
    IReadOnlyList<RenderedMachineVariant> MachineVariants,
    int ProcessProfileCount,
    int FilamentProfileCount);

/// <summary>
/// Identifies the version-pinned worker selected for catalog reads and bundle installation.
/// </summary>
public sealed record ProfileFamilyWorkerTarget(string BaseUrl, string OrcaVersion);
