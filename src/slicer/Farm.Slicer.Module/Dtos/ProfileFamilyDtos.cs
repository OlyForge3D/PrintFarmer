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
/// Requests an in-place edit of an existing custom OrcaSlicer profile family. Every field is
/// absent-aware: a <see langword="null"/> value means "leave this facet unchanged", so a caller can
/// patch one facet without disturbing the others. Every accepted edit forces a re-render because the
/// derived worker bundle embeds the family name, shared overrides, and variant set.
/// </summary>
public sealed class EditProfileFamilyRequestDto
{
    /// <summary>
    /// New family display name, or <see langword="null"/> to leave unchanged. A rename re-checks the
    /// same global name/alias collision rules as creation and moves the OrcaSlicer alias from the old
    /// name to the new one. An empty/whitespace value is rejected (<c>400</c>).
    /// </summary>
    [MaxLength(256)]
    public string? Name { get; set; }

    /// <summary>
    /// Replacement family-shared native overrides (<c>printable_area</c>, <c>printable_height</c>, …),
    /// or <see langword="null"/> to leave unchanged. An empty object clears all shared overrides. The
    /// renderer's existing identity-key validation rejects nozzle-specific/identity-bearing keys.
    /// </summary>
    public Dictionary<string, JsonElement>? FamilyOverrides { get; set; }

    /// <summary>
    /// Replacement nozzle-variant set, or <see langword="null"/> to leave the variant set unchanged.
    /// An <em>empty</em> array is an error (<c>400</c>), not "remove all variants": a family must own
    /// at least one variant. Adding a nozzle materialises a new variant; removing one is a scoped
    /// delete that honours the same reference check as family deletion (refused <c>409</c> when a
    /// printer template or a non-terminal slice job still points at the variant being removed).
    /// Surviving variants keep their <c>MachineProfile.Id</c>.
    /// </summary>
    public IReadOnlyList<double>? NozzleDiameters { get; set; }

    /// <summary>
    /// New upstream source machine-model name to re-bind the family to (§5 source re-bind), or
    /// <see langword="null"/> to leave the source binding unchanged. Re-binding re-derives the source
    /// manufacturer from the live worker catalog and re-renders; if the new source cannot be resolved
    /// the edit is rejected <c>422 source_preset_unavailable</c> with an actionable detail and the
    /// family is left untouched.
    /// </summary>
    [MaxLength(256)]
    public string? SourceMachineModelName { get; set; }
}

/// <summary>
/// One family's outcome from a re-render, used by the bulk <c>render-stale</c> response so a single
/// failure never hides the others. On success <paramref name="Code"/> and <paramref name="Detail"/>
/// are <see langword="null"/>; on failure they carry the same <c>{code,detail}</c> envelope the
/// single-family endpoints return.
/// </summary>
/// <param name="FamilyId">Family identity.</param>
/// <param name="FamilyName">Family display name.</param>
/// <param name="RenderStatus">Resulting render status (<c>Healthy</c> on success, else <c>Failed</c>).</param>
/// <param name="Code">Machine-readable failure code, or <see langword="null"/> on success.</param>
/// <param name="Detail">Actionable human-readable failure detail, or <see langword="null"/> on success.</param>
public sealed record ProfileFamilyRenderResultDto(
    Guid FamilyId,
    string FamilyName,
    ProfileFamilyRenderStatus RenderStatus,
    string? Code,
    string? Detail);

/// <summary>
/// Response for the bulk <c>render-stale</c> endpoint. The batch is bounded (a single request only
/// re-renders up to a fixed number of families, each a worker round-trip) so it cannot exceed
/// Kestrel/nginx request timeouts; <paramref name="RemainingCount"/> lets a client drain the queue
/// across successive calls.
/// </summary>
/// <param name="Results">Per-family outcomes for the families processed in this bounded pass.</param>
/// <param name="RemainingCount">
/// Number of Stale/Failed families NOT processed in this pass because the batch cap was reached.
/// <c>0</c> means the queue was fully drained; a positive value means the client should call again.
/// </param>
public sealed record RenderStaleFamiliesResponseDto(
    IReadOnlyList<ProfileFamilyRenderResultDto> Results,
    int RemainingCount);

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
/// Deliberately <see langword="null"/>. It is NOT persisted (the family's <c>Manufacturer</c> column
/// is the literal <c>"Custom"</c>) and the only way to recover it is
/// <c>DeriveSourceManufacturer</c>, which needs the worker's FULL catalog
/// (<c>AllProfilesResponseDto</c>) — a per-read worker round-trip that list/get (gated on the
/// non-admin <c>slicing:submit</c>) must not perform, because that read path has to degrade safely
/// when no worker is online (see the C4 staleness-detection decision). Populating it would directly
/// contradict that constraint, so the recoverable source identity is surfaced via
/// <paramref name="SourceMachineModelName"/> instead. No migration is in scope to persist it.
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
