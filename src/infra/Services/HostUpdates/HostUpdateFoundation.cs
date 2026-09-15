using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

#pragma warning disable SA1116 // Immutable record declarations are intentionally compact.
#pragma warning disable SA1136 // The two authorization values are a closed pair.
#pragma warning disable SA1502 // Simple immutable value declarations remain concise.
#pragma warning disable SA1503 // Guard clauses remain compact.
#pragma warning disable SA1514 // Adjacent bounded contracts are grouped intentionally.

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>Provides bounded, host-local plan and staging transitions for managed updates.</summary>
public sealed class HostUpdateFoundation
{
    private readonly IHostUpdateMetadataProvider metadataProvider;
    private readonly IHostUpdateCompatibilityEvaluator compatibilityEvaluator;
    private readonly IHostUpdateStager stager;
    private readonly IHostUpdateJournal journal;
    private readonly IHostUpdateInstallationLock installationLock;

    /// <summary>Initializes the shared foundation used by future manual and standing-policy callers.</summary>
    public HostUpdateFoundation(IHostUpdateMetadataProvider metadataProvider, IHostUpdateCompatibilityEvaluator compatibilityEvaluator, IHostUpdateStager stager, IHostUpdateJournal journal, IHostUpdateInstallationLock installationLock)
    {
        this.metadataProvider = metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));
        this.compatibilityEvaluator = compatibilityEvaluator ?? throw new ArgumentNullException(nameof(compatibilityEvaluator));
        this.stager = stager ?? throw new ArgumentNullException(nameof(stager));
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
        this.installationLock = installationLock ?? throw new ArgumentNullException(nameof(installationLock));
    }

    /// <summary>Creates a plan after fresh, typed preflight and release-identity validation.</summary>
    public async Task<HostUpdatePlanResult> PlanAsync(HostUpdatePlanRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        SignedReleaseMetadata metadata = await metadataProvider.GetCurrentAsync(request.TargetChannel, ct);
        HostUpdatePlanResult result = Evaluate(request, metadata);
        await journal.AppendAsync(HostUpdateJournalEntry.Planned(request, metadata, result), ct);
        return result;
    }

    /// <summary>Revalidates and stages a complete immutable set without applying it.</summary>
    public async Task<HostUpdateStageResult> StageAsync(HostUpdatePlan plan, HostUpdateAuthorization authorization, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(authorization);
        plan.Request.Validate();

        await using IAsyncDisposable lease = await installationLock.AcquireAsync(plan.InstallationId, ct);
        if (!authorization.IsValidFor(plan))
        {
            HostUpdateStageResult rejected = HostUpdateStageResult.Rejected("authorization_invalid");
            await journal.AppendAsync(HostUpdateJournalEntry.Failed(plan, rejected), ct);
            return rejected;
        }

        await journal.AppendAsync(HostUpdateJournalEntry.Approved(plan, authorization), ct);
        SignedReleaseMetadata currentMetadata = await metadataProvider.GetCurrentAsync(plan.TargetChannel, ct);
        HostUpdatePlanResult revalidation = Evaluate(plan.Request, currentMetadata);
        if (!revalidation.IsEligible || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(plan.PlanHash), Convert.FromHexString(revalidation.PlanHash)))
        {
            HostUpdateStageResult rejected = HostUpdateStageResult.Rejected("plan_or_metadata_changed");
            await journal.AppendAsync(HostUpdateJournalEntry.Failed(plan, rejected), ct);
            return rejected;
        }

        await journal.AppendAsync(HostUpdateJournalEntry.StagingIntent(plan, currentMetadata), ct);
        HostUpdateStagingResult staged = await stager.StageAsync(plan, currentMetadata, ct);
        if (!staged.IsComplete)
        {
            HostUpdateStageResult failed = HostUpdateStageResult.RecoverableFailure(staged.Code);
            await journal.AppendAsync(HostUpdateJournalEntry.Failed(plan, failed), ct);
            return failed;
        }

        if (!HostUpdateValidation.IsDigest(staged.StagedSetDigest) || !HostUpdateValidation.IsDigest(staged.RecoveryArtifactDigest))
        {
            HostUpdateStageResult failed = HostUpdateStageResult.RecoverableFailure("staging_receipt_invalid");
            await journal.AppendAsync(HostUpdateJournalEntry.Failed(plan, failed), ct);
            return failed;
        }

        HostUpdateStageResult completed = HostUpdateStageResult.Staged(staged.StagedSetDigest!, staged.RecoveryArtifactDigest!);
        await journal.AppendAsync(HostUpdateJournalEntry.Staged(plan, completed), ct);
        return completed;
    }

    private HostUpdatePlanResult Evaluate(HostUpdatePlanRequest request, SignedReleaseMetadata metadata)
    {
        List<string> reasons = [];
        reasons.AddRange(request.Installation.Validate());
        reasons.AddRange(metadata.ValidateFor(request));
        IReadOnlyList<string> compatibilityReasons = compatibilityEvaluator.Evaluate(request.Installation, metadata);
        if (compatibilityReasons.Any(reason => !HostUpdateValidation.IsRejectionCode(reason)))
        {
            reasons.Add("compatibility_evidence_invalid");
        }
        else
        {
            reasons.AddRange(compatibilityReasons);
        }

        reasons.Sort(StringComparer.Ordinal);
        return new HostUpdatePlanResult(reasons.Count == 0, ComputePlanHash(request, metadata), metadata.Identity.ManifestDigest, reasons);
    }

    private static string ComputePlanHash(HostUpdatePlanRequest request, SignedReleaseMetadata metadata)
    {
        string canonical = string.Join(
            '\n',
            request.OperationId, request.IdempotencyKey, request.ActorId, request.ReasonCode,
            request.SourceChannel, request.TargetChannel, request.ChannelPolicyRevision,
            string.Join(',', request.RequiredComponents.OrderBy(component => component, StringComparer.Ordinal)),
            request.Installation.CanonicalValue, metadata.CanonicalValue);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

/// <summary>Typed, explicitly configured installation and operational preflight evidence.</summary>
public sealed record HostInstallationEvidence(
    string InstallationId,
    string TrustedInstallationFingerprint,
    string SourceTargetFingerprint,
    string TopologyFingerprint,
    int ReplicaCount,
    int RemoteWorkerCount,
    string WorkerInventoryFingerprint,
    string Provider,
    string SchemaFingerprint,
    string ConfigurationFingerprint,
    string UpdaterVersion,
    string ResourceFingerprint,
    long AvailableDiskBytes,
    long RequiredDiskBytes,
    long RecoveryCapacityBytes,
    bool WithinMaintenanceWindow,
    bool RegistryReady,
    bool BackupDestinationReady)
{
    /// <summary>Returns a canonical non-secret representation used only in plan hashing.</summary>
    public string CanonicalValue => string.Join('|', InstallationId, TrustedInstallationFingerprint, SourceTargetFingerprint, TopologyFingerprint, ReplicaCount, RemoteWorkerCount, WorkerInventoryFingerprint, Provider, SchemaFingerprint, ConfigurationFingerprint, UpdaterVersion, ResourceFingerprint, AvailableDiskBytes, RequiredDiskBytes, RecoveryCapacityBytes, WithinMaintenanceWindow, RegistryReady, BackupDestinationReady);

    /// <summary>Returns stable codes for incomplete or unsuitable evidence.</summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> reasons = [];
        if (!HostUpdateValidation.IsIdentifier(InstallationId) || !HostUpdateValidation.IsDigest(TrustedInstallationFingerprint) || !HostUpdateValidation.IsDigest(SourceTargetFingerprint))
        {
            reasons.Add("installation_untrusted");
        }

        if (!HostUpdateValidation.IsDigest(TopologyFingerprint) || !HostUpdateValidation.IsDigest(WorkerInventoryFingerprint) || ReplicaCount < 1 || RemoteWorkerCount < 0)
        {
            reasons.Add("topology_invalid");
        }

        if (!HostUpdateValidation.IsProvider(Provider) || !HostUpdateValidation.IsDigest(SchemaFingerprint) || !HostUpdateValidation.IsDigest(ConfigurationFingerprint))
        {
            reasons.Add("schema_or_provider_invalid");
        }

        if (!HostUpdateValidation.IsVersion(UpdaterVersion))
        {
            reasons.Add("updater_version_invalid");
        }

        if (!HostUpdateValidation.IsDigest(ResourceFingerprint) || AvailableDiskBytes < RequiredDiskBytes || RecoveryCapacityBytes < RequiredDiskBytes)
        {
            reasons.Add("resources_insufficient");
        }

        if (!WithinMaintenanceWindow)
        {
            reasons.Add("maintenance_window_closed");
        }

        if (!RegistryReady)
        {
            reasons.Add("registry_unavailable");
        }

        if (!BackupDestinationReady)
        {
            reasons.Add("backup_destination_unavailable");
        }

        return reasons;
    }
}

