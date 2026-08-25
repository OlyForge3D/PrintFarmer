using System.Text.Json.Serialization;

namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// The kind of slicer profile being resolved via <c>resolve-for-model</c> (#2004).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProfileResolutionType
{
    Machine,
    Process,
    Filament
}

#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// Request to resolve a catalog profile's identity for a printer model, importing it from the
/// OrcaSlicer worker catalog on the caller's behalf if it has never been imported into PrintFarmer's
/// database (#2004). Unlike <see cref="SelectiveProfileImportRequest"/>, this does not require
/// admin authority: a caller holding only calibration scopes can resolve a single named profile
/// without any prior admin action.
/// </summary>
public class ResolveProfileForModelRequest
{
    /// <summary>The kind of profile to resolve.</summary>
    public ProfileResolutionType ProfileType { get; set; }

    /// <summary>
    /// The profile name as reported by the catalog read endpoint
    /// (<c>GET /api/slicer/profiles/machine/for-model/{modelId}</c> and its process/filament
    /// counterparts), which carries no Guid for profiles that have never been imported.
    /// </summary>
    public string ProfileName { get; set; } = string.Empty;
}

/// <summary>
/// Result of resolving (and, if needed, auto-importing) a catalog profile's identity for a
/// printer model (#2004).
/// </summary>
public class ResolveProfileForModelResultDto
{
    /// <summary>The printer model ID the profile was resolved for.</summary>
    public Guid PrinterModelId { get; set; }

    /// <summary>The kind of profile that was resolved.</summary>
    public ProfileResolutionType ProfileType { get; set; }

    /// <summary>The profile name that was resolved.</summary>
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>
    /// The resolved profile's database identity, usable directly by calibration/slicing endpoints.
    /// Null when resolution failed; see <see cref="Error"/>.
    /// </summary>
    public Guid? ProfileId { get; set; }

    /// <summary>
    /// True when the profile had never been imported and was auto-imported from the OrcaSlicer
    /// worker catalog by this call. False when it already existed in PrintFarmer's database.
    /// </summary>
    public bool Imported { get; set; }

    /// <summary>Optional error message when resolution failed (<see cref="ProfileId"/> is null).</summary>
    public string? Error { get; set; }
}

#pragma warning restore SA1402
