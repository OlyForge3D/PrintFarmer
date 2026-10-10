using System.Security.Cryptography;
using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Farm.HostUpdate.Cli;

/// <summary>
/// Records an offline bundle that <c>offline-update-bundle.mjs import</c> has already verified in
/// the same durable, anchored replay store the online scheduler uses (issue #3064). It never
/// trusts the mutable staging directory: it re-verifies the exact staged manifest bytes offline
/// with Cosign against the operator's trusted root and the channel's pinned release identity,
/// binds them to the verification record by digest, requires the requested channel to
/// equal both the signed manifest and the host's standing policy, and takes the execution lock so
/// it never races a live executor. It installs nothing and never authorizes rollout.
/// </summary>
internal static class HostUpdateOfflineAdmission
{
    internal const string ManifestName = "update-manifest.json";
    internal const string SignatureName = "update-manifest.sigstore.json";
    internal const string VerificationName = "offline-bundle-verification.json";
    internal const string VerifiedDecision = "verified-not-installable";
    private const long MaxStagedFileBytes = 1024 * 1024;
    private static readonly TimeSpan SignatureTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Creates the Cosign verifier; tests substitute a fake to avoid a real Sigstore bundle.</summary>
    internal static Func<CosignVerifierOptions, ISignedReleaseVerifier> VerifierFactory { get; set; } =
        options => new ProcessCosignVerifier(options);

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

        if (!TryReadStaged(args.Staging!, args.Channel!, out StagedRelease? staged, out string? stagingError))
        {
            return await FailAsync(output, args, HostUpdateCliExitCodes.Refused, stagingError!).ConfigureAwait(false);
        }

        // The staging directory and its verification record are mutable, so they never vouch for
        // authenticity: the exact staged manifest bytes are re-verified here against the
        // operator's trusted root and the channel's pinned release identity (#3077).
        string? signatureError = await VerifySignatureAsync(args, staged!, cancellationToken).ConfigureAwait(false);
        if (signatureError is not null)
        {
            return await FailAsync(output, args, HostUpdateCliExitCodes.Refused, signatureError).ConfigureAwait(false);
        }

