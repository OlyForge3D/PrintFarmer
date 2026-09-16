using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

#pragma warning disable CA1859 // Journal reconciliation deliberately exposes read-only collections.
#pragma warning disable CA1849 // The journal must synchronously commit its durable write before returning.
#pragma warning disable IDISP007 // File leases own streams created by their factory.
#pragma warning disable SA1107 // Closed record contracts retain concise declarations.
#pragma warning disable SA1136 // Adjacent bounded contracts are intentionally grouped.
#pragma warning disable SA1408 // Closed validation predicates retain direct composition.
#pragma warning disable SA1501 // Bounded guards intentionally remain concise.
#pragma warning disable SA1502 // Bounded validation predicates intentionally remain concise.
#pragma warning disable SA1503 // Closed validation guards remain concise.
#pragma warning disable SA1513 // Adjacent bounded contracts are intentionally grouped.
#pragma warning disable SA1514 // Adjacent bounded contracts are intentionally grouped.
#pragma warning disable SA1516 // Adjacent bounded contracts are intentionally grouped.
#pragma warning disable SA1519 // Closed validation guards remain concise.
#pragma warning disable S3878 // Span-compatible delimiter arrays avoid ambiguous string.Split overloads.

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>Provides only host-local preflight, receipt-verified staging, and journal reconciliation.</summary>
public sealed class HostUpdateFoundation(
    IHostUpdateInspector inspector,
    IHostUpdateMetadataProvider metadataProvider,
    IHostUpdateCompatibilityEvaluator compatibilityEvaluator,
    IHostUpdateAuthorizationEvaluator authorizationEvaluator,
    IHostUpdateStager stager,
    IHostUpdateJournal journal,
    IHostUpdateInstallationLock installationLock)
{
    /// <summary>Creates a plan from fresh, enrolled installation evidence rather than caller evidence.</summary>
    public async Task<HostUpdatePlanResult> PlanAsync(HostUpdatePlanRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsValid)
        {
            return HostUpdatePlanResult.Rejected("request_invalid");
        }

        HostInstallationEvidence installation = await inspector.InspectAsync(request.InstallationId, ct);
        SignedReleaseMetadata metadata = await metadataProvider.GetCurrentAsync(request.TargetChannel, ct);
        if (installation is null || metadata is null)
        {
            HostUpdatePlanResult rejected = HostUpdatePlanResult.Rejected("trusted_evidence_invalid");
            await journal.AppendAsync(HostUpdateJournalEntry.Planned(request, metadata, rejected), ct);
            return rejected;
        }

        HostUpdatePlanResult result = Evaluate(request, installation, metadata);
        await journal.AppendAsync(HostUpdateJournalEntry.Planned(request, metadata, result), ct);
        return result;
    }

    /// <summary>Revalidates enrolled evidence and metadata under the installation lock before byte staging.</summary>
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
            if (!TryGetTrustedRequest(entries, plan, out HostUpdatePlanRequest? trustedRequest, out HostUpdateStageResult? reconciliationFailure))
            {
                return reconciliationFailure!;
            }

            HostInstallationEvidence installation = await inspector.InspectAsync(trustedRequest!.InstallationId, ct);
            SignedReleaseMetadata metadata = await metadataProvider.GetCurrentAsync(trustedRequest.TargetChannel, ct);
            if (installation is null || metadata is null)
            {
                return HostUpdateStageResult.NeedsOperator("trusted_evidence_invalid");
            }

            HostUpdatePlanResult refreshed = Evaluate(trustedRequest, installation, metadata);
            HostUpdatePlan trustedPlan = new(trustedRequest, refreshed.PlanHash, metadata.Identity, refreshed.RequiredComponents, installation);
            if (!refreshed.IsEligible || !HostUpdateValidation.HashesEqual(plan.PlanHash, refreshed.PlanHash))
            {
                return await RecordFailureAsync(trustedPlan, metadata, HostUpdateStageResult.NeedsOperator("plan_or_metadata_changed"), ct);
            }

            HostUpdateStageResult? reconciled = Reconcile(entries, trustedPlan, metadata, installation);
            if (reconciled is not null)
            {
                return reconciled;
            }

            if (!authorizationEvaluator.IsAuthorized(authorization, trustedPlan, installation, metadata))
            {
                return await RecordFailureAsync(trustedPlan, metadata, HostUpdateStageResult.Rejected("authorization_invalid"), ct);
            }

            if (metadata.Sequence < installation.CurrentSequence && !authorization.ExplicitDowngradeAllowed)
            {
                return await RecordFailureAsync(trustedPlan, metadata, HostUpdateStageResult.Rejected("downgrade_not_authorized"), ct);
            }

            await journal.AppendAsync(HostUpdateJournalEntry.Approved(trustedPlan, metadata, authorization), ct);
            await journal.AppendAsync(HostUpdateJournalEntry.StagingIntent(trustedPlan, metadata), ct);
            HostUpdateStagingReceipt receipt = await stager.StageAsync(trustedPlan, metadata, ct);
            if (receipt is null || !receipt.IsValidFor(trustedPlan, metadata, installation))
            {
                return await RecordFailureAsync(trustedPlan, metadata, HostUpdateStageResult.NeedsOperator("staging_receipt_invalid"), ct);
            }

            HostUpdateStageResult completed = HostUpdateStageResult.Staged(receipt);
            await journal.AppendAsync(HostUpdateJournalEntry.Staged(trustedPlan, metadata, completed), ct);
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

    private async Task<HostUpdateStageResult> RecordFailureAsync(HostUpdatePlan plan, SignedReleaseMetadata metadata, HostUpdateStageResult result, CancellationToken ct)
    {
        await journal.AppendAsync(HostUpdateJournalEntry.Failed(plan, metadata, result), ct);
        return result;
    }

    private HostUpdatePlanResult Evaluate(HostUpdatePlanRequest request, HostInstallationEvidence? installation, SignedReleaseMetadata? metadata)
    {
        if (!request.IsValid || installation is null || metadata is null)
        {
            return HostUpdatePlanResult.Rejected("trusted_evidence_invalid");
        }

        List<string> reasons = [.. installation.Validate(request.InstallationId), .. metadata.ValidateFor(installation, request)];
        CanonicalReleaseIdentity? identity = HostUpdateValidation.IsReleaseIdentity(metadata.Identity, request.TargetChannel)
            ? metadata.Identity
            : null;
        IReadOnlyList<string>? compatibilityReasons = compatibilityEvaluator.Evaluate(installation, metadata);
        if (compatibilityReasons is null)
        {
            reasons.Add("compatibility_evidence_invalid");
            compatibilityReasons = [];
        }

        reasons.AddRange(compatibilityReasons.All(HostUpdateValidation.IsRejectionCode)
            ? compatibilityReasons
            : ["compatibility_evidence_invalid"]);
        reasons.Sort(StringComparer.Ordinal);
        return new(reasons.Count == 0, ComputePlanHash(request, installation, metadata), identity?.ManifestDigest ?? string.Empty, identity,
            installation.RequiredComponents, reasons, installation);
    }

    private static bool TryGetTrustedRequest(IReadOnlyList<HostUpdateJournalEntry>? entries, HostUpdatePlan plan, out HostUpdatePlanRequest? request, out HostUpdateStageResult? failure)
    {
        request = null;
        failure = null;
        if (entries is null)
        {
            failure = HostUpdateStageResult.NeedsOperator("journal_unreconciled");
            return false;
        }

        IReadOnlyList<HostUpdateJournalEntry> related = entries.Where(entry => HostUpdateValidation.MatchesEitherIdentity(entry, plan)).ToList();
        if (related.Any(entry => !HostUpdateValidation.MatchesBothIdentities(entry, plan)))
        {
            failure = HostUpdateStageResult.NeedsOperator("operation_identity_conflict");
            return false;
        }

        HostUpdateJournalEntry? planned = related.LastOrDefault(entry => entry.State == HostUpdateLifecycle.Planned);
        if (planned is null || !HostUpdateValidation.HashesEqual(planned.Snapshot.PlanHash, plan.PlanHash) ||
            !HostUpdateValidation.TryGetRequest(planned, out request))
        {
            failure = HostUpdateStageResult.NeedsOperator("plan_untrusted");
            return false;
        }

        return true;
    }

    private static HostUpdateStageResult? Reconcile(IReadOnlyList<HostUpdateJournalEntry> entries, HostUpdatePlan plan, SignedReleaseMetadata metadata, HostInstallationEvidence installation)
    {
        IReadOnlyList<HostUpdateJournalEntry> matching = entries.Where(entry => HostUpdateValidation.MatchesBothIdentities(entry, plan)).ToList();
        if (matching.Any(entry => !HostUpdateValidation.HashesEqual(entry.Snapshot.PlanHash, plan.PlanHash)))
        {
            return HostUpdateStageResult.NeedsOperator("operation_conflict");
        }

        HostUpdateJournalEntry latest = matching[^1];
        return latest.State switch
        {
            HostUpdateLifecycle.Staged when latest.Snapshot.Receipt is { } receipt &&
                HostUpdateValidation.SnapshotMatchesTrustedPlan(latest.Snapshot, plan, metadata, installation) &&
                receipt.IsValidFor(plan, metadata, installation) => HostUpdateStageResult.Staged(receipt),
            HostUpdateLifecycle.Staged => HostUpdateStageResult.NeedsOperator("staged_receipt_untrusted"),
            HostUpdateLifecycle.Staging or HostUpdateLifecycle.Failed or HostUpdateLifecycle.NeedsOperator or HostUpdateLifecycle.RolledBack =>
                HostUpdateStageResult.NeedsOperator("operation_unreconciled"),
            _ => null,
        };
    }

    private static string ComputePlanHash(HostUpdatePlanRequest request, HostInstallationEvidence installation, SignedReleaseMetadata metadata) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', request.CanonicalValue, installation.CanonicalValue, metadata.CanonicalValue))));
}

