using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Farm.Infrastructure.Services.HostUpdates;
using Microsoft.Extensions.Configuration;

namespace Farm.HostUpdate.Cli;

/// <summary>
/// Recovers a failed offline activation to the prior release bundled with the same verified
/// offline bundle, with no network (issue #3082). The staging directory and its verification
/// record are mutable and never vouch for authenticity: the staged target and prior signed
/// manifests are both re-verified offline against the operator's trusted root, the prior set is
/// bound to the record by digest, and the operator's own protected-backup reference must equal
/// the recorded one. Only then does it run the ordinary recovery, gated so the failed request
/// must be the staged target in preloaded-image mode and the installed state must be exactly the
/// authenticated prior set. The recovery engine never pulls in preloaded mode; a database restore
/// owned by an external provider stops as needs-operator before any change.
/// </summary>
internal static partial class HostUpdateOfflineRecovery
{
    internal const string PriorManifestName = "prior-" + HostUpdateOfflineAdmission.ManifestName;
    internal const string PriorSignatureName = "prior-" + HostUpdateOfflineAdmission.SignatureName;
    private const long MaxProtectedBackupBytes = 64 * 1024;
    private static readonly string[] ProtectedBackupFields = ["id", "locationClass", "releaseVersion", "sha256"];
    private static readonly string[] LocationClasses = ["host-local", "attached-volume", "external-storage"];

    public static async Task<int> RunAsync(
        IServiceProvider provider,
        IConfiguration configuration,
        HostUpdateExecutionOptions options,
        HostUpdateCliArguments args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!HostUpdateOfflineAdmission.TryReadStaged(args.Staging!, args.Channel!, out HostUpdateOfflineAdmission.StagedRelease? staged, out string? stagingError))
        {
            return await FailAsync(output, args, stagingError!).ConfigureAwait(false);
        }

        string? signatureError = await HostUpdateOfflineAdmission.VerifySignatureAsync(args, staged!, cancellationToken).ConfigureAwait(false);
        if (signatureError is not null)
        {
            return await FailAsync(output, args, signatureError).ConfigureAwait(false);
        }

        if (!string.Equals(staged!.Candidate.ReleaseId, args.ReleaseId, StringComparison.Ordinal))
        {
            return await FailAsync(output, args, "offline_recovery_release_mismatch").ConfigureAwait(false);
        }

        if (!TryReadPrior(staged, out PriorSet? prior, out string? priorError))
        {
            return await FailAsync(output, args, priorError!).ConfigureAwait(false);
        }

        // The prior manifest bytes are authenticated independently of the mutable record.
        var priorStaged = staged with
        {
            Candidate = staged.Candidate with { Channel = prior!.Manifest.Channel },
            ManifestBytes = prior.ManifestBytes,
            SignatureBytes = prior.SignatureBytes,
        };
        if (await HostUpdateOfflineAdmission.VerifySignatureAsync(args, priorStaged, cancellationToken).ConfigureAwait(false) is not null)
        {
            return await FailAsync(output, args, "prior_recovery_set_unverified").ConfigureAwait(false);
        }

        string? backupError = CheckProtectedBackup(args.ProtectedBackup!, prior);
        if (backupError is not null)
        {
            return await FailAsync(output, args, backupError).ConfigureAwait(false);
        }