/// <summary>Fixed plan input; it intentionally contains no commands, URLs, or filesystem paths.</summary>
public sealed record HostUpdatePlanRequest(string OperationId, string IdempotencyKey, string ActorId, string ReasonCode, string SourceChannel, string TargetChannel, string ChannelPolicyRevision, IReadOnlySet<string> RequiredComponents, HostInstallationEvidence Installation)
{
    /// <summary>Validates all caller-controlled inputs before metadata access or journal writes.</summary>
    public void Validate()
    {
        if (!HostUpdateValidation.IsIdentifier(OperationId) || !HostUpdateValidation.IsIdentifier(IdempotencyKey) || !HostUpdateValidation.IsIdentifier(ActorId) || !HostUpdateValidation.IsRejectionCode(ReasonCode) || !HostUpdateValidation.IsChannel(SourceChannel) || !HostUpdateValidation.IsChannel(TargetChannel) || !HostUpdateValidation.IsIdentifier(ChannelPolicyRevision) || RequiredComponents.Count == 0 || RequiredComponents.Any(component => !HostUpdateValidation.IsIdentifier(component)) || Installation is null)
        {
            throw new ArgumentException("Host update input is not a constrained, valid plan request.", nameof(OperationId));
        }
    }
}

/// <summary>An immutable validated plan that this foundation can only stage.</summary>
public sealed record HostUpdatePlan(HostUpdatePlanRequest Request, string PlanHash)
{
    /// <summary>Gets the operation identifier.</summary>
    public string OperationId => Request.OperationId;

