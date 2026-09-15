using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

#pragma warning disable CA1859 // Reconciliation intentionally exposes read-only journal collections.
#pragma warning disable IDISP007 // File leases own streams created by their factories.
#pragma warning disable SA1136 // Closed lifecycle values are intentionally compact.
#pragma warning disable SA1408 // Closed validation predicates retain direct boolean composition.
#pragma warning disable SA1501 // Guard clauses are intentionally concise.
#pragma warning disable SA1502 // Immutable contracts are intentionally concise.
#pragma warning disable SA1503 // Guard clauses are intentionally concise.
#pragma warning disable SA1514 // Adjacent bounded contracts are intentionally grouped.
#pragma warning disable SA1516 // Adjacent bounded contracts are intentionally grouped.
#pragma warning disable SA1513 // Compact validation helpers use conventional final braces.

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>Provides only host-local preflight, receipt-verified staging, and journal reconciliation.</summary>
public sealed class HostUpdateFoundation(
    IHostUpdateMetadataProvider metadataProvider,
    IHostUpdateCompatibilityEvaluator compatibilityEvaluator,
    IHostUpdateStager stager,
    IHostUpdateJournal journal,
    IHostUpdateInstallationLock installationLock)
{
    /// <summary>Creates a hash-bound plan from fresh trusted installation evidence and release metadata.</summary>
    public async Task<HostUpdatePlanResult> PlanAsync(HostUpdatePlanRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsValid)
        {
            return HostUpdatePlanResult.Rejected("request_invalid");
        }

        SignedReleaseMetadata metadata = await metadataProvider.GetCurrentAsync(request.TargetChannel, ct);
        HostUpdatePlanResult result = Evaluate(request, metadata);
        await journal.AppendAsync(HostUpdateJournalEntry.Planned(request, metadata, result), ct);
        return result;
    }

    /// <summary>Revalidates evidence under the installation lock and stages no more than the immutable plan.</summary>
    public async Task<HostUpdateStageResult> StageAsync(HostUpdatePlan plan, HostUpdateAuthorization authorization, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(authorization);
        if (!plan.IsValid)
        {
            return HostUpdateStageResult.Rejected("plan_invalid");
        }

        try
        {
            await using IAsyncDisposable lease = await installationLock.AcquireAsync(plan.InstallationId, ct);
            IReadOnlyList<HostUpdateJournalEntry> entries = await journal.ReadAsync(ct);
            HostUpdateStageResult? reconciliation = Reconcile(entries, plan);
            if (reconciliation is not null)
            {
                return reconciliation;
            }

            if (!authorization.IsValidFor(plan))
            {
                return await RecordFailureAsync(plan, HostUpdateStageResult.Rejected("authorization_invalid"), ct);
            }

            await journal.AppendAsync(HostUpdateJournalEntry.Approved(plan, authorization), ct);
            SignedReleaseMetadata metadata = await metadataProvider.GetCurrentAsync(plan.TargetChannel, ct);
            HostUpdatePlanResult refreshed = Evaluate(plan.Request, metadata);
            if (!refreshed.IsEligible || !HostUpdateValidation.HashesEqual(plan.PlanHash, refreshed.PlanHash))
            {
                return await RecordFailureAsync(plan, HostUpdateStageResult.Rejected("plan_or_metadata_changed"), ct);
            }

            await journal.AppendAsync(HostUpdateJournalEntry.StagingIntent(plan, metadata), ct);
            HostUpdateStagingReceipt receipt = await stager.StageAsync(plan, metadata, ct);
            if (!receipt.IsValidFor(plan, metadata))
            {
                return await RecordFailureAsync(plan, HostUpdateStageResult.RecoverableFailure("staging_receipt_invalid"), ct);
            }

            HostUpdateStageResult completed = HostUpdateStageResult.Staged(receipt);
            await journal.AppendAsync(HostUpdateJournalEntry.Staged(plan, completed), ct);
            return completed;
        }
        catch (IOException)
        {
            return HostUpdateStageResult.Rejected("installation_busy");
        }
        catch (InvalidDataException)
        {
            return HostUpdateStageResult.NeedsOperator("journal_unreconciled");
        }
    }

    private async Task<HostUpdateStageResult> RecordFailureAsync(HostUpdatePlan plan, HostUpdateStageResult result, CancellationToken ct)
    {
        await journal.AppendAsync(HostUpdateJournalEntry.Failed(plan, result), ct);
        return result;
    }

    private HostUpdatePlanResult Evaluate(HostUpdatePlanRequest request, SignedReleaseMetadata metadata)
    {
        List<string> reasons = [.. request.Installation.Validate(), .. metadata.ValidateFor(request)];
        IReadOnlyList<string> compatibilityReasons = compatibilityEvaluator.Evaluate(request.Installation, metadata);
        reasons.AddRange(compatibilityReasons.All(HostUpdateValidation.IsRejectionCode) ? compatibilityReasons : ["compatibility_evidence_invalid"]);
        reasons.Sort(StringComparer.Ordinal);
        return new(reasons.Count == 0, ComputePlanHash(request, metadata), metadata.Identity.ManifestDigest, metadata.Identity, request.Installation.RequiredComponents, reasons);
    }

    private static HostUpdateStageResult? Reconcile(IReadOnlyList<HostUpdateJournalEntry> entries, HostUpdatePlan plan)
    {
        IReadOnlyList<HostUpdateJournalEntry> matching = entries.Where(entry =>
            entry.Snapshot.InstallationId == plan.InstallationId &&
            (entry.OperationId == plan.OperationId || entry.IdempotencyKey == plan.IdempotencyKey)).ToList();
        if (matching.Count == 0)
        {
            return null;
        }

        if (matching.Any(entry => !HostUpdateValidation.HashesEqual(entry.Snapshot.PlanHash, plan.PlanHash)))
        {
            return HostUpdateStageResult.NeedsOperator("operation_conflict");
        }

        HostUpdateJournalEntry latest = matching[^1];
        return latest.State switch
        {
            HostUpdateLifecycle.Staged when latest.Snapshot.Receipt is { } receipt => HostUpdateStageResult.Staged(receipt),
            HostUpdateLifecycle.Staging => HostUpdateStageResult.NeedsOperator("staging_intent_unresolved"),
            HostUpdateLifecycle.NeedsOperator or HostUpdateLifecycle.RolledBack => HostUpdateStageResult.NeedsOperator("operation_unreconciled"),
            _ => null,
        };
    }

    private static string ComputePlanHash(HostUpdatePlanRequest request, SignedReleaseMetadata metadata) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', request.CanonicalValue, metadata.CanonicalValue))));
}

