using System.Security.Cryptography;
using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Farm.HostUpdate.Cli;

/// <summary>
/// Records an offline bundle that <c>offline-update-bundle.mjs import</c> has already verified in
/// the same durable, anchored replay store the online scheduler uses (issue #3064). It binds the
/// staged manifest bytes to the verification record by digest, requires the requested channel to
/// equal both the signed manifest and the host's standing policy, and takes the execution lock so
/// it never races a live executor. It installs nothing and never authorizes rollout.
/// </summary>
internal static class HostUpdateOfflineAdmission
{
    internal const string ManifestName = "update-manifest.json";
    internal const string VerificationName = "offline-bundle-verification.json";
    internal const string VerifiedDecision = "verified-not-installable";
    private const long MaxStagedFileBytes = 1024 * 1024;

    public static async Task<int> RunAsync(
        IServiceProvider provider,
        IConfiguration configuration,
        HostUpdateCliArguments args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        HostStateOptions? hostState;
        try
        {
            hostState = configuration.GetSection(HostStateOptions.SectionName).Get<HostStateOptions>();
        }
        catch (InvalidOperationException)
        {
            return await FailAsync(output, args, HostUpdateCliExitCodes.ConfigurationUnproven, "host_state_configuration_invalid").ConfigureAwait(false);
        }

        if (hostState is null || !hostState.Enabled || string.IsNullOrWhiteSpace(hostState.RootPath))
        {
            return await FailAsync(output, args, HostUpdateCliExitCodes.ConfigurationUnproven, "host_state_not_enabled").ConfigureAwait(false);
        }

        HostUpdatePolicyObservation policy = HostUpdateRecoveryDrift.ReadPolicy(configuration);
        if (!policy.Available)
        {
            return await FailAsync(output, args, HostUpdateCliExitCodes.ConfigurationUnproven, "policy_unavailable:" + policy.Error).ConfigureAwait(false);
        }

        if (!string.Equals(policy.Channel, args.Channel, StringComparison.Ordinal))
        {
            return await FailAsync(output, args, HostUpdateCliExitCodes.Refused, "channel_mismatch_policy").ConfigureAwait(false);
        }

        if (!TryReadStaged(args.Staging!, args.Channel!, out VerifiedHostUpdateCandidate? candidate, out string? stagingError))
        {
            return await FailAsync(output, args, HostUpdateCliExitCodes.Refused, stagingError!).ConfigureAwait(false);
        }

        IHostUpdateExecutionLease? lease = HostUpdateCli.TryAcquireLock(provider);
        if (lease is null)
        {
            return await FailAsync(output, args, HostUpdateCliExitCodes.LockHeld, "host_update_lock_held").ConfigureAwait(false);
        }

        using (lease)
        {
            HostStatePath paths;
            try
            {
                paths = new HostStatePath(Options.Create(hostState));
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException
                or System.Security.SecurityException or InvalidOperationException)
            {
                return await FailAsync(output, args, HostUpdateCliExitCodes.ConfigurationUnproven, "host_state_unverifiable:" + exception.GetType().Name).ConfigureAwait(false);
            }

            HostUpdateReplayDecision decision;
            try
            {
                using var anchor = new FileHostUpdateReplayAnchor(paths);
                using var store = new FileHostUpdateReplayStore(paths.Root, anchor);
                decision = await store.DecideAsync(candidate!, HostUpdateReplayIntent.Import, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (HostUpdateCli.IsStateFailure(exception))
            {
                return await HostUpdateCli.EmitAsync(output, args.Json, HostUpdateCliExitCodes.StateUnreadable,
                    new HostUpdateCli.CliFailure(HostUpdateCli.StateFailureCode(exception), [])).ConfigureAwait(false);
            }

            bool admitted = decision.Disposition == HostUpdateReplayDisposition.Imported ||
                (decision.Disposition == HostUpdateReplayDisposition.Accepted && decision.Reused);
            string? reason = admitted ? null : "replay_" + decision.Disposition.ToString().ToLowerInvariant();
            var report = new OfflineAdmissionReport(
                admitted ? "admitted" : "refused",
                reason,
                candidate!.ReleaseId,
                candidate.Channel,
                candidate.Sequence,
                candidate.ManifestDigest,
                decision.Disposition,
                decision.CorrelationId,
                decision.Reused);
            return await HostUpdateCli.EmitAsync(output, args.Json, admitted ? HostUpdateCliExitCodes.Success : HostUpdateCliExitCodes.Refused, report).ConfigureAwait(false);
        }
    }

    internal static bool TryReadStaged(string staging, string channel, out VerifiedHostUpdateCandidate? candidate, out string? error)
    {
        candidate = null;
        if (!TryReadFile(staging, ManifestName, out byte[]? manifestBytes, out error) ||
            !TryReadFile(staging, VerificationName, out byte[]? recordBytes, out error))
        {
            return false;
        }

        SignedUpdateManifest manifest;
        try
        {
            manifest = SignedUpdateManifestValidator.Parse(System.Text.Encoding.UTF8.GetString(manifestBytes!));
        }
        catch (JsonException)
        {
            error = "staging_manifest_invalid";
            return false;
        }

        if (!SignedUpdateManifestValidator.Validate(manifest).IsValid)
        {
            error = "staging_manifest_invalid";
            return false;
        }

        string digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(manifestBytes!));
        try
        {
            using JsonDocument record = JsonDocument.Parse(recordBytes);
            JsonElement root = record.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schema", out JsonElement schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != 1 ||
                !IsString(root, "decision", VerifiedDecision) ||
                !IsString(root, "manifestDigest", digest) ||
                !root.TryGetProperty("release", out JsonElement release) || release.ValueKind != JsonValueKind.Object ||
                !IsString(release, "channel", manifest.Channel) ||
                !IsString(release, "version", manifest.Version))
            {
                error = "staging_verification_mismatch";
                return false;
            }
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            error = "staging_verification_invalid";
            return false;
        }

        if (!string.Equals(manifest.Channel, channel, StringComparison.Ordinal))
        {
            error = "channel_mismatch_manifest";
            return false;
        }

        IReadOnlyDictionary<string, string> platforms = manifest.PlatformDigests;
        candidate = new VerifiedHostUpdateCandidate(
            $"{manifest.Channel}:{manifest.Version}",
            manifest.SourceCommit,
            manifest.Sequence,
            digest,
            manifest.Channel,
            CryptographicallyVerified: true,
            CompatibilityReady: true,
            InstallationAvailable: true,
            SafetyPassed: true,
            MaintenanceWindowOpen: true,
            IsNewer: true,
            new HostUpdatePlatformDigests(
                platforms.GetValueOrDefault("api", string.Empty),
                platforms.GetValueOrDefault("frontend", string.Empty),
                platforms.GetValueOrDefault("slicer-host", string.Empty),
                platforms.GetValueOrDefault("printer-discovery", string.Empty),
                platforms.GetValueOrDefault("orcaslicer-worker", string.Empty),
                platforms.GetValueOrDefault("monolith", string.Empty)));
        error = null;
        return true;
    }