    /// <summary>Gets the retry-safe idempotency identifier.</summary>
    public string IdempotencyKey => Request.IdempotencyKey;

    /// <summary>Gets the configured installation identifier.</summary>
    public string InstallationId => Request.Installation.InstallationId;

    /// <summary>Gets the selected target channel.</summary>
    public string TargetChannel => Request.TargetChannel;
}

/// <summary>Bounded approval from a future manual or standing-policy integration.</summary>
public sealed record HostUpdateAuthorization(string PlanHash, string ChannelPolicyRevision, DateTimeOffset ExpiresAt, HostUpdateAuthorizationKind Kind)
{
    /// <summary>Returns whether the authorization is current and bound to this plan and policy.</summary>
    public bool IsValidFor(HostUpdatePlan plan) =>
        ExpiresAt > DateTimeOffset.UtcNow &&
        HostUpdateValidation.IsHexHash(PlanHash) &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(PlanHash), Convert.FromHexString(plan.PlanHash)) &&
        string.Equals(ChannelPolicyRevision, plan.Request.ChannelPolicyRevision, StringComparison.Ordinal);
}

/// <summary>Authorization source without adding scheduling or request integration.</summary>
public enum HostUpdateAuthorizationKind
{
    Manual,
    StandingPolicy,
}

/// <summary>Result of a fresh preflight evaluation.</summary>
public sealed record HostUpdatePlanResult(bool IsEligible, string PlanHash, string ManifestDigest, IReadOnlyList<string> Reasons);

/// <summary>Canonical identity of a signed immutable release set.</summary>
public sealed record CanonicalReleaseIdentity(string ReleaseId, string Version, string SourceTag, string SourceBranch, string SourceCommit, string AuthorizedBranchHead, string BuildMetadata, string OciReleaseLabel, string OciVersionLabel, string ProvenanceSubjectDigest, string ManifestDigest, string IndexDigest)
{
    /// <summary>Returns canonical immutable identity fields for hashing.</summary>
    public string CanonicalValue => string.Join('|', ReleaseId, Version, SourceTag, SourceBranch, SourceCommit, AuthorizedBranchHead, BuildMetadata, OciReleaseLabel, OciVersionLabel, ProvenanceSubjectDigest, ManifestDigest, IndexDigest);
}