/// <summary>Provides the fixed host-local evidence collection surface; it accepts no command, URL, or path.</summary>
public interface IHostUpdateInspector
{
    /// <summary>Collects current bounded evidence for the enrolled installation.</summary>
    Task<HostInstallationEvidence> InspectAsync(string installationId, CancellationToken ct);
}

/// <summary>Represents the complete trusted topology observed on one host installation.</summary>
public sealed record HostInstallationEvidence(
    string InstallationId, string TrustedInstallationFingerprint, string SourceTargetFingerprint, string TopologyFingerprint,
    string Platform, IReadOnlySet<string> RequiredComponents, int ReplicaCount, int RemoteWorkerCount, string WorkerInventoryFingerprint,
    string Provider, string SchemaFingerprint, string ConfigurationFingerprint, string UpdaterVersion, string ResourceFingerprint,
    long AvailableDiskBytes, long RequiredDiskBytes, long RecoveryCapacityBytes, bool WithinMaintenanceWindow, bool RegistryReady, bool BackupDestinationReady)
{
    /// <summary>Gets immutable evidence used by plan hashing.</summary>
    public string CanonicalValue => string.Join('|', InstallationId, TrustedInstallationFingerprint, SourceTargetFingerprint, TopologyFingerprint, Platform,
        string.Join(',', RequiredComponents.OrderBy(value => value, StringComparer.Ordinal)), ReplicaCount, RemoteWorkerCount, WorkerInventoryFingerprint,
        Provider, SchemaFingerprint, ConfigurationFingerprint, UpdaterVersion, ResourceFingerprint, AvailableDiskBytes, RequiredDiskBytes,
        RecoveryCapacityBytes, WithinMaintenanceWindow, RegistryReady, BackupDestinationReady);

    /// <summary>Returns stable codes for missing, stale, or insufficient host evidence.</summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> reasons = [];
        if (!HostUpdateValidation.IsIdentifier(InstallationId) || !HostUpdateValidation.IsDigest(TrustedInstallationFingerprint) || !HostUpdateValidation.IsDigest(SourceTargetFingerprint))
        {
            reasons.Add("installation_untrusted");
        }

        if (!HostUpdateValidation.IsDigest(TopologyFingerprint) || !HostUpdateValidation.IsIdentifier(Platform) || RequiredComponents.Count == 0 || RequiredComponents.Any(component => !HostUpdateValidation.IsIdentifier(component)) || !HostUpdateValidation.IsDigest(WorkerInventoryFingerprint) || ReplicaCount < 1 || RemoteWorkerCount < 0)
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

/// <summary>Constrained planning input that contains no caller-selected components or executable values.</summary>
public sealed record HostUpdatePlanRequest(string OperationId, string IdempotencyKey, string ActorId, string Nonce, string ReasonCode, string SourceChannel, string TargetChannel, string ChannelPolicyRevision, HostInstallationEvidence Installation)
{
    /// <summary>Gets a canonical form used by the immutable plan hash.</summary>
    public string CanonicalValue => string.Join('|', OperationId, IdempotencyKey, ActorId, Nonce, ReasonCode, SourceChannel, TargetChannel, ChannelPolicyRevision, Installation.CanonicalValue);

    /// <summary>Gets whether the closed request contract is safe to evaluate.</summary>
    public bool IsValid => HostUpdateValidation.IsIdentifier(OperationId) && HostUpdateValidation.IsIdentifier(IdempotencyKey) &&
        HostUpdateValidation.IsIdentifier(ActorId) && HostUpdateValidation.IsIdentifier(Nonce) && HostUpdateValidation.IsRejectionCode(ReasonCode) &&
        HostUpdateValidation.IsChannel(SourceChannel) && HostUpdateValidation.IsChannel(TargetChannel) && HostUpdateValidation.IsIdentifier(ChannelPolicyRevision);
}

/// <summary>An immutable topology-derived staging plan.</summary>
public sealed record HostUpdatePlan(HostUpdatePlanRequest Request, string PlanHash, CanonicalReleaseIdentity Identity, IReadOnlySet<string> RequiredComponents)
{
    /// <summary>Gets the operation identifier.</summary>
    public string OperationId => Request.OperationId;
    /// <summary>Gets the idempotency identifier.</summary>
    public string IdempotencyKey => Request.IdempotencyKey;
    /// <summary>Gets the trusted installation identifier.</summary>
    public string InstallationId => Request.Installation.InstallationId;
    /// <summary>Gets the chosen target channel.</summary>
    public string TargetChannel => Request.TargetChannel;
    /// <summary>Gets whether this is a bounded valid plan.</summary>
    public bool IsValid => Request.IsValid && HostUpdateValidation.IsHexHash(PlanHash) &&
        HostUpdateValidation.IsReleaseIdentity(Identity, TargetChannel) && RequiredComponents.SetEquals(Request.Installation.RequiredComponents);
}

/// <summary>Bounded host policy authorization for exactly one plan.</summary>
public sealed record HostUpdateAuthorization(string ActorId, string Nonce, string InstallationId, string PlanHash, string SourceChannel, string TargetChannel, string ChannelPolicyRevision, DateTimeOffset ExpiresAt, HostUpdateAuthorizationKind Kind, bool InsiderWarningAcknowledged, bool StandingPolicyActive)
{
    /// <summary>Returns whether the policy authorization remains current and safe for the plan.</summary>
    public bool IsValidFor(HostUpdatePlan plan) =>
        ExpiresAt > DateTimeOffset.UtcNow && HostUpdateValidation.IsIdentifier(ActorId) && HostUpdateValidation.IsIdentifier(Nonce) &&
        HostUpdateValidation.IsHexHash(PlanHash) && HostUpdateValidation.HashesEqual(PlanHash, plan.PlanHash) &&
        ActorId == plan.Request.ActorId && Nonce == plan.Request.Nonce && InstallationId == plan.InstallationId &&
        SourceChannel == plan.Request.SourceChannel && TargetChannel == plan.TargetChannel && ChannelPolicyRevision == plan.Request.ChannelPolicyRevision &&
        (TargetChannel != "insider" || InsiderWarningAcknowledged) &&
        (Kind == HostUpdateAuthorizationKind.Manual || (StandingPolicyActive && SourceChannel == TargetChannel));
}

/// <summary>Identifies an explicit manual approval or a revocable non-transition standing policy.</summary>
public enum HostUpdateAuthorizationKind { Manual, StandingPolicy }

/// <summary>Returns the fresh plan outcome including topology-derived required components.</summary>
public sealed record HostUpdatePlanResult(bool IsEligible, string PlanHash, string ManifestDigest, CanonicalReleaseIdentity? Identity, IReadOnlySet<string> RequiredComponents, IReadOnlyList<string> Reasons)
{
    /// <summary>Creates a redacted invalid-request outcome without a hash or release identity.</summary>
    public static HostUpdatePlanResult Rejected(string code) => new(false, string.Empty, string.Empty, null, new HashSet<string>(StringComparer.Ordinal), [code]);
}

/// <summary>Canonical immutable identity carried by every journal lifecycle record.</summary>
public sealed record CanonicalReleaseIdentity(string ReleaseId, string Version, string SourceTag, string SourceBranch, string SourceCommit, string AuthorizedBranchHead, string BuildMetadata, string OciReleaseLabel, string OciVersionLabel, string ProvenanceSubjectDigest, string ManifestDigest, string IndexDigest)
{
    /// <summary>Gets the canonical immutable identity representation.</summary>
    public string CanonicalValue => string.Join('|', ReleaseId, Version, SourceTag, SourceBranch, SourceCommit, AuthorizedBranchHead, BuildMetadata, OciReleaseLabel, OciVersionLabel, ProvenanceSubjectDigest, ManifestDigest, IndexDigest);
}

/// <summary>Signed metadata that independently declares every topology-selected component and platform digest.</summary>
public sealed record SignedReleaseMetadata(string Channel, long Sequence, bool SignatureVerified, CanonicalReleaseIdentity Identity, IReadOnlyDictionary<string, string> ComponentPlatformDigests)
{
    /// <summary>Gets components derived from release topology rather than supplied by callers.</summary>
    public IReadOnlySet<string> RequiredComponents => ComponentPlatformDigests.Keys.Select(key => key.Split('/', 2)[0]).ToHashSet(StringComparer.Ordinal);
    /// <summary>Gets immutable metadata used by plan hashing.</summary>
    public string CanonicalValue => string.Join('|', Channel, Sequence, SignatureVerified, Identity.CanonicalValue, string.Join(',', ComponentPlatformDigests.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")));

    /// <summary>Returns stable codes for an invalid target-channel release set.</summary>
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

        if (Sequence <= 0 || string.CompareOrdinal(Identity.Version, "0.0.0") <= 0)
        {
            reasons.Add("release_version_invalid");
        }

        foreach (string component in request.Installation.RequiredComponents)
        {
            if (!ComponentPlatformDigests.TryGetValue($"{component}/{request.Installation.Platform}", out string? digest) || !HostUpdateValidation.IsDigest(digest))
            {
                reasons.Add("release_set_incomplete");
            }
        }

        return reasons;
    }
}

/// <summary>Gets fresh signed metadata for a fixed trusted channel.</summary>
public interface IHostUpdateMetadataProvider { Task<SignedReleaseMetadata> GetCurrentAsync(string channel, CancellationToken ct); }
/// <summary>Evaluates fixed deployment compatibility without controlling a host.</summary>
public interface IHostUpdateCompatibilityEvaluator { IReadOnlyList<string> Evaluate(HostInstallationEvidence installation, SignedReleaseMetadata metadata); }
/// <summary>Stages only the plan's verified immutable bytes and prior recovery set.</summary>
public interface IHostUpdateStager { Task<HostUpdateStagingReceipt> StageAsync(HostUpdatePlan plan, SignedReleaseMetadata metadata, CancellationToken ct); }
/// <summary>Acquires one exclusive host-local lease for a trusted installation.</summary>
public interface IHostUpdateInstallationLock { Task<IAsyncDisposable> AcquireAsync(string installationId, CancellationToken ct); }

/// <summary>Verified receipt binding each selected component byte plus prior recovery configuration.</summary>
public sealed record HostUpdateStagingReceipt(bool IsComplete, string Code, string ManifestDigest, IReadOnlyDictionary<string, string> ComponentPlatformDigests, string PreviousSetDigest, string PreviousConfigurationDigest)
{
    /// <summary>Verifies the receipt against the locked plan and current signed metadata.</summary>
    public bool IsValidFor(HostUpdatePlan plan, SignedReleaseMetadata metadata) =>
        IsComplete && Code == "staged" && HostUpdateValidation.DigestsEqual(ManifestDigest, metadata.Identity.ManifestDigest) &&
        HostUpdateValidation.IsDigest(PreviousSetDigest) && HostUpdateValidation.IsDigest(PreviousConfigurationDigest) &&
        ComponentPlatformDigests.Count == plan.RequiredComponents.Count &&
        plan.RequiredComponents.All(component => ComponentPlatformDigests.TryGetValue($"{component}/{plan.Request.Installation.Platform}", out string? digest) &&
            metadata.ComponentPlatformDigests.TryGetValue($"{component}/{plan.Request.Installation.Platform}", out string? expected) &&
            HostUpdateValidation.DigestsEqual(digest, expected));
}

/// <summary>Reports only a completed receipt, recoverable no-apply failure, or operator-required reconciliation.</summary>
public sealed record HostUpdateStageResult(bool IsStaged, bool IsRecoverableFailure, bool RequiresOperator, string Code, HostUpdateStagingReceipt? Receipt)
{
    /// <summary>Creates a verified staged result.</summary>
    public static HostUpdateStageResult Staged(HostUpdateStagingReceipt receipt) => new(true, false, false, "staged", receipt);
    /// <summary>Creates a recoverable pre-apply staging failure.</summary>
    public static HostUpdateStageResult RecoverableFailure(string code) => new(false, true, false, HostUpdateValidation.IsRejectionCode(code) ? code : "staging_failed", null);
    /// <summary>Creates a policy or evidence rejection.</summary>
    public static HostUpdateStageResult Rejected(string code) => new(false, false, false, HostUpdateValidation.IsRejectionCode(code) ? code : "rejected", null);
    /// <summary>Creates a fail-closed result requiring explicit operator reconciliation.</summary>
    public static HostUpdateStageResult NeedsOperator(string code) => new(false, false, true, HostUpdateValidation.IsRejectionCode(code) ? code : "needs_operator", null);
}

/// <summary>Persists and reads an append-only, redacted host-local operation journal.</summary>
public interface IHostUpdateJournal { Task AppendAsync(HostUpdateJournalEntry entry, CancellationToken ct); Task<IReadOnlyList<HostUpdateJournalEntry>> ReadAsync(CancellationToken ct); }
/// <summary>Defines the typed lifecycle without authorizing apply or recovery behavior.</summary>
public enum HostUpdateLifecycle { Planned, Approved, Staging, Staged, Failed, RolledBack, NeedsOperator }

/// <summary>Redacted durable lifecycle evidence for a single installation operation.</summary>
public sealed record HostUpdateJournalEntry(long Revision, DateTimeOffset RecordedAt, string OperationId, string IdempotencyKey, HostUpdateLifecycle State, string Code, bool Recoverable, HostUpdateJournalSnapshot Snapshot)
{
    /// <summary>Records a fresh preflight outcome.</summary>
    public static HostUpdateJournalEntry Planned(HostUpdatePlanRequest request, SignedReleaseMetadata metadata, HostUpdatePlanResult result) => Create(request, metadata.Identity, HostUpdateLifecycle.Planned, result.IsEligible ? "eligible" : result.Reasons[0], false, result.PlanHash, null);
    /// <summary>Records a bounded approval.</summary>
    public static HostUpdateJournalEntry Approved(HostUpdatePlan plan, HostUpdateAuthorization authorization) => Create(plan.Request, plan.Identity, HostUpdateLifecycle.Approved, authorization.Kind == HostUpdateAuthorizationKind.Manual ? "manual_authorized" : "standing_policy_authorized", false, plan.PlanHash, null);
    /// <summary>Records durable intent immediately before staging bytes.</summary>
    public static HostUpdateJournalEntry StagingIntent(HostUpdatePlan plan, SignedReleaseMetadata metadata) => Create(plan.Request, metadata.Identity, HostUpdateLifecycle.Staging, "intent", false, plan.PlanHash, null);
    /// <summary>Records the verified receipt and retained prior recovery set.</summary>
    public static HostUpdateJournalEntry Staged(HostUpdatePlan plan, HostUpdateStageResult result) => Create(plan.Request, plan.Identity, HostUpdateLifecycle.Staged, result.Code, false, plan.PlanHash, result.Receipt);
    /// <summary>Records a redacted rejection or recoverable outcome.</summary>
    public static HostUpdateJournalEntry Failed(HostUpdatePlan plan, HostUpdateStageResult result) => Create(plan.Request, plan.Identity, result.RequiresOperator ? HostUpdateLifecycle.NeedsOperator : HostUpdateLifecycle.Failed, result.Code, result.IsRecoverableFailure, plan.PlanHash, null);

    private static HostUpdateJournalEntry Create(HostUpdatePlanRequest request, CanonicalReleaseIdentity? identity, HostUpdateLifecycle state, string code, bool recoverable, string planHash, HostUpdateStagingReceipt? receipt) =>
        new(0, DateTimeOffset.UtcNow, request.OperationId, request.IdempotencyKey, state, code, recoverable,
            new(request.Installation.InstallationId, request.ActorId, request.Nonce, request.ReasonCode, request.SourceChannel, request.TargetChannel,
                request.ChannelPolicyRevision, planHash, identity, receipt));
}

/// <summary>Contains only bounded identifiers, canonical identity, and verified receipt digests.</summary>
public sealed record HostUpdateJournalSnapshot(string InstallationId, string ActorId, string Nonce, string ReasonCode, string SourceChannel, string TargetChannel, string ChannelPolicyRevision, string PlanHash, CanonicalReleaseIdentity? Identity, HostUpdateStagingReceipt? Receipt);

/// <summary>Serializes revisions with both a same-process gate and an OS-visible journal lease.</summary>
public sealed class FileHostUpdateJournal : IHostUpdateJournal
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly string journalPath;
    private readonly string lockPath;
    private readonly SemaphoreSlim gate;

    /// <summary>Initializes a journal beneath a host-owned state directory.</summary>
    public FileHostUpdateJournal(string hostStateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostStateDirectory);
        string root = Path.GetFullPath(hostStateDirectory);
        journalPath = Path.Combine(root, "host-update.journal.jsonl");
        lockPath = Path.Combine(root, "host-update.journal.lock");
        gate = Gates.GetOrAdd(journalPath, _ => new SemaphoreSlim(1, 1));
    }

    /// <inheritdoc />
    public async Task AppendAsync(HostUpdateJournalEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
            await using FileStream lease = await AcquireLeaseAsync(ct);
            IReadOnlyList<HostUpdateJournalEntry> entries = await ReadUnsafeAsync(ct);
            HostUpdateJournalEntry durable = entry with { Revision = checked((entries.Count == 0 ? 0 : entries[^1].Revision) + 1) };
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(durable, SerializerOptions) + "\n");
            await using FileStream stream = new(journalPath, new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read, Options = FileOptions.WriteThrough });
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }
        finally { gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HostUpdateJournalEntry>> ReadAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
            await using FileStream lease = await AcquireLeaseAsync(ct);
            return await ReadUnsafeAsync(ct);
        }
        finally { gate.Release(); }
    }

    private async Task<FileStream> AcquireLeaseAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            { return new FileStream(lockPath, new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None, Options = FileOptions.WriteThrough }); }
            catch (IOException) { await Task.Delay(TimeSpan.FromMilliseconds(25), ct); }
        }
    }

    private async Task<IReadOnlyList<HostUpdateJournalEntry>> ReadUnsafeAsync(CancellationToken ct)
    {
        if (!File.Exists(journalPath))
        {
            return [];
        }

        string contents = await File.ReadAllTextAsync(journalPath, ct);
        if (contents.Length == 0 || !contents.EndsWith('\n'))
        {
            throw new InvalidDataException("Journal is incomplete.");
        }

        List<HostUpdateJournalEntry> entries = [];
        foreach (string line in contents.Split('\n', StringSplitOptions.None)[..^1])
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidDataException("Journal contains a blank record.");
            }

            try
            {
                HostUpdateJournalEntry? entry = JsonSerializer.Deserialize<HostUpdateJournalEntry>(line, SerializerOptions);
                if (entry is null || entry.Revision != entries.Count + 1 || !HostUpdateValidation.IsJournalEntry(entry))
                {
                    throw new InvalidDataException("Journal record is invalid.");
                }

                entries.Add(entry);
            }
            catch (JsonException exception) { throw new InvalidDataException("Journal record is corrupt.", exception); }
        }

        return entries;
    }
}