    private static bool IsString(JsonElement element, string name, string expected) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    private static bool TryReadFile(string staging, string name, out byte[]? bytes, out string? error)
    {
        bytes = null;
        error = "staging_unreadable";
        try
        {
            var directory = new DirectoryInfo(staging);
            if (!directory.Exists || directory.LinkTarget is not null || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                error = "staging_not_directory";
                return false;
            }

            string path = Path.Join(directory.FullName, name);
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var opened = new FileInfo(path);
            if (opened.LinkTarget is not null || opened.Attributes.HasFlag(FileAttributes.ReparsePoint) || stream.Length > MaxStagedFileBytes)
            {
                error = "staging_file_invalid:" + name;
                return false;
            }

            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = exception is FileNotFoundException ? "staging_missing:" + name : "staging_unreadable";
            return false;
        }
    }

    private static Task<int> FailAsync(TextWriter output, HostUpdateCliArguments args, int exitCode, string code) =>
        HostUpdateCli.EmitAsync(output, args.Json, exitCode, new HostUpdateCli.CliFailure(code, []));

    private sealed record OfflineAdmissionReport(
        string Decision,
        string? Reason,
        string ReleaseId,
        string Channel,
        long Sequence,
        string ManifestDigest,
        HostUpdateReplayDisposition Disposition,
        string CorrelationId,
        bool Reused);
}