/// <summary>Trusted signed release metadata with immutable component platform digests.</summary>
public sealed record SignedReleaseMetadata(string Channel, long Sequence, bool SignatureVerified, CanonicalReleaseIdentity Identity, IReadOnlyDictionary<string, string> ComponentPlatformDigests)
{
    /// <summary>Returns all immutable metadata for plan hashing.</summary>
    public string CanonicalValue => string.Join('|', Channel, Sequence, SignatureVerified, Identity.CanonicalValue, string.Join(',', ComponentPlatformDigests.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")));

    /// <summary>Returns stable validation codes for a plan's selected release set.</summary>
    public IReadOnlyList<string> ValidateFor(HostUpdatePlanRequest request)
    {
        List<string> reasons = [];
        if (!SignatureVerified)
        {
            reasons.Add("metadata_signature_invalid");
        }

        if (!string.Equals(Channel, request.TargetChannel, StringComparison.Ordinal))
        {
            reasons.Add("channel_mismatch");
        }

        if (!HostUpdateValidation.IsReleaseIdentity(Identity, request.TargetChannel))
        {
            reasons.Add("release_identity_invalid");
        }

        if (string.CompareOrdinal(Identity.Version, "0.0.0") <= 0)
        {
            reasons.Add("release_version_invalid");
        }

        if (!request.RequiredComponents.All(component => ComponentPlatformDigests.TryGetValue(component, out string? digest) && HostUpdateValidation.IsDigest(digest)))
        {
            reasons.Add("release_set_incomplete");
        }

        return reasons;
    }
}

/// <summary>Obtains currently verified signed metadata for one fixed channel.</summary>
public interface IHostUpdateMetadataProvider
{
    Task<SignedReleaseMetadata> GetCurrentAsync(string channel, CancellationToken ct);
}

/// <summary>Evaluates deployment-specific compatibility without exposing application actions.</summary>
public interface IHostUpdateCompatibilityEvaluator
{
    IReadOnlyList<string> Evaluate(HostInstallationEvidence installation, SignedReleaseMetadata metadata);
}

/// <summary>Stages only approved immutable artifacts and recovery assets before downtime.</summary>
public interface IHostUpdateStager
{
    Task<HostUpdateStagingResult> StageAsync(HostUpdatePlan plan, SignedReleaseMetadata metadata, CancellationToken ct);
}

/// <summary>Result reported by a staging adapter.</summary>
public sealed record HostUpdateStagingResult(bool IsComplete, string Code, string? StagedSetDigest, string? RecoveryArtifactDigest);

/// <summary>Public staging result that never implies apply or recovery occurred.</summary>
public sealed record HostUpdateStageResult(bool IsStaged, bool IsRecoverableFailure, string Code, string? StagedSetDigest, string? RecoveryArtifactDigest)
{
    /// <summary>Creates a completed staging result.</summary>
    public static HostUpdateStageResult Staged(string stagedSetDigest, string recoveryArtifactDigest) => new(true, false, "staged", stagedSetDigest, recoveryArtifactDigest);

    /// <summary>Creates a recoverable result that leaves the running release untouched.</summary>
    public static HostUpdateStageResult RecoverableFailure(string code) => new(false, true, HostUpdateValidation.IsRejectionCode(code) ? code : "staging_failed", null, null);

    /// <summary>Creates a non-retryable result requiring fresh authorization and evidence.</summary>
    public static HostUpdateStageResult Rejected(string code) => new(false, false, code, null, null);
}

/// <summary>Persists host-local journal entries independently from application containers and databases.</summary>
public interface IHostUpdateJournal
{
    Task AppendAsync(HostUpdateJournalEntry entry, CancellationToken ct);
}

/// <summary>Reads redacted host-local journal snapshots while application containers are stopped.</summary>
public interface IHostUpdateJournalReader
{
    Task<IReadOnlyList<HostUpdateJournalEntry>> ReadAsync(CancellationToken ct);
}

/// <summary>Acquires a host-local exclusive installation lease.</summary>
public interface IHostUpdateInstallationLock
{
    Task<IAsyncDisposable> AcquireAsync(string installationId, CancellationToken ct);
}

/// <summary>Redacted durable journal state for planning and staging only.</summary>
public sealed record HostUpdateJournalEntry(long Revision, DateTimeOffset RecordedAt, string OperationId, string IdempotencyKey, string State, string Code, bool Recoverable, HostUpdateJournalSnapshot Snapshot)
{
    /// <summary>Records the plan outcome without arbitrary caller-controlled text.</summary>
    public static HostUpdateJournalEntry Planned(HostUpdatePlanRequest request, SignedReleaseMetadata metadata, HostUpdatePlanResult result) => Create(request, metadata, "Planned", result.IsEligible ? "eligible" : result.Reasons[0], false, result.PlanHash, null, null);

    /// <summary>Records a plan-bound approval.</summary>
    public static HostUpdateJournalEntry Approved(HostUpdatePlan plan, HostUpdateAuthorization authorization) => Create(plan.Request, null, "Approved", authorization.Kind == HostUpdateAuthorizationKind.Manual ? "manual_authorized" : "standing_policy_authorized", false, plan.PlanHash, null, null);

    /// <summary>Records durable staging intent before artifact transfer.</summary>
    public static HostUpdateJournalEntry StagingIntent(HostUpdatePlan plan, SignedReleaseMetadata metadata) => Create(plan.Request, metadata, "Staging", "intent", false, plan.PlanHash, null, null);

    /// <summary>Records an immutable staging receipt including its recovery digest.</summary>
    public static HostUpdateJournalEntry Staged(HostUpdatePlan plan, HostUpdateStageResult result) => Create(plan.Request, null, "Staged", result.Code, false, plan.PlanHash, result.StagedSetDigest, result.RecoveryArtifactDigest);

    /// <summary>Records a rejected or recoverable result with a safe rejection code.</summary>
    public static HostUpdateJournalEntry Failed(HostUpdatePlan plan, HostUpdateStageResult result) => Create(plan.Request, null, "Failed", result.Code, result.IsRecoverableFailure, plan.PlanHash, null, null);

    private static HostUpdateJournalEntry Create(HostUpdatePlanRequest request, SignedReleaseMetadata? metadata, string state, string code, bool recoverable, string planHash, string? stagedSetDigest, string? recoveryArtifactDigest) =>
        new(0, DateTimeOffset.UtcNow, request.OperationId, request.IdempotencyKey, state, code, recoverable, new(request.ActorId, request.ReasonCode, request.SourceChannel, request.TargetChannel, request.ChannelPolicyRevision, planHash, metadata?.Identity.ReleaseId, metadata?.Identity.Version, metadata?.Identity.SourceTag, metadata?.Identity.SourceBranch, metadata?.Identity.SourceCommit, metadata?.Identity.ManifestDigest, metadata?.Identity.IndexDigest, stagedSetDigest, recoveryArtifactDigest));
}

/// <summary>Safe identifiers and immutable digests retained in a journal record.</summary>
public sealed record HostUpdateJournalSnapshot(string ActorId, string ReasonCode, string SourceChannel, string TargetChannel, string ChannelPolicyRevision, string PlanHash, string? ReleaseId, string? Version, string? SourceTag, string? SourceBranch, string? SourceCommit, string? ManifestDigest, string? IndexDigest, string? StagedSetDigest, string? RecoveryArtifactDigest);

/// <summary>Append-only JSON-lines journal with write-through durability and fail-closed reconciliation.</summary>
public sealed class FileHostUpdateJournal : IHostUpdateJournal, IHostUpdateJournalReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string journalPath;
    private long revision;

    /// <summary>Initializes a journal rooted in a host-owned directory.</summary>
    public FileHostUpdateJournal(string hostStateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostStateDirectory);
        journalPath = Path.Combine(Path.GetFullPath(hostStateDirectory), "host-update.journal.jsonl");
    }