/// <summary>Acquires a host-local exclusive installation file lease.</summary>
public sealed class FileHostUpdateInstallationLock(string hostStateDirectory) : IHostUpdateInstallationLock
{
    /// <inheritdoc />
    public Task<IAsyncDisposable> AcquireAsync(string installationId, CancellationToken ct)
    {
        if (!HostUpdateValidation.IsIdentifier(installationId))
        {
            throw new ArgumentException("Installation identifier is invalid.", nameof(installationId));
        }

        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(hostStateDirectory);
        string path = Path.Combine(hostStateDirectory, $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installationId)))}.lock");
        return Task.FromResult<IAsyncDisposable>(new FileLease(new FileStream(path, new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None, Options = FileOptions.WriteThrough })));
    }

    private sealed class FileLease(FileStream stream) : IAsyncDisposable { public ValueTask DisposeAsync() => stream.DisposeAsync(); }
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
    public static bool HashesEqual(string? left, string? right)
    {
        if (!IsHexHash(left) || !IsHexHash(right))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left!), Convert.FromHexString(right!));
    }

    public static bool DigestsEqual(string? left, string? right)
    {
        if (!IsDigest(left) || !IsDigest(right))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left![7..]),
            Convert.FromHexString(right![7..]));
    }
    public static bool IsReleaseIdentity(CanonicalReleaseIdentity identity, string channel) => identity is not null && IsIdentifier(identity.ReleaseId) && IsVersion(identity.Version) && IsIdentifier(identity.SourceTag) && ((channel == "stable" && identity.SourceBranch == "main") || (channel == "insider" && identity.SourceBranch == "development")) && IsHexHash(identity.SourceCommit) && IsHexHash(identity.AuthorizedBranchHead) && identity.SourceCommit == identity.AuthorizedBranchHead && IsIdentifier(identity.BuildMetadata) && IsIdentifier(identity.OciReleaseLabel) && IsVersion(identity.OciVersionLabel) && identity.ReleaseId == identity.OciReleaseLabel && identity.Version == identity.OciVersionLabel && IsDigest(identity.ProvenanceSubjectDigest) && IsDigest(identity.ManifestDigest) && IsDigest(identity.IndexDigest);
    public static bool IsJournalEntry(HostUpdateJournalEntry entry) => IsIdentifier(entry.OperationId) && IsIdentifier(entry.IdempotencyKey) && Enum.IsDefined(entry.State) && IsRejectionCode(entry.Code) && entry.Snapshot is { } snapshot && IsIdentifier(snapshot.InstallationId) && IsIdentifier(snapshot.ActorId) && IsIdentifier(snapshot.Nonce) && IsRejectionCode(snapshot.ReasonCode) && IsChannel(snapshot.SourceChannel) && IsChannel(snapshot.TargetChannel) && IsIdentifier(snapshot.ChannelPolicyRevision) && IsHexHash(snapshot.PlanHash) && (snapshot.Identity is null || IsReleaseIdentity(snapshot.Identity, snapshot.TargetChannel)) && (snapshot.Receipt is null || snapshot.Receipt.IsComplete && IsDigest(snapshot.Receipt.PreviousSetDigest) && IsDigest(snapshot.Receipt.PreviousConfigurationDigest));
}
