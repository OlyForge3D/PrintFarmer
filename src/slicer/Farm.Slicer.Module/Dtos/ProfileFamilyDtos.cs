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
/// One generated OrcaSlicer JSON file, relative to the worker's Custom directory.
/// </summary>
public sealed record RenderedProfileFileDto(string RelativePath, string Content);

/// <summary>
/// A family-scoped manifest fragment and its generated source files.
/// The worker merges this fragment into Custom.json atomically.
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