/// <summary>Collects current, trusted enrollment evidence for one fixed installation.</summary>
public interface IHostUpdateInspector { Task<HostInstallationEvidence> InspectAsync(string installationId, CancellationToken ct); }
/// <summary>Gets current signed metadata for one fixed release channel.</summary>
public interface IHostUpdateMetadataProvider { Task<SignedReleaseMetadata> GetCurrentAsync(string channel, CancellationToken ct); }
/// <summary>Evaluates fixed compatibility evidence without controlling a host.</summary>
public interface IHostUpdateCompatibilityEvaluator { IReadOnlyList<string> Evaluate(HostInstallationEvidence installation, SignedReleaseMetadata metadata); }
/// <summary>Validates trusted manual or standing authorization at the issuer/evaluator boundary.</summary>
public interface IHostUpdateAuthorizationEvaluator { bool IsAuthorized(HostUpdateAuthorization authorization, HostUpdatePlan plan, HostInstallationEvidence installation, SignedReleaseMetadata metadata); }
/// <summary>Stages only verified immutable bytes and recovery assets.</summary>
public interface IHostUpdateStager { Task<HostUpdateStagingReceipt> StageAsync(HostUpdatePlan plan, SignedReleaseMetadata metadata, CancellationToken ct); }
/// <summary>Acquires one exclusive host-local lease for a trusted installation.</summary>
public interface IHostUpdateInstallationLock { Task<IAsyncDisposable> AcquireAsync(string installationId, CancellationToken ct); }
/// <summary>Persists and reads the append-only host-local operation journal.</summary>
public interface IHostUpdateJournal { Task AppendAsync(HostUpdateJournalEntry entry, CancellationToken ct); Task<IReadOnlyList<HostUpdateJournalEntry>> ReadAsync(CancellationToken ct); }

