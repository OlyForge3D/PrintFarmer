using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// The immutable release identity a pull-delivered approval names (issue #3116). The pull contract
/// (#3115) maps its approval to this shape. None of it is trusted: the daemon re-derives every
/// field from the signed manifest bytes it fetches itself and refuses any difference.
/// </summary>
public sealed record HostUpdateDaemonApprovedRelease(
    string ApprovalId,
    string ReleaseId,
    string Channel,
    long Sequence,
    string ManifestDigest,
    string TrustRoot,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Host-local verification inputs. They come from root-owned host configuration and host policy,
/// never from the API. Both paths must be fully qualified so no search path or working directory
/// can substitute a different Cosign binary or Sigstore trusted root.
/// </summary>
public sealed record HostUpdateDaemonVerificationContext(string HostChannel, string HostPlatform, string TrustedRootPath, string CosignPath);

/// <summary>Untrusted published bytes: the release manifest and its Sigstore bundle.</summary>
public sealed record HostUpdateDaemonSignedArtifacts(byte[] Manifest, byte[] Bundle);

/// <summary>
/// Fetches the published manifest and Sigstore bundle for one release tag. The daemon derives the
/// tag from the approved release id; the API never supplies a URL. Returns <see langword="null"/>
/// when the release or either asset is absent.
/// </summary>
public interface IHostUpdateDaemonReleaseSource
{
    Task<HostUpdateDaemonSignedArtifacts?> FetchAsync(string channel, string tag, CancellationToken cancellationToken);
}

/// <summary>
/// Read-only anti-replay check against the durable, anchored replay store (#2665 high-water marks).
/// Returns <see langword="null"/> when the candidate could still be admitted, otherwise a fixed code.
/// It never advances or resets high-water state; admission itself stays with the executor path (#3117).
/// </summary>
public interface IHostUpdateReplayAdmissionReader
{
    Task<string?> EvaluateAdmissionAsync(VerifiedHostUpdateCandidate candidate, CancellationToken ct);
}

/// <summary>
/// A release the daemon verified. It can only be produced by <see cref="HostUpdateDaemonReleaseVerifier"/>,
/// so the dispatcher can require proof of verification rather than trusting a caller-built request.
/// </summary>
public sealed class HostUpdateDaemonVerifiedRelease
{
    internal HostUpdateDaemonVerifiedRelease(
        string approvalId,
        string releaseId,
        string channel,
        long sequence,
        string manifestDigest,
        string sourceCommit,
        string hostPlatform,
        IReadOnlyList<HostUpdateExecutionTarget> targets,
        string candidateIdentity,
        DateTimeOffset verifiedAt,
        DateTimeOffset expiresAt)
    {
        ApprovalId = approvalId;
        ReleaseId = releaseId;
        Channel = channel;
        Sequence = sequence;
        ManifestDigest = manifestDigest;
        SourceCommit = sourceCommit;
        HostPlatform = hostPlatform;
        Targets = targets;
        CandidateIdentity = candidateIdentity;
        VerifiedAt = verifiedAt;
        ExpiresAt = expiresAt;
    }

    public string ApprovalId { get; }

    public string ReleaseId { get; }

    public string Channel { get; }

    public long Sequence { get; }

    public string ManifestDigest { get; }

    public string SourceCommit { get; }

    public string TrustRoot => HostUpdateTrustRoot.DefaultTrustRoot;

    public string HostPlatform { get; }

    public IReadOnlyList<HostUpdateExecutionTarget> Targets { get; }

    /// <summary>The replay-store identity (trust root, channel, release, commit, sequence, digest).</summary>
    public string CandidateIdentity { get; }

    public DateTimeOffset VerifiedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    /// <summary>True only when <paramref name="request"/> would execute exactly this verified release and image set.</summary>
    public bool Matches(HostUpdateExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        HostUpdateExecutionChannel channel = Channel == "stable" ? HostUpdateExecutionChannel.Stable : HostUpdateExecutionChannel.Insider;
        if (!string.Equals(request.ReleaseId, ReleaseId, StringComparison.Ordinal) ||
            request.AuthenticatedSequence != Sequence ||
            !string.Equals(request.ManifestDigest, ManifestDigest, StringComparison.Ordinal) ||
            !string.Equals(request.SourceCommit, SourceCommit, StringComparison.Ordinal) ||
            request.Channel != channel ||
            !string.Equals(request.TrustRoot, TrustRoot, StringComparison.Ordinal) ||
            !string.Equals(request.HostPlatform, HostPlatform, StringComparison.Ordinal) ||
            request.ImageSourceMode != HostUpdateImageSourceMode.Registry ||
            request.Targets is null || request.Targets.Count != Targets.Count)
        {
            return false;
        }

        var expected = Targets.ToHashSet();
        return request.Targets.All(target => target is not null && expected.Remove(target)) && expected.Count == 0;
    }
}

public sealed record HostUpdateDaemonVerificationResult(HostUpdateDaemonVerifiedRelease? Release, string? RefusalCode)
{
    public bool Verified => Release is not null;

    public static HostUpdateDaemonVerificationResult Refused(string code) => new(null, code);
}

/// <summary>
/// Durable, redacted verification evidence. Every string is either a fixed code or a value that
/// passed its canonical grammar (release id, sha256 digest, commit, platform); anything else is
/// stored as <see langword="null"/> so a hostile approval cannot inject text into the audit trail.
/// </summary>
public sealed record HostUpdateDaemonVerificationEvidence(
    string EvidenceId,
    string? ApprovalId,
    string? ReleaseId,
    string? Channel,
    long? Sequence,
    string? ManifestDigest,
    string? SourceCommit,
    string TrustRoot,
    string TrustRootFingerprint,
    string? HostPlatform,
    string Outcome,
    string Code,
    DateTimeOffset RecordedAt)
{
    public const string VerifiedOutcome = "verified";

    public const string RefusedOutcome = "refused";
}

public interface IHostUpdateDaemonVerificationJournal
{
    void Append(HostUpdateDaemonVerificationEvidence evidence);

    IReadOnlyList<HostUpdateDaemonVerificationEvidence> ReadAll();
}

/// <summary>
/// Verifies an approved release on the host before any executor action (issue #3116), reusing the
/// existing release trust path: the pinned <see cref="HostUpdateTrustRoot"/>, Cosign
/// (<see cref="ISignedReleaseVerifier"/>) against an operator-supplied Sigstore trusted root,
/// <see cref="SignedUpdateManifestValidator"/> and the anchored replay store. It fails closed on
/// every missing, invalid, mismatched, downgraded, replayed, expired or untrusted input, and
/// journals the outcome. It executes nothing and grants nothing: dispatch stays behind
/// <see cref="IHostUpdateDaemonExecutionGate"/>, whose only production implementation is disabled.
/// </summary>
public sealed partial class HostUpdateDaemonReleaseVerifier(
    IHostUpdateDaemonReleaseSource source,
    IHostUpdateReplayAdmissionReader replay,
    IHostUpdateDaemonVerificationJournal evidence,
    Func<CosignVerifierOptions, ISignedReleaseVerifier>? verifierFactory = null,
    TimeProvider? timeProvider = null)
{
    /// <summary>The #2665 contract bounds an approval's remaining lifetime to five minutes.</summary>
    public static readonly TimeSpan MaxApprovalLifetime = TimeSpan.FromMinutes(5);

    internal const int MaxManifestBytes = 256 * 1024;
    internal const int MaxBundleBytes = 1024 * 1024;
    internal const int MaxTrustedRootBytes = 1024 * 1024;
    internal const string TrustedRootMediaTypePrefix = "application/vnd.dev.sigstore.trustedroot+json";

    private static readonly TimeSpan SignatureTimeout = TimeSpan.FromSeconds(60);
    private static readonly string[] ServiceOrder = ["api", "frontend", "slicer-host", "printer-discovery", "orcaslicer-worker", "monolith"];

    private readonly Func<CosignVerifierOptions, ISignedReleaseVerifier> createVerifier = verifierFactory ?? (options => new ProcessCosignVerifier(options));

    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public async Task<HostUpdateDaemonVerificationResult> VerifyAsync(
        HostUpdateDaemonApprovedRelease approved,
        HostUpdateDaemonVerificationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approved);
        ArgumentNullException.ThrowIfNull(context);
        var trace = new Trace(approved, context);
        HostUpdateDaemonVerificationResult result;
        try
        {
            result = await VerifyCoreAsync(approved, context, trace, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        return Record(trace, result);
    }

    private async Task<HostUpdateDaemonVerificationResult> VerifyCoreAsync(
        HostUpdateDaemonApprovedRelease approved,
        HostUpdateDaemonVerificationContext context,
        Trace trace,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = time.GetUtcNow();
        if (!IsApprovalShapeValid(approved))
        {
            return HostUpdateDaemonVerificationResult.Refused("approval_invalid");
        }

        if (!HostUpdateTrustRoot.IsPinned(approved.TrustRoot))
        {
            return HostUpdateDaemonVerificationResult.Refused("trust_root_untrusted");
        }

        if (approved.ExpiresAt <= now)
        {
            return HostUpdateDaemonVerificationResult.Refused("approval_expired");
        }

        if (approved.ExpiresAt - now > MaxApprovalLifetime)
        {
            return HostUpdateDaemonVerificationResult.Refused("approval_lifetime_exceeded");
        }

        if (context.HostChannel is not ("stable" or "insider") || !SignedUpdateManifestValidator.IsPlatform(context.HostPlatform))
        {
            return HostUpdateDaemonVerificationResult.Refused("host_policy_invalid");
        }

        if (!string.Equals(approved.Channel, context.HostChannel, StringComparison.Ordinal))
        {
            return HostUpdateDaemonVerificationResult.Refused("channel_mismatch");
        }

        if (string.IsNullOrWhiteSpace(context.CosignPath) || !Path.IsPathFullyQualified(context.CosignPath))
        {
            return HostUpdateDaemonVerificationResult.Refused("cosign_not_configured");
        }

        (byte[]? trustedRoot, string? trustError) = ReadTrustedRoot(context.TrustedRootPath, now);
        if (trustError is not null)
        {
            return HostUpdateDaemonVerificationResult.Refused(trustError);
        }

        string version = approved.ReleaseId[(approved.ReleaseId.IndexOf(':', StringComparison.Ordinal) + 1)..];
        HostUpdateDaemonSignedArtifacts? artifacts;
        try
        {
            artifacts = await source.FetchAsync(approved.Channel, "v" + version, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return HostUpdateDaemonVerificationResult.Refused("release_source_unavailable");
        }

        if (artifacts?.Manifest is not { Length: > 0 } manifestBytes)
        {
            return HostUpdateDaemonVerificationResult.Refused("manifest_missing");
        }

        if (artifacts.Bundle is not { Length: > 0 } bundleBytes)
        {
            return HostUpdateDaemonVerificationResult.Refused("signature_missing");
        }

        if (manifestBytes.Length > MaxManifestBytes)
        {
            return HostUpdateDaemonVerificationResult.Refused("manifest_oversized");
        }

        if (bundleBytes.Length > MaxBundleBytes)
        {
            return HostUpdateDaemonVerificationResult.Refused("signature_oversized");
        }

        // Binding first: the fetched bytes must be the exact immutable manifest the approval names.
        string digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(digest), Encoding.ASCII.GetBytes(approved.ManifestDigest)))
        {
            return HostUpdateDaemonVerificationResult.Refused("manifest_digest_mismatch");
        }

        string? signatureError = await VerifySignatureAsync(manifestBytes, bundleBytes, trustedRoot!, approved.Channel, context.CosignPath, cancellationToken).ConfigureAwait(false);
        if (signatureError is not null)
        {
            return HostUpdateDaemonVerificationResult.Refused(signatureError);
        }

        SignedUpdateManifest manifest;
        try
        {
            manifest = SignedUpdateManifestValidator.Parse(Encoding.UTF8.GetString(manifestBytes));
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return HostUpdateDaemonVerificationResult.Refused("manifest_invalid");
        }

        // Validation covers schema, channel/branch/tag provenance, sequence derivation, eligibility,
        // approved image repositories and the complete per-platform digest set.
        if (!SignedUpdateManifestValidator.Validate(manifest).IsValid)
        {
            return HostUpdateDaemonVerificationResult.Refused("manifest_invalid");
        }

        if (!string.Equals(manifest.Channel, approved.Channel, StringComparison.Ordinal))
        {
            return HostUpdateDaemonVerificationResult.Refused("channel_mismatch");
        }

        if (!string.Equals($"{manifest.Channel}:{manifest.Version}", approved.ReleaseId, StringComparison.Ordinal) ||
            manifest.Sequence != approved.Sequence)
        {
            return HostUpdateDaemonVerificationResult.Refused("manifest_binding_mismatch");
        }

        trace.SourceCommit = manifest.SourceCommit;
        (IReadOnlyList<HostUpdateExecutionTarget>? targets, HostUpdatePlatformDigests? digests, string? imageError) = SelectImageSet(manifest, context.HostPlatform);
        if (imageError is not null)
        {
            return HostUpdateDaemonVerificationResult.Refused(imageError);
        }

        var candidate = new VerifiedHostUpdateCandidate(
            approved.ReleaseId,
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
            digests!,
            TrustRoot: HostUpdateTrustRoot.DefaultTrustRoot,
            VerifiedAt: now,
            ExpiresAt: approved.ExpiresAt,
            HostPlatform: context.HostPlatform);
        string? replayError;
        try
        {
            replayError = await replay.EvaluateAdmissionAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException
            or SecurityException or NotSupportedException or JsonException or HostUpdateSubsystemUnavailableException)
        {
            // Missing or rolled-back replay state holds for trusted recovery; it is never reset here.
            return HostUpdateDaemonVerificationResult.Refused("replay_state_unavailable");
        }

        if (replayError is not null)
        {
            return HostUpdateDaemonVerificationResult.Refused(replayError);
        }

        return new(
            new HostUpdateDaemonVerifiedRelease(
                approved.ApprovalId,
                approved.ReleaseId,
                manifest.Channel,
                manifest.Sequence,
                digest,
                manifest.SourceCommit,
                context.HostPlatform,
                targets!,
                candidate.Identity,
                now,
                approved.ExpiresAt),
            null);
    }

    private HostUpdateDaemonVerificationResult Record(Trace trace, HostUpdateDaemonVerificationResult result)
    {
        HostUpdateDaemonVerificationEvidence record = trace.ToEvidence(
            result.Verified ? HostUpdateDaemonVerificationEvidence.VerifiedOutcome : HostUpdateDaemonVerificationEvidence.RefusedOutcome,
            result.Verified ? "verified" : result.RefusalCode!,
            time.GetUtcNow());
        try
        {
            IReadOnlyList<HostUpdateDaemonVerificationEvidence> existing = evidence.ReadAll();
            HostUpdateDaemonVerificationEvidence? last = existing.Count == 0 ? null : existing[^1];

            // A repeated identical refusal (the same approval retried each poll) is recorded once.
            if (!result.Verified && last is not null && last.Outcome == record.Outcome && last.Code == record.Code &&
                last.ApprovalId == record.ApprovalId && last.ManifestDigest == record.ManifestDigest)
            {
                return result;
            }

            evidence.Append(record);
        }
        catch (Exception exception) when (HostUpdateDaemonJournalReader.IsStateFailure(exception))
        {
            // Unrecorded evidence is not evidence: a success that cannot be journaled is refused.
            return result.Verified ? HostUpdateDaemonVerificationResult.Refused("verification_evidence_unavailable") : result;
        }

        return result;
    }

    private async Task<string?> VerifySignatureAsync(
        byte[] manifest,
        byte[] bundle,
        byte[] trustedRoot,
        string channel,
        string cosignPath,
        CancellationToken cancellationToken)
    {
        // Cosign reads a private copy of the exact trusted-root bytes validated above, so the
        // configured file cannot change between validation and verification.
        string directory;
        try
        {
            directory = CreatePrivateDirectory();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return "signature_unverifiable";
        }

        try
        {
            string rootCopy = Path.Join(directory, "trusted_root.json");
            await File.WriteAllBytesAsync(rootCopy, trustedRoot, cancellationToken).ConfigureAwait(false);
            ISignedReleaseVerifier verifier = createVerifier(new CosignVerifierOptions(cosignPath, SignatureTimeout, TrustedRootPath: rootCopy));
            bool verified = await verifier.VerifyAsync(manifest, bundle, HostUpdateTrustRoot.CertificateIdentity(channel), cancellationToken).ConfigureAwait(false);
            return verified ? null : "signature_invalid";
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return "signature_unverifiable";
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best effort; the directory holds only public trust material.
            }
        }
    }

    /// <summary>
    /// Reads and validates the operator's Sigstore trusted root. It must be a fully qualified,
    /// regular, non-linked file with no linked ancestor, a Sigstore trusted-root media type, and at
    /// least one certificate authority and one transparency log valid at <paramref name="now"/>.
    /// </summary>
    internal static (byte[]? Bytes, string? Error) ReadTrustedRoot(string? path, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (null, "trust_root_not_configured");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            return (null, "trust_root_invalid");
        }

        byte[] bytes;
        try
        {
            HostStateFileSecurity.RejectReparseTarget(path);
            var file = new FileInfo(path);
            if (!file.Exists || file.LinkTarget is not null || file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                file.Length is 0 or > MaxTrustedRootBytes)
            {
                return (null, "trust_root_invalid");
            }

            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is 0 or > MaxTrustedRootBytes)
            {
                return (null, "trust_root_invalid");
            }

            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException
            or ArgumentException or NotSupportedException)
        {
            return (null, "trust_root_invalid");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("mediaType", out JsonElement mediaType) || mediaType.ValueKind != JsonValueKind.String ||
                mediaType.GetString()?.StartsWith(TrustedRootMediaTypePrefix, StringComparison.Ordinal) != true ||
                !root.TryGetProperty("certificateAuthorities", out JsonElement authorities) || authorities.ValueKind != JsonValueKind.Array ||
                authorities.GetArrayLength() == 0 ||
                !root.TryGetProperty("tlogs", out JsonElement tlogs) || tlogs.ValueKind != JsonValueKind.Array || tlogs.GetArrayLength() == 0)
            {
                return (null, "trust_root_invalid");
            }

            bool authorityValid = authorities.EnumerateArray().Any(authority => IsValidAt(authority, now));
            bool tlogValid = tlogs.EnumerateArray().Any(tlog =>
                tlog.ValueKind == JsonValueKind.Object && tlog.TryGetProperty("publicKey", out JsonElement key) && IsValidAt(key, now));
            return authorityValid && tlogValid ? (bytes, null) : (null, "trust_root_expired");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return (null, "trust_root_invalid");
        }
    }

    private static bool IsValidAt(JsonElement element, DateTimeOffset now)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("validFor", out JsonElement validFor) || validFor.ValueKind != JsonValueKind.Object ||
            !validFor.TryGetProperty("start", out JsonElement start) || start.ValueKind != JsonValueKind.String ||
            !start.TryGetDateTimeOffset(out DateTimeOffset startAt) || startAt > now)
        {
            return false;
        }

        if (!validFor.TryGetProperty("end", out JsonElement end) || end.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        return end.ValueKind == JsonValueKind.String && end.TryGetDateTimeOffset(out DateTimeOffset endAt) && endAt > now;
    }

    /// <summary>
    /// The complete image set the executor requires for <paramref name="hostPlatform"/>: every
    /// required service, each pinned by its signed per-platform child digest. The executor runs
    /// the full six-service set for every topology, so a partial set is never verifiable.
    /// </summary>
    internal static (IReadOnlyList<HostUpdateExecutionTarget>? Targets, HostUpdatePlatformDigests? Digests, string? Error) SelectImageSet(
        SignedUpdateManifest manifest,
        string hostPlatform)
    {
        if (manifest.Platforms is null || !manifest.Platforms.Contains(hostPlatform, StringComparer.Ordinal))
        {
            return (null, null, "platform_not_in_manifest");
        }

        var targets = new List<HostUpdateExecutionTarget>(ServiceOrder.Length);
        foreach (string serviceId in ServiceOrder)
        {
            SignedUpdateService? service = manifest.Services?.FirstOrDefault(s => s is not null && string.Equals(s.Id, serviceId, StringComparison.Ordinal));
            if (service?.Platforms?.Contains(hostPlatform, StringComparer.Ordinal) != true ||
                manifest.PlatformDigests is null ||
                !manifest.PlatformDigests.TryGetValue(SignedUpdateManifestValidator.PlatformKey(serviceId, hostPlatform), out string? child) ||
                !HostUpdateValidation.IsCanonicalDigest(child))
            {
                return (null, null, "image_set_incomplete:" + serviceId);
            }

            targets.Add(new HostUpdateExecutionTarget(serviceId, hostPlatform, child));
        }

        var digests = new HostUpdatePlatformDigests(
            targets[0].ChildDigest,
            targets[1].ChildDigest,
            targets[2].ChildDigest,
            targets[3].ChildDigest,
            targets[4].ChildDigest,
            targets[5].ChildDigest);
        return digests.IsComplete ? (targets, digests, null) : (null, null, "image_set_mixed_or_incomplete");
    }

    private static bool IsApprovalShapeValid(HostUpdateDaemonApprovedRelease approved) =>
        approved.ApprovalId is not null && ApprovalIdPattern().IsMatch(approved.ApprovalId) &&
        approved.Channel is "stable" or "insider" &&
        HostUpdateValidation.IsReleaseId(approved.ReleaseId) &&
        approved.ReleaseId.StartsWith(approved.Channel + ":", StringComparison.Ordinal) &&
        approved.Sequence >= 1 &&
        HostUpdateValidation.IsCanonicalDigest(approved.ManifestDigest) &&
        approved.TrustRoot is not null;

    private static string CreatePrivateDirectory()
    {
        string directory = Path.Join(Path.GetTempPath(), "printfarmer-daemon-verify-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return directory;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)]
    internal static partial Regex ApprovalIdPattern();

    /// <summary>Collects only values that passed their canonical grammar, for redacted evidence.</summary>
    private sealed class Trace(HostUpdateDaemonApprovedRelease approved, HostUpdateDaemonVerificationContext context)
    {
        public string? SourceCommit { get; set; }

        public HostUpdateDaemonVerificationEvidence ToEvidence(string outcome, string code, DateTimeOffset recordedAt)
        {
            bool channelValid = approved.Channel is "stable" or "insider";
            return new(
                Guid.NewGuid().ToString("N"),
                approved.ApprovalId is not null && ApprovalIdPattern().IsMatch(approved.ApprovalId) ? approved.ApprovalId : null,
                HostUpdateValidation.IsReleaseId(approved.ReleaseId) ? approved.ReleaseId : null,
                channelValid ? approved.Channel : null,
                approved.Sequence >= 1 ? approved.Sequence : null,
                HostUpdateValidation.IsCanonicalDigest(approved.ManifestDigest) ? approved.ManifestDigest : null,
                SourceCommit,
                HostUpdateTrustRoot.IsPinned(approved.TrustRoot) ? HostUpdateTrustRoot.DefaultTrustRoot : "untrusted",
                HostUpdateTrustRoot.Fingerprint,
                SignedUpdateManifestValidator.IsPlatform(context.HostPlatform) ? context.HostPlatform : null,
                outcome,
                code,
                recordedAt);
        }
    }
}

/// <summary>
/// Hash-chained verification evidence beside the execution journal, using the same chain and
/// atomic-rewrite rules. It is deliberately a separate file: an execution-journal activity would
/// change the executor's first-acceptance and in-flight semantics before anything was admitted.
/// </summary>
public sealed class FileHostUpdateDaemonVerificationJournal(string path) : IHostUpdateDaemonVerificationJournal
{
    public const string FileName = "daemon-verification.ndjson";

    private readonly string stagedPath = path + ".staged";

    private sealed record ChainRecord(string PreviousHash, string Payload, string Hash);

    public IReadOnlyList<HostUpdateDaemonVerificationEvidence> ReadAll() =>
        ReadValidated().Select(pair => pair.Evidence).ToArray();

    public void Append(HostUpdateDaemonVerificationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        List<(ChainRecord Record, HostUpdateDaemonVerificationEvidence Evidence)> records = ReadValidated();
        string previous = records.Count == 0 ? string.Empty : records[^1].Record.Hash;
        string payload = JsonSerializer.Serialize(evidence);
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(previous + payload)));
        var lines = records.Select(r => JsonSerializer.Serialize(r.Record)).ToList();
        lines.Add(JsonSerializer.Serialize(new ChainRecord(previous, payload, hash)));
        string contents = string.Join('\n', lines) + "\n";
        _ = Parse(contents);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        HostStateFileSecurity.RejectReparseTarget(stagedPath);
        try
        {
            using (FileStream stream = new(stagedPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(contents);
                stream.Write(bytes);
                stream.Flush(true);
            }

            _ = Parse(File.ReadAllText(stagedPath));
            HostStateFileSecurity.RejectReparseTarget(path);
            File.Move(stagedPath, path, true);
        }
        finally
        {
            if (File.Exists(stagedPath) && !HostStateFileSecurity.IsReparsePoint(stagedPath))
            {
                File.Delete(stagedPath);
            }
        }
    }

    private List<(ChainRecord Record, HostUpdateDaemonVerificationEvidence Evidence)> ReadValidated()
    {
        if (File.Exists(stagedPath))
        {
            // Rename is the commit point; a surviving stage was never committed.
            HostStateFileSecurity.RejectReparseTarget(stagedPath);
            File.Delete(stagedPath);
        }

        if (!File.Exists(path))
        {
            return [];
        }

        HostStateFileSecurity.RejectReparseTarget(path);
        return Parse(File.ReadAllText(path));
    }

    private static List<(ChainRecord Record, HostUpdateDaemonVerificationEvidence Evidence)> Parse(string contents)
    {
        if (contents.Length != 0 && !contents.EndsWith('\n'))
        {
            throw new InvalidDataException("journal_verification_corrupt");
        }

        List<(ChainRecord, HostUpdateDaemonVerificationEvidence)> records = [];
        string previous = string.Empty;
        foreach (string line in contents.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            ChainRecord? record;
            HostUpdateDaemonVerificationEvidence? evidence;
            byte[] actual;
            try
            {
                record = JsonSerializer.Deserialize<ChainRecord>(line);
                actual = Convert.FromHexString(record?.Hash ?? string.Empty);
                evidence = record?.Payload is null ? null : JsonSerializer.Deserialize<HostUpdateDaemonVerificationEvidence>(record.Payload);
            }
            catch (Exception exception) when (exception is JsonException or FormatException)
            {
                throw new InvalidDataException("journal_verification_integrity_failure", exception);
            }

            byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes((record?.PreviousHash ?? string.Empty) + (record?.Payload ?? string.Empty)));
            if (record is null || evidence is null || !string.Equals(record.PreviousHash, previous, StringComparison.Ordinal) ||
                !CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                throw new InvalidDataException("journal_verification_integrity_failure");
            }

            records.Add((record, evidence));
            previous = record.Hash;
        }

        return records;
    }
}