        return await HostUpdateCli.RecoverAsync(
            provider,
            configuration,
            options,
            args,
            output,
            cancellationToken,
            (request, installed) => CheckBinding(request, installed, staged, prior)).ConfigureAwait(false);
    }

    internal static string? CheckBinding(
        HostUpdateExecutionRequest request,
        InstalledHostState? installed,
        HostUpdateOfflineAdmission.StagedRelease staged,
        PriorSet prior)
    {
        if (request.ImageSourceMode != HostUpdateImageSourceMode.PreloadedLocal)
        {
            // A registry-mode request would re-apply the prior set by pulling; offline it must not.
            return "offline_recovery_requires_preloaded_request";
        }

        if (!string.Equals(request.ReleaseId, staged.Candidate.ReleaseId, StringComparison.Ordinal) ||
            !string.Equals(request.ManifestDigest, staged.Candidate.ManifestDigest, StringComparison.Ordinal))
        {
            return "offline_recovery_target_mismatch";
        }

        SignedUpdateManifest manifest = prior.Manifest;
        if (installed is null ||
            !string.Equals(installed.ReleaseId, $"{manifest.Channel}:{manifest.Version}", StringComparison.Ordinal) ||
            !string.Equals(installed.ManifestDigest, prior.ManifestDigest, StringComparison.Ordinal) ||
            installed.ServiceDigests.Count == 0)
        {
            return "prior_installed_state_mismatch";
        }

        // The installed state must be exactly the prior set for the same services and platforms the
        // failed target deploys: a missing, extra or re-platformed service is not the prior set.
        Dictionary<string, string> targetPlatforms = request.Targets.ToDictionary(t => t.ServiceId, t => t.Platform, StringComparer.Ordinal);
        if (installed.ServiceDigests.Count != targetPlatforms.Count ||
            (installed.ServicePlatforms is not null && installed.ServicePlatforms.Count != targetPlatforms.Count))
        {
            return "prior_installed_state_mismatch";
        }

        foreach ((string service, string digest) in installed.ServiceDigests)
        {
            if (!targetPlatforms.TryGetValue(service, out string? targetPlatform))
            {
                return "prior_installed_state_mismatch";
            }

            string platform = installed.ServicePlatforms is null ? targetPlatform : installed.ServicePlatforms.GetValueOrDefault(service) ?? string.Empty;
            if (!string.Equals(platform, targetPlatform, StringComparison.Ordinal) ||
                !manifest.PlatformDigests.TryGetValue(HostUpdateOfflineAdmission.PlatformKey(service, platform), out string? expected) ||
                !string.Equals(expected, digest, StringComparison.Ordinal))
            {
                return "prior_installed_state_mismatch";
            }
        }

        return null;
    }

    private static bool TryReadPrior(HostUpdateOfflineAdmission.StagedRelease staged, out PriorSet? prior, out string? error)
    {
        prior = null;
        if (!HostUpdateOfflineAdmission.TryReadFile(staged.StagingPath, HostUpdateOfflineAdmission.VerificationName, out byte[]? recordBytes, out error))
        {
            return false;
        }

        JsonElement recorded;
        try
        {
            using JsonDocument record = JsonDocument.Parse(recordBytes);
            if (!record.RootElement.TryGetProperty("priorRecoverySet", out JsonElement value) || value.ValueKind == JsonValueKind.False)
            {
                error = "prior_recovery_set_missing";
                return false;
            }

            recorded = value.Clone();
        }
        catch (JsonException)
        {
            error = "staging_verification_invalid";
            return false;
        }

        if (recorded.ValueKind != JsonValueKind.Object ||
            !recorded.TryGetProperty("manifestDigest", out JsonElement digestElement) || digestElement.ValueKind != JsonValueKind.String ||
            !recorded.TryGetProperty("release", out JsonElement release) || release.ValueKind != JsonValueKind.Object ||
            !recorded.TryGetProperty("protectedBackup", out JsonElement backup) || !TryReadBackup(backup, out ProtectedBackupReference? recordedBackup))
        {
            error = "prior_recovery_set_invalid";
            return false;
        }

        if (!HostUpdateOfflineAdmission.TryReadFile(staged.StagingPath, PriorManifestName, out byte[]? manifestBytes, out error) ||
            !HostUpdateOfflineAdmission.TryReadFile(staged.StagingPath, PriorSignatureName, out byte[]? signatureBytes, out error))
        {
            return false;
        }

        string manifestDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(manifestBytes!));
        SignedUpdateManifest manifest;
        try
        {
            manifest = SignedUpdateManifestValidator.Parse(System.Text.Encoding.UTF8.GetString(manifestBytes!));
        }
        catch (JsonException)
        {
            error = "prior_recovery_set_mismatch";
            return false;
        }

        if (!SignedUpdateManifestValidator.Validate(manifest).IsValid ||
            !string.Equals(digestElement.GetString(), manifestDigest, StringComparison.Ordinal) ||
            !HostUpdateOfflineAdmission.IsString(release, "channel", manifest.Channel) ||
            !HostUpdateOfflineAdmission.IsString(release, "version", manifest.Version) ||
            !string.Equals(manifest.Channel, staged.Manifest.Channel, StringComparison.Ordinal) ||
            manifest.Sequence >= staged.Manifest.Sequence ||
            !string.Equals(recordedBackup!.ReleaseVersion, manifest.Version, StringComparison.Ordinal))
        {
            error = "prior_recovery_set_mismatch";
            return false;
        }

        prior = new PriorSet(manifest, manifestBytes!, signatureBytes!, manifestDigest, recordedBackup);
        error = null;
        return true;
    }

    private static string? CheckProtectedBackup(string path, PriorSet prior)
    {
        byte[] bytes;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.LinkTarget is not null || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return "protected_backup_invalid";
            }

            using FileStream stream = new(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxProtectedBackupBytes)
            {
                return "protected_backup_invalid";
            }

            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return "protected_backup_invalid";
        }

        ProtectedBackupReference? reference;
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            if (!TryReadBackup(document.RootElement, out reference))
            {
                return "protected_backup_invalid";
            }
        }
        catch (JsonException)
        {
            return "protected_backup_invalid";
        }

        return reference == prior.ProtectedBackup ? null : "protected_backup_mismatch";
    }

    // Identity, checksum and location class only: the closed field set leaves no place for backup
    // contents, credentials or paths (the same contract as offline-update-bundle.mjs).
    private static bool TryReadBackup(JsonElement element, out ProtectedBackupReference? reference)
    {
        reference = null;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(ProtectedBackupFields, StringComparer.Ordinal) ||
            element.EnumerateObject().Any(property => property.Value.ValueKind != JsonValueKind.String))
        {
            return false;
        }

        string id = element.GetProperty("id").GetString()!;
        string sha256 = element.GetProperty("sha256").GetString()!;
        string locationClass = element.GetProperty("locationClass").GetString()!;
        string releaseVersion = element.GetProperty("releaseVersion").GetString()!;
        if (!BackupIdPattern().IsMatch(id) || !Sha256Pattern().IsMatch(sha256) ||
            !LocationClasses.Contains(locationClass, StringComparer.Ordinal) || releaseVersion.Length == 0)
        {
            return false;
        }

        reference = new ProtectedBackupReference(id, sha256, locationClass, releaseVersion);
        return true;
    }

    private static Task<int> FailAsync(TextWriter output, HostUpdateCliArguments args, string code) =>
        HostUpdateCli.EmitAsync(output, args.Json, HostUpdateCliExitCodes.Refused, new HostUpdateCli.CliFailure(code, []));

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex BackupIdPattern();

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    internal sealed record ProtectedBackupReference(string Id, string Sha256, string LocationClass, string ReleaseVersion);

    internal sealed record PriorSet(
        SignedUpdateManifest Manifest,
        byte[] ManifestBytes,
        byte[] SignatureBytes,
        string ManifestDigest,
        ProtectedBackupReference ProtectedBackup);
}