/// <summary>Trusted installation, topology, prior-release, and capacity evidence.</summary>
public sealed record HostInstallationEvidence(
    string InstallationId, string TrustedInstallationFingerprint, string TopologyFingerprint, string Platform, IReadOnlySet<string> RequiredComponents,
    string Provider, string SchemaFingerprint, string ConfigurationFingerprint, string UpdaterVersion, long CurrentSequence,
    CanonicalReleaseIdentity PriorReleaseIdentity, string PriorSetDigest, long AvailableDiskBytes, long RequiredDiskBytes, bool WithinMaintenanceWindow,
    bool RegistryReady, bool BackupDestinationReady)
{
    /// <summary>Gets the immutable plan-hash representation.</summary>
    public string CanonicalValue => string.Join('|', InstallationId, TrustedInstallationFingerprint, TopologyFingerprint, Platform,
        string.Join(',', RequiredComponents?.OrderBy(value => value, StringComparer.Ordinal) ?? Enumerable.Empty<string>()), Provider, SchemaFingerprint, ConfigurationFingerprint,
        UpdaterVersion, CurrentSequence, PriorReleaseIdentity?.CanonicalValue, PriorSetDigest, AvailableDiskBytes, RequiredDiskBytes,
        WithinMaintenanceWindow, RegistryReady, BackupDestinationReady);

    /// <summary>Returns rejection codes for invalid or stale enrolled evidence.</summary>
    public IReadOnlyList<string> Validate(string expectedInstallationId)
    {
        List<string> reasons = [];
        if (!HostUpdateValidation.IsIdentifier(InstallationId) || !string.Equals(InstallationId, expectedInstallationId, StringComparison.Ordinal) ||
            !HostUpdateValidation.IsDigest(TrustedInstallationFingerprint))
        {
            reasons.Add("installation_untrusted");
        }

        if (!HostUpdateValidation.IsDigest(TopologyFingerprint) || !HostUpdateValidation.IsIdentifier(Platform) || RequiredComponents is null || RequiredComponents.Count == 0 ||
            RequiredComponents.Any(component => !HostUpdateValidation.IsIdentifier(component)))
        {
            reasons.Add("topology_invalid");
        }

        if (!HostUpdateValidation.IsProvider(Provider) || !HostUpdateValidation.IsDigest(SchemaFingerprint) ||
            !HostUpdateValidation.IsDigest(ConfigurationFingerprint) || PriorReleaseIdentity is null || !HostUpdateValidation.IsReleaseIdentity(PriorReleaseIdentity, PriorReleaseIdentity.Channel) ||
            !HostUpdateValidation.IsDigest(PriorSetDigest) || CurrentSequence < 1)
        {
            reasons.Add("installation_identity_invalid");
        }

        if (!HostUpdateValidation.IsSemanticVersion(UpdaterVersion))
        {
            reasons.Add("updater_version_invalid");
        }

        if (AvailableDiskBytes < RequiredDiskBytes)
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

/// <summary>Constrained caller input. Installation details are always obtained through the inspector.</summary>
public sealed record HostUpdatePlanRequest(string OperationId, string IdempotencyKey, string ActorId, string Nonce, string ReasonCode, string SourceChannel, string TargetChannel, string ChannelPolicyRevision, string InstallationId)
{
    /// <summary>Gets canonical caller input for the plan hash.</summary>
    public string CanonicalValue => string.Join('|', OperationId, IdempotencyKey, ActorId, Nonce, ReasonCode, SourceChannel, TargetChannel, ChannelPolicyRevision, InstallationId);
    /// <summary>Gets whether the fixed non-executable input is safe to inspect.</summary>
    public bool IsValid => HostUpdateValidation.IsIdentifier(OperationId) && HostUpdateValidation.IsIdentifier(IdempotencyKey) &&
        HostUpdateValidation.IsIdentifier(ActorId) && HostUpdateValidation.IsIdentifier(Nonce) && HostUpdateValidation.IsRejectionCode(ReasonCode) &&
        HostUpdateValidation.IsChannel(SourceChannel) && HostUpdateValidation.IsChannel(TargetChannel) &&
        HostUpdateValidation.IsIdentifier(ChannelPolicyRevision) && HostUpdateValidation.IsIdentifier(InstallationId);
}

/// <summary>An immutable topology-derived staging plan.</summary>
public sealed record HostUpdatePlan(HostUpdatePlanRequest Request, string PlanHash, CanonicalReleaseIdentity Identity, IReadOnlySet<string> RequiredComponents, HostInstallationEvidence Installation)
{
    public string OperationId => Request.OperationId;
    public string IdempotencyKey => Request.IdempotencyKey;
    public string InstallationId => Installation.InstallationId;
    public string TargetChannel => Request.TargetChannel;
    public bool IsValid => Request is { IsValid: true } request && Installation is { RequiredComponents: { } installationComponents } installation &&
        RequiredComponents is { } requiredComponents && HostUpdateValidation.IsHexHash(PlanHash) &&
        HostUpdateValidation.IsReleaseIdentity(Identity, TargetChannel) && requiredComponents.SetEquals(installationComponents) &&
        installation.Validate(request.InstallationId).Count == 0;
}

/// <summary>Bounded authorization accepted only through a trusted evaluator.</summary>
public sealed record HostUpdateAuthorization(string ActorId, string Nonce, string InstallationId, string PlanHash, string SourceChannel, string TargetChannel,
    string ChannelPolicyRevision, DateTimeOffset ExpiresAt, HostUpdateAuthorizationKind Kind, bool InsiderWarningAcknowledged, bool StandingPolicyActive,
    bool StandingPolicyRevoked, bool ExplicitDowngradeAllowed)
{
    /// <summary>Checks the closed authorization shape before an evaluator accepts it.</summary>
    public bool IsStructurallyValidFor(HostUpdatePlan? plan) => plan is not null && plan.Request is not null && Enum.IsDefined(Kind) && ExpiresAt > DateTimeOffset.UtcNow &&
        HostUpdateValidation.IsIdentifier(ActorId) && HostUpdateValidation.IsIdentifier(Nonce) && HostUpdateValidation.HashesEqual(PlanHash, plan.PlanHash) &&
        ActorId == plan.Request.ActorId && Nonce == plan.Request.Nonce && InstallationId == plan.InstallationId &&
        SourceChannel == plan.Request.SourceChannel && TargetChannel == plan.TargetChannel && ChannelPolicyRevision == plan.Request.ChannelPolicyRevision &&
        (TargetChannel != "insider" || InsiderWarningAcknowledged) && HasValidPolicyState();

    private bool HasValidPolicyState() => Kind switch
    {
        HostUpdateAuthorizationKind.Manual => !StandingPolicyActive && !StandingPolicyRevoked,
        HostUpdateAuthorizationKind.StandingPolicy => StandingPolicyActive && !StandingPolicyRevoked && SourceChannel == TargetChannel,
        _ => false,
    };
}

/// <summary>Identifies exactly the manual and non-transition standing authorization forms.</summary>
public enum HostUpdateAuthorizationKind { Manual, StandingPolicy }

/// <summary>Contains immutable signed release identity, component bytes, and updater compatibility.</summary>
public sealed record SignedReleaseMetadata(string Channel, long Sequence, bool SignatureVerified, CanonicalReleaseIdentity Identity,
    IReadOnlyDictionary<string, string> ComponentPlatformDigests, string MinimumUpdaterVersion)
{
    public string CanonicalValue => string.Join('|', Channel, Sequence, SignatureVerified, Identity?.CanonicalValue, MinimumUpdaterVersion,
        string.Join(',', ComponentPlatformDigests?.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}") ?? []));
    /// <summary>Returns release-set, version, and updater rejection codes.</summary>
    public IReadOnlyList<string> ValidateFor(HostInstallationEvidence installation, HostUpdatePlanRequest request)
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

        if (!HostUpdateValidation.IsReleaseIdentity(Identity, request.TargetChannel) || Sequence < 1 || !HostUpdateValidation.IsSemanticVersion(MinimumUpdaterVersion))
        {
            reasons.Add("release_identity_invalid");
        }

        if (HostUpdateValidation.TryParseSemanticVersion(installation.UpdaterVersion, out Version installedUpdater) &&
            HostUpdateValidation.TryParseSemanticVersion(MinimumUpdaterVersion, out Version minimumUpdater) &&
            installedUpdater.CompareTo(minimumUpdater) < 0)
        {
            reasons.Add("updater_too_old");
        }

        if (Sequence == installation.CurrentSequence && !HostUpdateValidation.DigestsEqual(Identity?.ManifestDigest, installation.PriorReleaseIdentity?.ManifestDigest))
        {
            reasons.Add("release_sequence_conflict");
        }

        if (installation.RequiredComponents is null || ComponentPlatformDigests is null)
        {
            reasons.Add("release_set_incomplete");
            return reasons;
        }

        foreach (string component in installation.RequiredComponents)
        {
            if (!ComponentPlatformDigests.TryGetValue($"{component}/{installation.Platform}", out string? digest) || !HostUpdateValidation.IsDigest(digest))
            {
                reasons.Add("release_set_incomplete");
            }
        }

        return reasons;
    }
}

/// <summary>Canonical immutable release identity, including its channel.</summary>
public sealed record CanonicalReleaseIdentity(string ReleaseId, string Version, string Channel, string SourceTag, string SourceBranch, string SourceCommit,
    string AuthorizedBranchHead, string BuildMetadata, string OciReleaseLabel, string OciVersionLabel, string ProvenanceSubjectDigest, string ManifestDigest, string IndexDigest)
{
    public string CanonicalValue => string.Join('|', ReleaseId, Version, Channel, SourceTag, SourceBranch, SourceCommit, AuthorizedBranchHead, BuildMetadata,
        OciReleaseLabel, OciVersionLabel, ProvenanceSubjectDigest, ManifestDigest, IndexDigest);
}

/// <summary>Reports fresh planning output derived exclusively from trusted evidence.</summary>
public sealed record HostUpdatePlanResult(bool IsEligible, string PlanHash, string ManifestDigest, CanonicalReleaseIdentity? Identity,
    IReadOnlySet<string> RequiredComponents, IReadOnlyList<string> Reasons, HostInstallationEvidence? Installation)
{
    public static HostUpdatePlanResult Rejected(string code) => new(false, string.Empty, string.Empty, null, new HashSet<string>(), [code], null);
}

/// <summary>Verified staging receipt binding current bytes and the inspected prior release/configuration identity.</summary>
public sealed record HostUpdateStagingReceipt(bool IsComplete, string Code, CanonicalReleaseIdentity Identity, string ManifestDigest,
    IReadOnlyDictionary<string, string> ComponentPlatformDigests, CanonicalReleaseIdentity PriorReleaseIdentity, string PreviousSetDigest, string PreviousConfigurationDigest)
{
    public bool IsValidFor(HostUpdatePlan? plan, SignedReleaseMetadata? metadata, HostInstallationEvidence? installation) =>
        plan is not null && metadata is not null && installation is not null && ComponentPlatformDigests is not null && plan.RequiredComponents is not null &&
        metadata.ComponentPlatformDigests is not null && IsComplete && Code == "staged" && Identity == metadata.Identity && PriorReleaseIdentity == installation.PriorReleaseIdentity &&
        HostUpdateValidation.DigestsEqual(ManifestDigest, metadata.Identity.ManifestDigest) &&
        HostUpdateValidation.DigestsEqual(PreviousSetDigest, installation.PriorSetDigest) &&
        HostUpdateValidation.DigestsEqual(PreviousConfigurationDigest, installation.ConfigurationFingerprint) &&
        ComponentPlatformDigests.Count == plan.RequiredComponents.Count && plan.RequiredComponents.All(component =>
            ComponentPlatformDigests.TryGetValue($"{component}/{installation.Platform}", out string? actual) &&
            metadata.ComponentPlatformDigests.TryGetValue($"{component}/{installation.Platform}", out string? expected) &&
            HostUpdateValidation.DigestsEqual(actual, expected));
}

/// <summary>Reports completed staging, recoverable failure, or explicit operator intervention.</summary>
public sealed record HostUpdateStageResult(bool IsStaged, bool IsRecoverableFailure, bool RequiresOperator, string Code, HostUpdateStagingReceipt? Receipt)
{
    public static HostUpdateStageResult Staged(HostUpdateStagingReceipt receipt) => new(true, false, false, "staged", receipt);
    public static HostUpdateStageResult RecoverableFailure(string code) => new(false, true, false, code, null);
    public static HostUpdateStageResult Rejected(string code) => new(false, false, false, code, null);
    public static HostUpdateStageResult NeedsOperator(string code) => new(false, false, true, code, null);
}

/// <summary>Defines the durable foundation lifecycle; this issue does not execute apply or recovery.</summary>
public enum HostUpdateLifecycle { Planned, Approved, Staging, Staged, Failed, RolledBack, NeedsOperator }

/// <summary>Redacted durable lifecycle evidence. Identities always originate from verified metadata.</summary>
public sealed record HostUpdateJournalEntry(long Revision, DateTimeOffset RecordedAt, string OperationId, string IdempotencyKey, HostUpdateLifecycle State,
    string Code, bool Recoverable, HostUpdateJournalSnapshot Snapshot)
{
    public static HostUpdateJournalEntry Planned(HostUpdatePlanRequest request, SignedReleaseMetadata? metadata, HostUpdatePlanResult result) =>
        Create(request, metadata is not null && HostUpdateValidation.IsReleaseIdentity(metadata.Identity, request.TargetChannel) ? metadata.Identity : null,
            HostUpdateLifecycle.Planned, result.IsEligible ? "eligible" : result.Reasons[0], false, result.PlanHash, null, result.Installation,
            result.RequiredComponents, metadata?.ComponentPlatformDigests);
    public static HostUpdateJournalEntry Approved(HostUpdatePlan plan, SignedReleaseMetadata metadata, HostUpdateAuthorization authorization) =>
        Create(plan.Request, metadata.Identity, HostUpdateLifecycle.Approved, authorization.Kind == HostUpdateAuthorizationKind.Manual ? "manual_authorized" : "standing_policy_authorized", false, plan.PlanHash, null, plan.Installation, plan.RequiredComponents, metadata.ComponentPlatformDigests);
    public static HostUpdateJournalEntry StagingIntent(HostUpdatePlan plan, SignedReleaseMetadata metadata) =>
        Create(plan.Request, metadata.Identity, HostUpdateLifecycle.Staging, "intent", false, plan.PlanHash, null, plan.Installation, plan.RequiredComponents, metadata.ComponentPlatformDigests);
    public static HostUpdateJournalEntry Staged(HostUpdatePlan plan, SignedReleaseMetadata metadata, HostUpdateStageResult result) =>
        Create(plan.Request, metadata.Identity, HostUpdateLifecycle.Staged, result.Code, false, plan.PlanHash, result.Receipt, plan.Installation, plan.RequiredComponents, metadata.ComponentPlatformDigests);
    public static HostUpdateJournalEntry Failed(HostUpdatePlan plan, SignedReleaseMetadata metadata, HostUpdateStageResult result) =>
        Create(plan.Request, HostUpdateValidation.IsReleaseIdentity(metadata.Identity, plan.TargetChannel) ? metadata.Identity : null,
            result.RequiresOperator ? HostUpdateLifecycle.NeedsOperator : HostUpdateLifecycle.Failed, result.Code, result.IsRecoverableFailure, plan.PlanHash, null,
            plan.Installation, plan.RequiredComponents, metadata.ComponentPlatformDigests);

    private static HostUpdateJournalEntry Create(HostUpdatePlanRequest request, CanonicalReleaseIdentity? identity, HostUpdateLifecycle state, string code,
        bool recoverable, string planHash, HostUpdateStagingReceipt? receipt, HostInstallationEvidence? installation = null,
        IReadOnlySet<string>? requiredComponents = null, IReadOnlyDictionary<string, string>? componentPlatformDigests = null)
    {
        bool hasValidEvidence = identity is not null && installation is not null && installation.Validate(request.InstallationId).Count == 0 &&
            HostUpdateValidation.IsDigest(installation.TopologyFingerprint) &&
            HostUpdateValidation.IsIdentifier(installation.Platform) && requiredComponents is { Count: > 0 } &&
            requiredComponents.All(HostUpdateValidation.IsIdentifier) && componentPlatformDigests is not null &&
            componentPlatformDigests.Count == requiredComponents.Count && requiredComponents.All(component =>
                componentPlatformDigests.TryGetValue($"{component}/{installation!.Platform}", out string? digest) &&
                HostUpdateValidation.IsDigest(digest));
        string topologyFingerprint = hasValidEvidence ? installation!.TopologyFingerprint : HostUpdateValidation.RedactedDigest;
        string platform = hasValidEvidence ? installation!.Platform : HostUpdateValidation.RedactedPlatform;
        HashSet<string> components = hasValidEvidence
            ? new(requiredComponents!, StringComparer.Ordinal)
            : new([HostUpdateValidation.RedactedComponent], StringComparer.Ordinal);
        IReadOnlyDictionary<string, string> digests = hasValidEvidence
            ? componentPlatformDigests!
            : new Dictionary<string, string> { [$"{HostUpdateValidation.RedactedComponent}/{HostUpdateValidation.RedactedPlatform}"] = HostUpdateValidation.RedactedDigest };

        return new(0, DateTimeOffset.UtcNow, request.OperationId, request.IdempotencyKey, state, code, recoverable,
            new(request.InstallationId, request.ActorId, request.Nonce, request.ReasonCode, request.SourceChannel,
                request.TargetChannel, request.ChannelPolicyRevision, planHash, identity, receipt, topologyFingerprint, components, platform, digests));
    }
}

/// <summary>Contains redacted identifiers and immutable verification evidence only.</summary>
public sealed record HostUpdateJournalSnapshot(string InstallationId, string ActorId, string Nonce, string ReasonCode, string SourceChannel,
    string TargetChannel, string ChannelPolicyRevision, string PlanHash, CanonicalReleaseIdentity? Identity, HostUpdateStagingReceipt? Receipt,
    string TopologyFingerprint, HashSet<string> RequiredComponents, string Platform, IReadOnlyDictionary<string, string> ComponentPlatformDigests);

/// <summary>Provides a fixed-operation, host-local read-only journal inspection surface.</summary>
public static class HostUpdateJournalInspection
{
    public static async Task<IReadOnlyList<HostUpdateJournalEntry>> ReadSnapshotAsync(string installationId, string hostStateDirectory, CancellationToken ct)
    {
        if (!HostUpdateValidation.IsIdentifier(installationId) || string.IsNullOrWhiteSpace(hostStateDirectory) || !Path.IsPathFullyQualified(hostStateDirectory))
        {
            throw new ArgumentException("A valid installation identifier and absolute host state directory are required.");
        }

        return (await new FileHostUpdateJournal(hostStateDirectory).ReadAsync(ct)).Where(entry => entry.Snapshot.InstallationId == installationId).ToList();
    }
}

/// <summary>Serializes validated journal revisions with same-process and OS-visible leases.</summary>
public sealed class FileHostUpdateJournal : IHostUpdateJournal
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly string journalPath;
    private readonly string lockPath;
    private readonly SemaphoreSlim gate;

    public FileHostUpdateJournal(string hostStateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostStateDirectory);
        string root = Path.GetFullPath(hostStateDirectory);
        journalPath = Path.Combine(root, "host-update.journal.jsonl");
        lockPath = Path.Combine(root, "host-update.journal.lock");
        gate = Gates.GetOrAdd(journalPath, _ => new SemaphoreSlim(1, 1));
    }

    public async Task AppendAsync(HostUpdateJournalEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!HostUpdateValidation.IsJournalEntry(entry))
        {
            throw new InvalidDataException("Journal record is invalid.");
        }

        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
            await using FileStream lease = await AcquireLeaseAsync(ct);
            IReadOnlyList<HostUpdateJournalEntry> entries = await ReadUnsafeAsync(ct);
            HostUpdateJournalEntry? previous = entries.LastOrDefault(existing => HostUpdateValidation.HasSameOperation(existing, entry));
            if (!HostUpdateValidation.IsLifecycleTransition(previous, entry))
            {
                throw new InvalidDataException("Journal lifecycle transition is invalid.");
            }

            HostUpdateJournalEntry durable = entry with { Revision = checked((entries.Count == 0 ? 0 : entries[^1].Revision) + 1) };
            await using FileStream stream = new(journalPath, new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            });
            stream.Seek(0, SeekOrigin.End);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(durable, SerializerOptions) + "\n"), ct);
            await stream.FlushAsync(ct);
            stream.Flush(flushToDisk: true);
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<HostUpdateJournalEntry>> ReadAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        { Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!); await using FileStream lease = await AcquireLeaseAsync(ct); return await ReadUnsafeAsync(ct); }
        finally { gate.Release(); }
    }

    private async Task<FileStream> AcquireLeaseAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(25, ct); }
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
            try
            {
                HostUpdateJournalEntry? entry = string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<HostUpdateJournalEntry>(line, SerializerOptions);
                if (entry is null || entry.Revision != entries.Count + 1 || !HostUpdateValidation.IsJournalEntry(entry) ||
                    !HostUpdateValidation.IsLifecycleTransition(entries.LastOrDefault(existing => HostUpdateValidation.HasSameOperation(existing, entry)), entry))
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
public sealed class FileHostUpdateInstallationLock : IHostUpdateInstallationLock
{
    private readonly string hostStateDirectory;

    public FileHostUpdateInstallationLock(string hostStateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostStateDirectory);
        this.hostStateDirectory = Path.GetFullPath(hostStateDirectory);
    }

    public Task<IAsyncDisposable> AcquireAsync(string installationId, CancellationToken ct)
    {
        if (!HostUpdateValidation.IsIdentifier(installationId))
        {
            throw new ArgumentException("Installation identifier is invalid.", nameof(installationId));
        }

        Directory.CreateDirectory(hostStateDirectory);
        string path = Path.Combine(hostStateDirectory, $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installationId)))}.lock");
        return Task.FromResult<IAsyncDisposable>(new FileLease(path));
    }

    private sealed class FileLease : IAsyncDisposable
    {
        private readonly FileStream stream;

        public FileLease(string path) => stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}

