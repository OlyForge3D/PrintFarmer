using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Farm.Slicer.Module.Models;

/// <summary>An immutable native filament document and its expected SHA-256 digest.</summary>
/// <param name="Json">The exact native filament profile JSON.</param>
/// <param name="Sha256">Expected SHA-256 (hex) of the UTF-8 document.</param>
public sealed record NativeFilamentProfile(string Json, string Sha256);

/// <summary>
/// The exact native slicer profile documents delivered with a claimed job, plus their digests.
/// </summary>
/// <remarks>
/// These are verbatim upstream-Orca JSON documents resolved from the profile store, not serialized
/// CLR DTOs. The worker verifies each digest before writing the file the slicer will consume, so a
/// truncated or substituted document cannot silently reach the slicer.
/// </remarks>
/// <param name="MachineJson">Native machine profile JSON.</param>
/// <param name="ProcessJson">Native process profile JSON.</param>
/// <param name="FilamentJson">Native filament profile JSON.</param>
/// <param name="MachineSha256">Expected SHA-256 (hex) of <paramref name="MachineJson"/>.</param>
/// <param name="ProcessSha256">Expected SHA-256 (hex) of <paramref name="ProcessJson"/>.</param>
/// <param name="FilamentSha256">Expected SHA-256 (hex) of <paramref name="FilamentJson"/>.</param>
public sealed record NativeSlicerProfiles(
    string MachineJson,
    string ProcessJson,
    string FilamentJson,
    string MachineSha256,
    string ProcessSha256,
    string FilamentSha256)
{
    /// <summary>Exact native filament snapshots in extruder order, when explicitly selected.</summary>
    public IReadOnlyList<NativeFilamentProfile>? ExtruderFilaments { get; init; }

    /// <summary>
    /// Builds a profile set from persisted job columns, filling in any digest the job did not record.
    /// </summary>
    /// <param name="machineJson">Native machine profile JSON.</param>
    /// <param name="processJson">Native process profile JSON.</param>
    /// <param name="filamentJson">Native filament profile JSON.</param>
    /// <param name="machineSha256">Recorded machine digest, if any.</param>
    /// <param name="processSha256">Recorded process digest, if any.</param>
    /// <param name="filamentSha256">Recorded filament digest, if any.</param>
    /// <param name="extruderFilamentProfilesJson">Ordered native filament snapshots, if explicitly selected.</param>
    /// <returns>The profile set, or <see langword="null"/> when a legacy job is missing a document.</returns>
    /// <exception cref="JsonException">An explicit snapshot list or document contains invalid JSON.</exception>
    /// <exception cref="InvalidOperationException">Explicit snapshots are empty, malformed or missing required native profiles.</exception>
    public static NativeSlicerProfiles? FromJob(
        string? machineJson,
        string? processJson,
        string? filamentJson,
        string? machineSha256,
        string? processSha256,
        string? filamentSha256,
        string? extruderFilamentProfilesJson = null)
    {
        IReadOnlyList<NativeFilamentProfile>? extruderFilaments = null;
        if (extruderFilamentProfilesJson is not null)
        {
            extruderFilaments = JsonSerializer.Deserialize<List<NativeFilamentProfile>>(
                extruderFilamentProfilesJson, JsonSerializerOptions.Web);
            ValidateExtruderFilaments(extruderFilaments);
        }

        if (string.IsNullOrWhiteSpace(machineJson) ||
            string.IsNullOrWhiteSpace(processJson) ||
            string.IsNullOrWhiteSpace(filamentJson))
        {
            if (extruderFilamentProfilesJson is not null)
            {
                throw new InvalidOperationException("Explicit native filament snapshots require native machine, process and filament profiles.");
            }

            return null;
        }

        return new NativeSlicerProfiles(
            machineJson,
            processJson,
            filamentJson,
            Coalesce(machineSha256, machineJson),
            Coalesce(processSha256, processJson),
            Coalesce(filamentSha256, filamentJson))
        {
            ExtruderFilaments = extruderFilaments
        };
    }

    /// <summary>Rejects empty or malformed explicit snapshots rather than resolving profiles by name.</summary>
    /// <param name="profiles">The ordered explicit snapshots.</param>
    public static void ValidateExtruderFilaments(IReadOnlyList<NativeFilamentProfile>? profiles)
    {
        if (profiles is not { Count: > 0 })
        {
            throw new InvalidOperationException("Explicit native filament snapshots must not be empty.");
        }

        foreach (NativeFilamentProfile profile in profiles)
        {
            if (profile is null || string.IsNullOrWhiteSpace(profile.Json) ||
                string.IsNullOrWhiteSpace(profile.Sha256) ||
                profile.Sha256.Length != 64 || !profile.Sha256.All(char.IsAsciiHexDigit))
            {
                throw new InvalidOperationException("An explicit native filament snapshot is missing its document or SHA-256 digest.");
            }

            using JsonDocument document = JsonDocument.Parse(profile.Json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("An explicit native filament snapshot must contain a JSON object.");
            }
        }
    }

    /// <summary>Computes the uppercase hexadecimal SHA-256 of a UTF-8 payload.</summary>
    /// <param name="value">The payload to digest.</param>
    /// <returns>The uppercase hexadecimal digest.</returns>
    public static string ComputeSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)));

    private static string Coalesce(string? recorded, string content) =>
        string.IsNullOrWhiteSpace(recorded) ? ComputeSha256(content) : recorded.Trim();
}