    /// <inheritdoc />
    public async Task AppendAsync(HostUpdateJournalEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        if (revision == 0)
        {
            IReadOnlyList<HostUpdateJournalEntry> entries = await ReadAsync(ct);
            revision = entries.Count == 0 ? 0 : entries[^1].Revision;
        }

        HostUpdateJournalEntry durableEntry = entry with { Revision = checked(++revision) };
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(durableEntry, SerializerOptions) + Environment.NewLine);
        await using FileStream stream = new(journalPath, new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read, Options = FileOptions.WriteThrough });
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HostUpdateJournalEntry>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(journalPath))
        {
            return [];
        }

        List<HostUpdateJournalEntry> entries = [];
        using FileStream stream = new(journalPath, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite });
        using StreamReader reader = new(stream, Encoding.UTF8, true, leaveOpen: false);
        while (await reader.ReadLineAsync(ct) is { Length: > 0 } line)
        {
            HostUpdateJournalEntry? entry = JsonSerializer.Deserialize<HostUpdateJournalEntry>(line, SerializerOptions);
            if (entry is null || entry.Revision <= 0 || (entries.Count > 0 && entry.Revision <= entries[^1].Revision) || !HostUpdateValidation.IsJournalEntry(entry))
            {
                throw new InvalidDataException("Host update journal is corrupt and cannot be reconciled safely.");
            }

            entries.Add(entry);
        }

        return entries;
    }
}

/// <summary>Exclusive host installation lock backed by an OS file handle.</summary>
public sealed class FileHostUpdateInstallationLock : IHostUpdateInstallationLock
{
    private readonly string hostStateDirectory;