        VerifiedHostUpdateCandidate candidate = staged!.Candidate;

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
                decision = await store.DecideAsync(candidate, HostUpdateReplayIntent.Import, cancellationToken).ConfigureAwait(false);
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
                candidate.ReleaseId,
                candidate.Channel,
                candidate.Sequence,
                candidate.ManifestDigest,
                decision.Disposition,
                decision.CorrelationId,
                decision.Reused);
            return await HostUpdateCli.EmitAsync(output, args.Json, admitted ? HostUpdateCliExitCodes.Success : HostUpdateCliExitCodes.Refused, report).ConfigureAwait(false);
        }
    }

    internal static bool TryReadStaged(string staging, string channel, out StagedRelease? staged, out string? error)
    {
        staged = null;
        if (!TryReadFile(staging, ManifestName, out byte[]? manifestBytes, out error) ||
            !TryReadFile(staging, SignatureName, out byte[]? signatureBytes, out error) ||
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

        string manifestPlatform = manifest.Platforms.Count > 0 ? manifest.Platforms[0] : string.Empty;
        string Preloaded(string serviceId) => PreloadedDigest(manifest, serviceId, manifestPlatform) ?? string.Empty;
        var candidate = new VerifiedHostUpdateCandidate(
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
                Preloaded("api"),
                Preloaded("frontend"),
                Preloaded("slicer-host"),
                Preloaded("printer-discovery"),
                Preloaded("orcaslicer-worker"),
                Preloaded("monolith")),
            HostPlatform: manifestPlatform);
        staged = new StagedRelease(candidate, manifest, manifestBytes!, signatureBytes!, Path.GetFullPath(staging));
        error = null;
        return true;
    }

    internal static async Task<string?> VerifySignatureAsync(HostUpdateCliArguments args, StagedRelease staged, CancellationToken cancellationToken)
    {
        string trustedRoot;
        string stagingPath;
        try
        {
            var root = new FileInfo(args.TrustedRoot!);
            if (!root.Exists || root.LinkTarget is not null || root.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return "trusted_root_invalid";
            }

            // Compare physical locations: a linked or junctioned parent could otherwise alias the
            // mutable staging directory under an unrelated-looking path (#3077).
            trustedRoot = ResolvePhysicalPath(root.FullName);
            stagingPath = ResolvePhysicalPath(staged.StagingPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return "trusted_root_invalid";
        }

        if (IsWithin(stagingPath, trustedRoot))
        {
            return "trusted_root_inside_staging";
        }

        ISignedReleaseVerifier verifier = VerifierFactory(new CosignVerifierOptions(args.Cosign ?? "cosign", SignatureTimeout, TrustedRootPath: trustedRoot));
        bool verified = await verifier.VerifyAsync(staged.ManifestBytes, staged.SignatureBytes,
            HostUpdateTrustRoot.CertificateIdentity(staged.Candidate.Channel), cancellationToken).ConfigureAwait(false);
        return verified ? null : "staging_signature_unverified";
    }

    private const int MaxLinkHops = 40;

    /// <summary>Returns <paramref name="path"/> with every symbolic link and junction component resolved.</summary>
    internal static string ResolvePhysicalPath(string path) => ResolvePhysicalPath(Path.GetFullPath(path), 0);

    private static string ResolvePhysicalPath(string fullPath, int hops)
    {
        string root = Path.GetPathRoot(fullPath) ?? throw new IOException("path_has_no_root");
        string current = root;
        string[] segments = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments)
        {
            current = Path.Join(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is null)
            {
                continue;
            }

            if (++hops > MaxLinkHops)
            {
                throw new IOException("too_many_links");
            }

            string target = Path.GetFullPath(info.LinkTarget, Path.GetDirectoryName(current) ?? root);
            current = ResolvePhysicalPath(target, hops);
        }

        return current;
    }

    private static bool IsWithin(string directory, string path)
    {
        string relative = Path.GetRelativePath(directory, path);
        return relative == "." || !(relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative));
    }

    internal static bool IsString(JsonElement element, string name, string expected) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    internal static bool TryReadFile(string staging, string name, out byte[]? bytes, out string? error)
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

    internal static string PlatformKey(string serviceId, string platform) => $"{serviceId}/{platform}";

    private static readonly string[] PreloadedServiceIds = ["api", "frontend", "slicer-host", "printer-discovery", "orcaslicer-worker", "monolith"];

    private static bool IsCanonicalDigest(string? value) =>
        value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(Uri.IsHexDigit);

    /// <summary>
    /// The signed image pin a preloaded (offline) host can prove locally for one service on one
    /// platform. Offline bundles are OCI layouts rooted at the signed multi-platform index, so
    /// <c>docker load</c> records <c>RepoDigests = repo@index</c>; the per-platform child digest is
    /// never resolvable locally. The index digest is signed (it is the <c>@</c> pin of
    /// <see cref="SignedUpdateService.Image"/>) and content-addresses its platform children, and
    /// local inspection separately proves the loaded image's OS/architecture. The signed child for
    /// <paramref name="platform"/> must still exist, proving the platform was signed for this service.
    /// In <see cref="HostUpdateImageSourceMode.PreloadedLocal"/> requests,
    /// <see cref="HostUpdateExecutionTarget.ChildDigest"/> therefore carries this index pin.
    /// Returns <see langword="null"/> when either signed digest is missing or non-canonical.
    /// </summary>
    internal static string? PreloadedDigest(SignedUpdateManifest manifest, string serviceId, string platform)
    {
        if (!manifest.PlatformDigests.TryGetValue(PlatformKey(serviceId, platform), out string? child) ||
            !IsCanonicalDigest(child))
        {
            return null;
        }

        SignedUpdateService? service = manifest.Services.FirstOrDefault(s => string.Equals(s.Id, serviceId, StringComparison.Ordinal));
        int at = service?.Image.LastIndexOf('@') ?? -1;
        string index = at < 0 ? string.Empty : service!.Image[(at + 1)..];
        return IsCanonicalDigest(index) ? index : null;
    }

    /// <summary>
    /// The complete preloaded digest set for <paramref name="platform"/>. Throws
    /// <c>platform_not_in_manifest</c>, <c>image_set_incomplete:{service}</c>, or
    /// <c>image_set_mixed_or_incomplete</c> as <see cref="InvalidOperationException"/>.
    /// </summary>
    internal static HostUpdatePlatformDigests PreloadedDigests(SignedUpdateManifest manifest, string platform)
    {
        if (!manifest.Platforms.Contains(platform, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("platform_not_in_manifest");
        }

        string Digest(string serviceId) =>
            PreloadedDigest(manifest, serviceId, platform) ?? throw new InvalidOperationException("image_set_incomplete:" + serviceId);

        var digests = new HostUpdatePlatformDigests(
            Digest(PreloadedServiceIds[0]),
            Digest(PreloadedServiceIds[1]),
            Digest(PreloadedServiceIds[2]),
            Digest(PreloadedServiceIds[3]),
            Digest(PreloadedServiceIds[4]),
            Digest(PreloadedServiceIds[5]));
        if (!digests.IsComplete)
        {
            throw new InvalidOperationException("image_set_mixed_or_incomplete");
        }

        return digests;
    }

    internal sealed record StagedRelease(VerifiedHostUpdateCandidate Candidate, SignedUpdateManifest Manifest, byte[] ManifestBytes, byte[] SignatureBytes, string StagingPath);

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