internal static class HostUpdateValidation
{
    private static readonly HashSet<string> Channels = ["stable", "insider"];
    private static readonly HashSet<string> Providers = ["postgres", "sqlserver"];
    public const string RedactedComponent = "redacted";
    public const string RedactedPlatform = "redacted";
    public const string RedactedDigest = "sha256:0000000000000000000000000000000000000000000000000000000000000000";
    public static bool IsIdentifier(string? value) => value is { Length: > 0 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    public static bool IsChannel(string? value) => value is not null && Channels.Contains(value);
    public static bool IsProvider(string? value) => value is not null && Providers.Contains(value);
    public static bool IsDigest(string? value) => value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) && value[7..].All(Uri.IsHexDigit);
    public static bool IsHexHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    public static bool IsRejectionCode(string? value) => value is { Length: > 0 and <= 80 } && value.All(character => char.IsLower(character) || char.IsDigit(character) || character == '_');
    public static bool HashesEqual(string? left, string? right) => IsHexHash(left) && IsHexHash(right) &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left!), Convert.FromHexString(right!));
    public static bool DigestsEqual(string? left, string? right) => IsDigest(left) && IsDigest(right) && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left![7..]), Convert.FromHexString(right![7..]));
    public static bool IsSemanticVersion(string? value) => TryParseSemanticVersion(value, out _);
    public static bool TryParseSemanticVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string core = value.Split(['-', '+'])[0];
        if (!Version.TryParse(core, out Version? parsed) || parsed is null || parsed.Major < 0 || parsed.Minor < 0 || parsed.Build < 0 || parsed.Revision is not -1)
        {
            return false;
        }

        version = parsed;
        return true;
    }
    public static bool IsReleaseIdentity(CanonicalReleaseIdentity? identity, string channel) => identity is not null && IsChannel(channel) &&
        IsCanonicalReleaseVersion(identity.Version, channel) && identity.ReleaseId == $"{channel}:{identity.Version}" &&
        IsChannel(identity.Channel) && identity.Channel == channel && identity.SourceTag == $"v{identity.Version}" && HasExpectedSourceBranch(identity.SourceBranch, channel) &&
        IsHexHash(identity.SourceCommit) && identity.SourceCommit == identity.AuthorizedBranchHead &&
        IsIdentifier(identity.BuildMetadata) && identity.ReleaseId == identity.OciReleaseLabel && identity.Version == identity.OciVersionLabel && IsDigest(identity.ProvenanceSubjectDigest) &&
        IsDigest(identity.ManifestDigest) && IsDigest(identity.IndexDigest);
    private static bool HasExpectedSourceBranch(string sourceBranch, string channel) =>
        (channel == "stable" && sourceBranch == "main") || (channel == "insider" && sourceBranch == "development");
    /// <summary>Accepts only the release-channel version grammar used in immutable publication identities.</summary>
    private static bool IsCanonicalReleaseVersion(string? value, string channel)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        int prereleaseDelimiter = value.IndexOf('-');
        string core = prereleaseDelimiter < 0 ? value : value[..prereleaseDelimiter];
        if (!IsCanonicalVersionCore(core))
        {
            return false;
        }

        if (channel == "stable")
        {
            return value == core;
        }

        if (channel != "insider" || prereleaseDelimiter < 0)
        {
            return false;
        }

        string prerelease = value[(prereleaseDelimiter + 1)..];
        foreach (string label in new[] { "insider", "beta", "rc" })
        {
            string prefix = $"{label}.";
            if (prerelease.StartsWith(prefix, StringComparison.Ordinal))
            {
                return IsCanonicalPositiveInteger(prerelease[prefix.Length..]);
            }
        }

        return false;
    }
    /// <summary>Checks a three-segment numeric version has no ambiguous leading zeroes.</summary>
    private static bool IsCanonicalVersionCore(string value)
    {
        string[] segments = value.Split('.');
        return segments.Length == 3 && segments.All(IsCanonicalNonNegativeInteger);
    }
    /// <summary>Checks one canonical non-negative version segment.</summary>
    private static bool IsCanonicalNonNegativeInteger(string value) =>
        value.Length > 0 && value.All(char.IsAsciiDigit) && (value.Length == 1 || value[0] != '0');
    /// <summary>Checks the required positive insider build number.</summary>
    private static bool IsCanonicalPositiveInteger(string value) =>
        IsCanonicalNonNegativeInteger(value) && value != "0";
    public static bool IsJournalEntry(HostUpdateJournalEntry? entry) => entry is not null && IsIdentifier(entry.OperationId) && IsIdentifier(entry.IdempotencyKey) && Enum.IsDefined(entry.State) &&
        IsRejectionCode(entry.Code) && entry.Snapshot is { } snapshot && IsIdentifier(snapshot.InstallationId) && IsIdentifier(snapshot.ActorId) &&
        IsIdentifier(snapshot.Nonce) && IsRejectionCode(snapshot.ReasonCode) && IsChannel(snapshot.SourceChannel) && IsChannel(snapshot.TargetChannel) &&
        IsIdentifier(snapshot.ChannelPolicyRevision) && (string.IsNullOrEmpty(snapshot.PlanHash) || IsHexHash(snapshot.PlanHash)) &&
        (snapshot.Identity is null || IsReleaseIdentity(snapshot.Identity, snapshot.TargetChannel)) &&
        IsDigest(snapshot.TopologyFingerprint) && IsIdentifier(snapshot.Platform) && snapshot.RequiredComponents is { Count: > 0 } &&
        snapshot.RequiredComponents.All(IsIdentifier) && snapshot.ComponentPlatformDigests is not null &&
        snapshot.ComponentPlatformDigests.Count == snapshot.RequiredComponents.Count &&
        snapshot.RequiredComponents.All(component => snapshot.ComponentPlatformDigests.TryGetValue($"{component}/{snapshot.Platform}", out string? digest) && IsDigest(digest)) &&
        HasValidReceipt(entry.State, snapshot);
    private static bool HasValidReceipt(HostUpdateLifecycle state, HostUpdateJournalSnapshot snapshot)
    {
        if (state != HostUpdateLifecycle.Staged)
        {
            return snapshot.Receipt is null;
        }

        return snapshot.Receipt is { IsComplete: true } receipt && snapshot.Identity is { } identity &&
            IsReleaseIdentity(receipt.Identity, snapshot.TargetChannel) && IsReleaseIdentity(receipt.PriorReleaseIdentity, receipt.PriorReleaseIdentity.Channel) &&
            IsDigest(receipt.ManifestDigest) && IsDigest(receipt.PreviousSetDigest) && IsDigest(receipt.PreviousConfigurationDigest) &&
            receipt.Identity == identity && DigestsEqual(receipt.ManifestDigest, identity.ManifestDigest) &&
            DictionaryEqual(receipt.ComponentPlatformDigests, snapshot.ComponentPlatformDigests);
    }
    public static bool MatchesEitherIdentity(HostUpdateJournalEntry? entry, HostUpdatePlan? plan) => entry?.Snapshot is { } snapshot && plan is not null &&
        snapshot.InstallationId == plan.InstallationId && (entry.OperationId == plan.OperationId || entry.IdempotencyKey == plan.IdempotencyKey);
    public static bool MatchesBothIdentities(HostUpdateJournalEntry? entry, HostUpdatePlan? plan) => entry?.Snapshot is { } snapshot && plan is not null &&
        snapshot.InstallationId == plan.InstallationId && entry.OperationId == plan.OperationId && entry.IdempotencyKey == plan.IdempotencyKey;
    public static bool HasSameOperation(HostUpdateJournalEntry? left, HostUpdateJournalEntry? right) => left?.Snapshot is { } leftSnapshot && right?.Snapshot is { } rightSnapshot &&
        leftSnapshot.InstallationId == rightSnapshot.InstallationId && left.OperationId == right.OperationId && left.IdempotencyKey == right.IdempotencyKey;
    public static bool TryGetRequest(HostUpdateJournalEntry? entry, out HostUpdatePlanRequest? request)
    {
        if (entry?.Snapshot is not { } snapshot)
        {
            request = null;
            return false;
        }

        request = new(entry.OperationId, entry.IdempotencyKey, snapshot.ActorId, snapshot.Nonce, snapshot.ReasonCode,
            snapshot.SourceChannel, snapshot.TargetChannel, snapshot.ChannelPolicyRevision, snapshot.InstallationId);
        return request.IsValid;
    }
    public static bool SnapshotMatchesTrustedPlan(HostUpdateJournalSnapshot? snapshot, HostUpdatePlan? plan, SignedReleaseMetadata? metadata, HostInstallationEvidence? installation) =>
        snapshot is { Identity: { } snapshotIdentity } && plan is not null && metadata is { Identity: { } metadataIdentity } && installation is not null &&
        snapshotIdentity == metadataIdentity && DigestsEqual(snapshotIdentity.ManifestDigest, metadataIdentity.ManifestDigest) &&
        DigestsEqual(snapshot.TopologyFingerprint, installation.TopologyFingerprint) && snapshot.Platform == installation.Platform &&
        snapshot.RequiredComponents is not null && plan.RequiredComponents is not null && snapshot.RequiredComponents.SetEquals(plan.RequiredComponents) &&
        DictionaryEqual(snapshot.ComponentPlatformDigests, metadata.ComponentPlatformDigests) &&
        snapshot.RequiredComponents.All(component => snapshot.ComponentPlatformDigests.TryGetValue($"{component}/{snapshot.Platform}", out string? digest) && IsDigest(digest));
    public static bool DictionaryEqual(IReadOnlyDictionary<string, string>? left, IReadOnlyDictionary<string, string>? right) =>
        left is not null && right is not null && left.Count == right.Count &&
        left.All(pair => right.TryGetValue(pair.Key, out string? value) && DigestsEqual(pair.Value, value));
    public static bool IsLifecycleTransition(HostUpdateJournalEntry? previous, HostUpdateJournalEntry? current) =>
        current is not null && (previous is null ? current.State == HostUpdateLifecycle.Planned :
        (previous.State, current.State) is (HostUpdateLifecycle.Planned, HostUpdateLifecycle.Approved or HostUpdateLifecycle.Failed or HostUpdateLifecycle.NeedsOperator) or
        (HostUpdateLifecycle.Approved, HostUpdateLifecycle.Staging or HostUpdateLifecycle.Failed or HostUpdateLifecycle.NeedsOperator) or
        (HostUpdateLifecycle.Staging, HostUpdateLifecycle.Staged or HostUpdateLifecycle.Failed or HostUpdateLifecycle.NeedsOperator));
}