    /// <summary>Initializes a lock store rooted in a host-owned directory.</summary>
    public FileHostUpdateInstallationLock(string hostStateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostStateDirectory);
        this.hostStateDirectory = Path.GetFullPath(hostStateDirectory);
    }

    /// <inheritdoc />
    public Task<IAsyncDisposable> AcquireAsync(string installationId, CancellationToken ct)
    {
        if (!HostUpdateValidation.IsIdentifier(installationId))
        {
            throw new ArgumentException("Installation identifier is invalid.", nameof(installationId));
        }

        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(hostStateDirectory);
        string lockPath = Path.Combine(hostStateDirectory, $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installationId)))}.lock");
        FileStream stream = new(lockPath, new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None, Options = FileOptions.WriteThrough });
        return Task.FromResult<IAsyncDisposable>(new FileLease(stream));
    }

    private sealed class FileLease(FileStream stream) : IAsyncDisposable
    {
        [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP007:Don't dispose injected", Justification = "The lock factory exclusively creates and transfers stream ownership to the lease.")]
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}

internal static class HostUpdateValidation
{
    private static readonly HashSet<string> Channels = ["stable", "insider"];
    private static readonly HashSet<string> Providers = ["postgres", "sqlserver"];

    public static bool IsIdentifier(string? value) => value is { Length: > 0 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    public static bool IsChannel(string? value) => value is not null && Channels.Contains(value);

    public static bool IsProvider(string? value) => value is not null && Providers.Contains(value);

    public static bool IsVersion(string? value) => value is { Length: > 0 and <= 64 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+');

    public static bool IsDigest(string? value) => value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) && value[7..].All(Uri.IsHexDigit);

    public static bool IsHexHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public static bool IsRejectionCode(string? value) => value is { Length: > 0 and <= 80 } && value.All(character => char.IsLower(character) || char.IsDigit(character) || character == '_');

    public static bool IsReleaseIdentity(CanonicalReleaseIdentity identity, string targetChannel) =>
        identity is not null && IsIdentifier(identity.ReleaseId) && IsVersion(identity.Version) && IsIdentifier(identity.SourceTag) &&
        ((targetChannel == "stable" && identity.SourceBranch == "main") || (targetChannel == "insider" && identity.SourceBranch == "development")) &&
        IsHexHash(identity.SourceCommit) && IsHexHash(identity.AuthorizedBranchHead) && string.Equals(identity.SourceCommit, identity.AuthorizedBranchHead, StringComparison.Ordinal) &&
        IsIdentifier(identity.BuildMetadata) && IsIdentifier(identity.OciReleaseLabel) && IsVersion(identity.OciVersionLabel) &&
        string.Equals(identity.ReleaseId, identity.OciReleaseLabel, StringComparison.Ordinal) && string.Equals(identity.Version, identity.OciVersionLabel, StringComparison.Ordinal) &&
        IsDigest(identity.ProvenanceSubjectDigest) && IsDigest(identity.ManifestDigest) && IsDigest(identity.IndexDigest);

    public static bool IsJournalEntry(HostUpdateJournalEntry entry) =>
        IsIdentifier(entry.OperationId) && IsIdentifier(entry.IdempotencyKey) && IsRejectionCode(entry.Code) &&
        entry.Snapshot is { } snapshot && IsIdentifier(snapshot.ActorId) && IsRejectionCode(snapshot.ReasonCode) &&
        IsChannel(snapshot.SourceChannel) && IsChannel(snapshot.TargetChannel) && IsIdentifier(snapshot.ChannelPolicyRevision) && IsHexHash(snapshot.PlanHash) &&
        (snapshot.ReleaseId is null || IsIdentifier(snapshot.ReleaseId)) && (snapshot.Version is null || IsVersion(snapshot.Version)) &&
        (snapshot.SourceTag is null || IsIdentifier(snapshot.SourceTag)) && (snapshot.SourceBranch is null || snapshot.SourceBranch is "main" or "development") &&
        (snapshot.SourceCommit is null || IsHexHash(snapshot.SourceCommit)) && (snapshot.ManifestDigest is null || IsDigest(snapshot.ManifestDigest)) &&
        (snapshot.IndexDigest is null || IsDigest(snapshot.IndexDigest)) && (snapshot.StagedSetDigest is null || IsDigest(snapshot.StagedSetDigest)) &&
        (snapshot.RecoveryArtifactDigest is null || IsDigest(snapshot.RecoveryArtifactDigest));
}
