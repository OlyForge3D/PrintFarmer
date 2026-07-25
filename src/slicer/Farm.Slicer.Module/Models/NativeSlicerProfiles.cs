using System.Security.Cryptography;
using System.Text;

namespace Farm.Slicer.Module.Models;

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
    /// <summary>
    /// Builds a profile set from persisted job columns, filling in any digest the job did not record.
    /// </summary>
    /// <param name="machineJson">Native machine profile JSON.</param>
    /// <param name="processJson">Native process profile JSON.</param>
    /// <param name="filamentJson">Native filament profile JSON.</param>
    /// <param name="machineSha256">Recorded machine digest, if any.</param>
    /// <param name="processSha256">Recorded process digest, if any.</param>
    /// <param name="filamentSha256">Recorded filament digest, if any.</param>
    /// <returns>The profile set, or <see langword="null"/> when any document is missing.</returns>
    public static NativeSlicerProfiles? FromJob(
        string? machineJson,
        string? processJson,
        string? filamentJson,
        string? machineSha256,
        string? processSha256,
        string? filamentSha256)
    {
        if (string.IsNullOrWhiteSpace(machineJson) ||
            string.IsNullOrWhiteSpace(processJson) ||
            string.IsNullOrWhiteSpace(filamentJson))
        {
            return null;
        }

        return new NativeSlicerProfiles(
            machineJson,
            processJson,
            filamentJson,
            Coalesce(machineSha256, machineJson),
            Coalesce(processSha256, processJson),
            Coalesce(filamentSha256, filamentJson));
    }

    /// <summary>Computes the uppercase hexadecimal SHA-256 of a UTF-8 payload.</summary>
    /// <param name="value">The payload to digest.</param>
    /// <returns>The uppercase hexadecimal digest.</returns>
    public static string ComputeSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)));

    private static string Coalesce(string? recorded, string content) =>
        string.IsNullOrWhiteSpace(recorded) ? ComputeSha256(content) : recorded.Trim();
}
