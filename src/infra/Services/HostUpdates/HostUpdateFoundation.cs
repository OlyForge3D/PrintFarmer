using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

#pragma warning disable CA1859 // Journal reconciliation deliberately exposes read-only collections.
#pragma warning disable CA1849 // The journal must synchronously commit its durable write before returning.
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
            return await RecordPlanResultAsync(request, metadata, rejected, ct);
        }

        SignedReleaseMetadata projectedMetadata = ProjectMetadata(metadata, installation);
        HostUpdatePlanResult result = Evaluate(request, installation, projectedMetadata);
        return await RecordPlanResultAsync(request, projectedMetadata, result, ct);
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

            metadata = ProjectMetadata(metadata, installation);
            HostUpdatePlanResult refreshed = Evaluate(trustedRequest, installation, metadata);
            HostUpdatePlan trustedPlan = new(trustedRequest, refreshed.PlanHash, metadata.Identity, refreshed.RequiredComponents, installation);
            if (!refreshed.IsEligible || !HostUpdateValidation.HashesEqual(plan.PlanHash, refreshed.PlanHash))
            {
                return await RecordFailureAsync(trustedPlan, metadata, HostUpdateStageResult.NeedsOperator("plan_or_metadata_changed"), null, ct);
            }

            HostUpdateStageResult? reconciled = Reconcile(entries, trustedPlan, metadata, installation);
            if (reconciled is not null)
            {
                return reconciled;
            }

            if (!authorization.IsStructurallyValidFor(trustedPlan) ||
                !authorizationEvaluator.IsAuthorized(authorization, trustedPlan, installation, metadata))
            {
                return await RecordFailureAsync(trustedPlan, metadata, HostUpdateStageResult.Rejected("authorization_invalid"), authorization, ct);
            }

            if (metadata.Sequence < installation.CurrentSequence && !authorization.ExplicitDowngradeAllowed)
            {
                return await RecordFailureAsync(trustedPlan, metadata, HostUpdateStageResult.Rejected("downgrade_not_authorized"), authorization, ct);
            }

            HostUpdateStageResult? journalFailure = await TryAppendAsync(
                HostUpdateJournalEntry.Approved(trustedPlan, metadata, authorization), "journal_approval_append_failure", ct);
            if (journalFailure is not null)
            {
                return journalFailure;
            }

            journalFailure = await TryAppendAsync(
                HostUpdateJournalEntry.StagingIntent(trustedPlan, metadata), "journal_staging_intent_append_failure", ct);
            if (journalFailure is not null)
            {
                return journalFailure;
            }

            HostUpdateStagingReceipt receipt;
            try
            {
                receipt = await stager.StageAsync(trustedPlan, metadata, ct);
            }
            catch (IOException)
            {
                return await RecordStagingFailureAsync(trustedPlan, metadata, "staging_io_failure", ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return await RecordStagingFailureAsync(trustedPlan, metadata, "staging_failure", ct);
            }

            if (receipt is null || !receipt.IsValidFor(trustedPlan, metadata, installation))
            {
                return await RecordStagingFailureAsync(trustedPlan, metadata, "staging_receipt_invalid", ct);
            }

            HostUpdateStageResult completed = HostUpdateStageResult.Staged(receipt);
            journalFailure = await TryAppendAsync(HostUpdateJournalEntry.Staged(trustedPlan, metadata, completed), "journal_staged_append_failure", ct);
            if (journalFailure is not null)
            {
                return journalFailure;
            }

            return completed;
        }
        catch (HostUpdateInstallationBusyException)
        {
            return HostUpdateStageResult.Rejected("installation_busy");
        }
        catch (IOException)
        {
            return HostUpdateStageResult.NeedsOperator("journal_io_failure");
        }
        catch (InvalidDataException)
        {
            return HostUpdateStageResult.NeedsOperator("journal_unreconciled");
        }
    }

    private async Task<HostUpdatePlanResult> RecordPlanResultAsync(HostUpdatePlanRequest request, SignedReleaseMetadata? metadata, HostUpdatePlanResult result, CancellationToken ct)
    {
        try
        {
            await journal.AppendAsync(HostUpdateJournalEntry.Planned(request, metadata, result), ct);
            return result;
        }
        catch (IOException)
        {
            return HostUpdatePlanResult.Rejected("journal_io_failure");
        }
        catch (InvalidDataException)
        {
            return HostUpdatePlanResult.Rejected("journal_unreconciled");
        }
    }

    private async Task<HostUpdateStageResult> RecordFailureAsync(HostUpdatePlan plan, SignedReleaseMetadata metadata, HostUpdateStageResult result,
        HostUpdateAuthorization? authorization, CancellationToken ct)
    {
        return await TryAppendAsync(HostUpdateJournalEntry.Failed(plan, metadata, result, authorization), "journal_failure_append_failure", ct) ?? result;
    }

    private async Task<HostUpdateStageResult> RecordStagingFailureAsync(HostUpdatePlan plan, SignedReleaseMetadata metadata, string code, CancellationToken ct)
    {
        HostUpdateStageResult result = HostUpdateStageResult.NeedsOperator(code);
        return await TryAppendAsync(HostUpdateJournalEntry.Failed(plan, metadata, result), "journal_failure_append_failure", ct) ?? result;
    }

    private async Task<HostUpdateStageResult?> TryAppendAsync(HostUpdateJournalEntry entry, string failureCode, CancellationToken ct)
    {
        try
        {
            await journal.AppendAsync(entry, ct);
            return null;
        }
        catch (IOException)
        {
            return HostUpdateStageResult.NeedsOperator(failureCode);
        }
        catch (InvalidDataException)
        {
            return HostUpdateStageResult.NeedsOperator("journal_unreconciled");
        }
    }

    private HostUpdatePlanResult Evaluate(HostUpdatePlanRequest request, HostInstallationEvidence? installation, SignedReleaseMetadata? metadata)
    {
        if (!request.IsValid || installation is null || metadata is null)
        {
            return HostUpdatePlanResult.Rejected("trusted_evidence_invalid");
        }

        List<string> reasons = [.. installation.Validate(request.InstallationId), .. metadata.ValidateFor(installation, request)];
        if (!string.Equals(request.SourceChannel, installation.PriorReleaseIdentity?.Channel, StringComparison.Ordinal))
        {
            reasons.Add("source_channel_mismatch");
        }
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

        if (matching.Count == 0)
        {
            return null;
        }

        bool hasOtherUnreconciledOperation = entries
            .Where(entry => entry.Snapshot.InstallationId == plan.InstallationId && !HostUpdateValidation.HasSameOperation(entry, matching[0]))
            .GroupBy(entry => (entry.OperationId, entry.IdempotencyKey))
            .Select(group => group.Last())
            .Any(entry => entry.State is HostUpdateLifecycle.Approved or HostUpdateLifecycle.Staging or HostUpdateLifecycle.NeedsOperator or HostUpdateLifecycle.Staged);
        if (hasOtherUnreconciledOperation)
        {
            return HostUpdateStageResult.NeedsOperator("installation_operation_unreconciled");
        }

        HostUpdateJournalEntry latest = matching[^1];
        return latest.State switch
        {
            HostUpdateLifecycle.Staged when latest.Snapshot.Receipt is { } receipt &&
                HostUpdateValidation.SnapshotMatchesTrustedPlan(latest.Snapshot, plan, metadata, installation) &&
                receipt.IsValidFor(plan, metadata, installation) => HostUpdateStageResult.Staged(receipt),
            HostUpdateLifecycle.Staged => HostUpdateStageResult.NeedsOperator("staged_receipt_untrusted"),
            HostUpdateLifecycle.Approved or HostUpdateLifecycle.Staging or HostUpdateLifecycle.Failed or HostUpdateLifecycle.NeedsOperator or HostUpdateLifecycle.RolledBack =>
                HostUpdateStageResult.NeedsOperator("operation_unreconciled"),
            _ => null,
        };
    }

    private static string ComputePlanHash(HostUpdatePlanRequest request, HostInstallationEvidence installation, SignedReleaseMetadata metadata) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', request.CanonicalValue, installation.CanonicalValue, metadata.CanonicalValue))));

    private static SignedReleaseMetadata ProjectMetadata(SignedReleaseMetadata metadata, HostInstallationEvidence installation) =>
        metadata with
        {
            ComponentPlatformDigests = installation.RequiredComponents is null
                ? new Dictionary<string, string>()
                : installation.RequiredComponents
                    .Select(component => SignedUpdateManifestValidator.PlatformKey(component, installation.Platform))
                    .Where(key => metadata.ComponentPlatformDigests?.ContainsKey(key) == true)
                    .ToDictionary(key => key, key => metadata.ComponentPlatformDigests![key], StringComparer.Ordinal),
        };
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

        if (AvailableDiskBytes < 0 || RequiredDiskBytes <= 0 || AvailableDiskBytes < RequiredDiskBytes)
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
        HostUpdateAuthorizationKind.StandingPolicy => StandingPolicyActive && !StandingPolicyRevoked && !ExplicitDowngradeAllowed && SourceChannel == TargetChannel,
        _ => false,
    };
}

/// <summary>Identifies exactly the manual and non-transition standing authorization forms.</summary>
public enum HostUpdateAuthorizationKind { Manual, StandingPolicy }

/// <summary>Contains immutable signed release identity, component bytes, and updater compatibility.</summary>
public sealed record SignedReleaseMetadata(string Channel, long Sequence, bool SignatureVerified, CanonicalReleaseIdentity Identity,
    IReadOnlyDictionary<string, string> ComponentPlatformDigests, string MinimumUpdaterVersion,
    IReadOnlyDictionary<string, string>? ComponentIndexDigests = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? ComponentPlatforms = null)
{
    public string CanonicalValue => string.Join('|', Channel, Sequence, SignatureVerified, Identity?.CanonicalValue, MinimumUpdaterVersion,
        string.Join(',', ComponentPlatformDigests?.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}") ?? []),
        string.Join(',', ComponentIndexDigests?.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}") ?? []));
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
            if (!ComponentPlatformDigests.TryGetValue(
                    SignedUpdateManifestValidator.PlatformKey(component, installation.Platform),
                    out string? digest)
                || !HostUpdateValidation.IsDigest(digest))
            {
                reasons.Add("release_set_incomplete");
            }
        }

        return reasons;
    }
}

/// <summary>Canonical immutable release identity, including its channel.</summary>
public sealed record CanonicalReleaseIdentity(string ReleaseId, string Version, string Channel, string SourceTag, string SourceBranch, string SourceCommit,
    string AuthorizedBranchHead, string BuildMetadata, string OciReleaseLabel, string OciVersionLabel, string ManifestDigest)
{
    public string CanonicalValue => string.Join('|', ReleaseId, Version, Channel, SourceTag, SourceBranch, SourceCommit, AuthorizedBranchHead, BuildMetadata,
        OciReleaseLabel, OciVersionLabel, ManifestDigest);
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
            ComponentPlatformDigests.TryGetValue(SignedUpdateManifestValidator.PlatformKey(component, installation.Platform), out string? actual) &&
            metadata.ComponentPlatformDigests.TryGetValue(SignedUpdateManifestValidator.PlatformKey(component, installation.Platform), out string? expected) &&
            HostUpdateValidation.DigestsEqual(actual, expected));
}

/// <summary>Reports completed staging, rejection, or explicit operator intervention.</summary>
public sealed record HostUpdateStageResult(bool IsStaged, bool RequiresOperator, string Code, HostUpdateStagingReceipt? Receipt)
{
    public static HostUpdateStageResult Staged(HostUpdateStagingReceipt receipt) => new(true, false, "staged", receipt);
    public static HostUpdateStageResult Rejected(string code) => new(false, false, code, null);
    public static HostUpdateStageResult NeedsOperator(string code) => new(false, true, code, null);
}

/// <summary>Defines the durable foundation lifecycle; this issue does not execute apply or recovery.</summary>
public enum HostUpdateLifecycle { Planned, Approved, Staging, Staged, Failed, RolledBack, NeedsOperator }

/// <summary>Redacted durable lifecycle evidence. Identities always originate from verified metadata.</summary>
public sealed record HostUpdateJournalEntry(long Revision, DateTimeOffset RecordedAt, string OperationId, string IdempotencyKey, HostUpdateLifecycle State,
    string Code, HostUpdateJournalSnapshot Snapshot, string IntegrityHash = "")
{
    public static HostUpdateJournalEntry Planned(HostUpdatePlanRequest request, SignedReleaseMetadata? metadata, HostUpdatePlanResult result) =>
        Create(request, metadata is not null && HostUpdateValidation.IsReleaseIdentity(metadata.Identity, request.TargetChannel) ? metadata.Identity : null,
            HostUpdateLifecycle.Planned, result.IsEligible ? "eligible" : result.Reasons[0], result.PlanHash, null, result.Installation,
            result.RequiredComponents, metadata?.ComponentPlatformDigests);
    public static HostUpdateJournalEntry Approved(HostUpdatePlan plan, SignedReleaseMetadata metadata, HostUpdateAuthorization authorization) =>
        Create(plan.Request, metadata.Identity, HostUpdateLifecycle.Approved, authorization.Kind == HostUpdateAuthorizationKind.Manual ? "manual_authorized" : "standing_policy_authorized", plan.PlanHash, null, plan.Installation, plan.RequiredComponents, metadata.ComponentPlatformDigests, authorization);
    public static HostUpdateJournalEntry StagingIntent(HostUpdatePlan plan, SignedReleaseMetadata metadata) =>
        Create(plan.Request, metadata.Identity, HostUpdateLifecycle.Staging, "intent", plan.PlanHash, null, plan.Installation, plan.RequiredComponents, metadata.ComponentPlatformDigests);
    public static HostUpdateJournalEntry Staged(HostUpdatePlan plan, SignedReleaseMetadata metadata, HostUpdateStageResult result) =>
        Create(plan.Request, metadata.Identity, HostUpdateLifecycle.Staged, result.Code, plan.PlanHash, result.Receipt, plan.Installation, plan.RequiredComponents, metadata.ComponentPlatformDigests);
    public static HostUpdateJournalEntry Failed(HostUpdatePlan plan, SignedReleaseMetadata metadata, HostUpdateStageResult result, HostUpdateAuthorization? authorization = null) =>
        Create(plan.Request, HostUpdateValidation.IsReleaseIdentity(metadata.Identity, plan.TargetChannel) ? metadata.Identity : null,
            result.RequiresOperator ? HostUpdateLifecycle.NeedsOperator : HostUpdateLifecycle.Failed, result.Code, plan.PlanHash, null,
            plan.Installation, plan.RequiredComponents, metadata.ComponentPlatformDigests, authorization);

    private static HostUpdateJournalEntry Create(HostUpdatePlanRequest request, CanonicalReleaseIdentity? identity, HostUpdateLifecycle state, string code,
        string planHash, HostUpdateStagingReceipt? receipt, HostInstallationEvidence? installation = null,
        IReadOnlySet<string>? requiredComponents = null, IReadOnlyDictionary<string, string>? componentPlatformDigests = null,
        HostUpdateAuthorization? authorization = null)
    {
        bool hasValidEvidence = identity is not null && installation is not null && installation.Validate(request.InstallationId).Count == 0 &&
            HostUpdateValidation.IsDigest(installation.TopologyFingerprint) &&
            HostUpdateValidation.IsIdentifier(installation.Platform) && requiredComponents is { Count: > 0 } &&
            requiredComponents.All(HostUpdateValidation.IsIdentifier) && componentPlatformDigests is not null &&
            componentPlatformDigests.Count == requiredComponents.Count && requiredComponents.All(component =>
                componentPlatformDigests.TryGetValue(
                    SignedUpdateManifestValidator.PlatformKey(component, installation!.Platform),
                    out string? digest) &&
                HostUpdateValidation.IsDigest(digest));
        string topologyFingerprint = hasValidEvidence ? installation!.TopologyFingerprint : HostUpdateValidation.RedactedDigest;
        string platform = hasValidEvidence ? installation!.Platform : HostUpdateValidation.RedactedPlatform;
        HashSet<string> components = hasValidEvidence
            ? new(requiredComponents!, StringComparer.Ordinal)
            : new([HostUpdateValidation.RedactedComponent], StringComparer.Ordinal);
        IReadOnlyDictionary<string, string> digests = hasValidEvidence
            ? componentPlatformDigests!
            : new Dictionary<string, string>
            {
                [SignedUpdateManifestValidator.PlatformKey(
                    HostUpdateValidation.RedactedComponent,
                    HostUpdateValidation.RedactedPlatform)] = HostUpdateValidation.RedactedDigest,
            };

        return new(0, DateTimeOffset.UtcNow, request.OperationId, request.IdempotencyKey, state, code,
            new(request.InstallationId, request.ActorId, request.Nonce, request.ReasonCode, request.SourceChannel,
                request.TargetChannel, request.ChannelPolicyRevision, planHash, identity, receipt, topologyFingerprint, components, platform, digests,
                HostUpdateAuthorizationAudit.Create(authorization, state == HostUpdateLifecycle.Approved)));
    }
}

/// <summary>Contains redacted identifiers and immutable verification evidence only.</summary>
public sealed record HostUpdateJournalSnapshot(string InstallationId, string ActorId, string Nonce, string ReasonCode, string SourceChannel,
    string TargetChannel, string ChannelPolicyRevision, string PlanHash, CanonicalReleaseIdentity? Identity, HostUpdateStagingReceipt? Receipt,
    string TopologyFingerprint, HashSet<string> RequiredComponents, string Platform, IReadOnlyDictionary<string, string> ComponentPlatformDigests,
    HostUpdateAuthorizationAudit? AuthorizationAudit = null);

/// <summary>Contains bounded, redacted authorization policy evidence for lifecycle audit.</summary>
public sealed record HostUpdateAuthorizationAudit(
    string ActorId, string Nonce, string InstallationId, string PlanHash, string SourceChannel, string TargetChannel,
    string ChannelPolicyRevision, string Kind, bool Accepted, bool InsiderWarningAcknowledged, bool ExplicitDowngradeAllowed,
    DateTimeOffset? ExpiresAt, bool StandingPolicyActive, bool StandingPolicyRevoked)
{
    public static HostUpdateAuthorizationAudit? Create(HostUpdateAuthorization? authorization, bool accepted) => authorization is null
        ? null
        : new(
            HostUpdateValidation.SafeIdentifier(authorization.ActorId),
            HostUpdateValidation.SafeIdentifier(authorization.Nonce),
            HostUpdateValidation.SafeIdentifier(authorization.InstallationId),
            HostUpdateValidation.SafeHash(authorization.PlanHash),
            HostUpdateValidation.SafeChannel(authorization.SourceChannel),
            HostUpdateValidation.SafeChannel(authorization.TargetChannel),
            HostUpdateValidation.SafeIdentifier(authorization.ChannelPolicyRevision),
            Enum.IsDefined(authorization.Kind) ? authorization.Kind.ToString().ToLowerInvariant() : "invalid",
            accepted,
            authorization.InsiderWarningAcknowledged,
            authorization.ExplicitDowngradeAllowed,
            authorization.ExpiresAt == default ? null : authorization.ExpiresAt,
            authorization.StandingPolicyActive,
            authorization.StandingPolicyRevoked);
}

/// <summary>Provides a fixed-operation, host-local read-only journal inspection surface.</summary>
public static class HostUpdateJournalInspection
{
    public static async Task<IReadOnlyList<HostUpdateJournalEntry>> ReadSnapshotAsync(string installationId, string hostStateDirectory, CancellationToken ct)
    {
        if (!HostUpdateValidation.IsIdentifier(installationId) || !HostUpdateValidation.IsHostLocalStateDirectory(hostStateDirectory))
        {
            throw new ArgumentException("A valid installation identifier and host-local state directory are required.");
        }

        return (await FileHostUpdateJournal.ReadExistingAsync(hostStateDirectory, ct)).Where(entry => entry.Snapshot.InstallationId == installationId).ToList();
    }
}

/// <summary>Serializes validated journal revisions with same-process and OS-visible leases.</summary>
public sealed class FileHostUpdateJournal : IHostUpdateJournal
{
    private readonly string journalPath;
    private readonly string lockPath;
    private readonly TimeSpan acquisitionTimeout;

    public FileHostUpdateJournal(string hostStateDirectory, TimeSpan? acquisitionTimeout = null)
    {
        if (!HostUpdateValidation.IsHostLocalStateDirectory(hostStateDirectory))
        {
            throw new ArgumentException("A host-local fully-qualified state directory is required.", nameof(hostStateDirectory));
        }

        if (acquisitionTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(acquisitionTimeout));
        }

        string root = Path.GetFullPath(hostStateDirectory);
        journalPath = Path.Combine(root, "host-update.journal.jsonl");
        lockPath = Path.Combine(root, "host-update.journal.lock");
        this.acquisitionTimeout = acquisitionTimeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task AppendAsync(HostUpdateJournalEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!HostUpdateValidation.IsJournalEntry(entry))
        {
            throw new InvalidDataException("Journal record is invalid.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        await using FileStream lease = await AcquireLeaseAsync(ct);
        IReadOnlyList<HostUpdateJournalEntry> entries = await ReadUnsafeAsync(ct);
        HostUpdateJournalEntry? previous = entries.LastOrDefault(existing => HostUpdateValidation.HasSameOperation(existing, entry));
        if (!HostUpdateValidation.IsLifecycleTransition(previous, entry))
        {
            throw new InvalidDataException("Journal lifecycle transition is invalid.");
        }

        HostUpdateJournalEntry durable = entry with
        {
            Revision = checked((entries.Count == 0 ? 0 : entries[^1].Revision) + 1),
            IntegrityHash = string.Empty,
        };
        durable = durable with { IntegrityHash = HostUpdateValidation.ComputeJournalIntegrityHash(durable, entries.Count == 0 ? null : entries[^1].IntegrityHash) };
        await using FileStream stream = new(journalPath, new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
        });
        stream.Seek(0, SeekOrigin.End);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(durable, HostUpdateValidation.JournalSerializerOptions) + "\n"), ct);
        await stream.FlushAsync(ct);
        stream.Flush(flushToDisk: true);
    }

    public async Task<IReadOnlyList<HostUpdateJournalEntry>> ReadAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        await using FileStream lease = await AcquireLeaseAsync(ct);
        return await ReadUnsafeAsync(ct);
    }

    /// <summary>Reads an existing journal without creating state, directories, or a lock file.</summary>
    public static async Task<IReadOnlyList<HostUpdateJournalEntry>> ReadExistingAsync(string hostStateDirectory, CancellationToken ct)
    {
        if (!HostUpdateValidation.IsHostLocalStateDirectory(hostStateDirectory))
        {
            throw new ArgumentException("A host-local fully-qualified state directory is required.", nameof(hostStateDirectory));
        }

        string path = Path.Combine(Path.GetFullPath(hostStateDirectory), "host-update.journal.jsonl");
        return !File.Exists(path) ? [] : await ReadContentsAsync(await File.ReadAllTextAsync(path, ct));
    }

    private async Task<FileStream> AcquireLeaseAsync(CancellationToken ct)
    {
        return await HostUpdateFileLease.AcquireAsync(
            lockPath,
            acquisitionTimeout,
            null,
            path => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None),
            ct);
    }

    private async Task<IReadOnlyList<HostUpdateJournalEntry>> ReadUnsafeAsync(CancellationToken ct)
    {
        if (!File.Exists(journalPath))
        {
            return [];
        }

        return await ReadContentsAsync(await File.ReadAllTextAsync(journalPath, ct));
    }

    private static Task<IReadOnlyList<HostUpdateJournalEntry>> ReadContentsAsync(string contents)
    {
        if (contents.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<HostUpdateJournalEntry>>([]);
        }

        if (!contents.EndsWith('\n'))
        {
            throw new InvalidDataException("Journal is incomplete.");
        }

        List<HostUpdateJournalEntry> entries = [];
        foreach (string line in contents.Split('\n', StringSplitOptions.None)[..^1])
        {
            try
            {
                HostUpdateJournalEntry? entry = string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<HostUpdateJournalEntry>(line, HostUpdateValidation.JournalSerializerOptions);
                if (entry is null || entry.Revision != entries.Count + 1 || !HostUpdateValidation.IsJournalEntry(entry) ||
                    !HostUpdateValidation.HashesEqual(entry.IntegrityHash, HostUpdateValidation.ComputeJournalIntegrityHash(entry, entries.LastOrDefault()?.IntegrityHash)) ||
                    !HostUpdateValidation.IsLifecycleTransition(entries.LastOrDefault(existing => HostUpdateValidation.HasSameOperation(existing, entry)), entry))
                {
                    throw new InvalidDataException("Journal record is invalid.");
                }

                entries.Add(entry);
            }
            catch (JsonException exception) { throw new InvalidDataException("Journal record is corrupt.", exception); }
        }
        return Task.FromResult<IReadOnlyList<HostUpdateJournalEntry>>(entries);
    }
}

/// <summary>Acquires a host-local exclusive installation file lease.</summary>
public sealed class FileHostUpdateInstallationLock : IHostUpdateInstallationLock
{
    private readonly string hostStateDirectory;

    private readonly TimeSpan acquisitionTimeout;

    public FileHostUpdateInstallationLock(string hostStateDirectory, TimeSpan? acquisitionTimeout = null)
    {
        if (!HostUpdateValidation.IsHostLocalStateDirectory(hostStateDirectory))
        {
            throw new ArgumentException("A host-local fully-qualified state directory is required.", nameof(hostStateDirectory));
        }

        if (acquisitionTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(acquisitionTimeout));
        }

        this.hostStateDirectory = Path.GetFullPath(hostStateDirectory);
        this.acquisitionTimeout = acquisitionTimeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<IAsyncDisposable> AcquireAsync(string installationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!HostUpdateValidation.IsIdentifier(installationId))
        {
            throw new ArgumentException("Installation identifier is invalid.", nameof(installationId));
        }

        Directory.CreateDirectory(hostStateDirectory);
        string path = Path.Combine(hostStateDirectory, $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installationId)))}.lock");
        return await HostUpdateFileLease.AcquireAsync(
            path,
            acquisitionTimeout,
            exception => new HostUpdateInstallationBusyException($"The installation lock for '{installationId}' is busy.", exception),
            lockPath => new FileLease(lockPath),
            ct);
    }

    private sealed class FileLease : IAsyncDisposable
    {
        private readonly FileStream stream;

        public FileLease(string path) => stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}

/// <summary>Indicates that the bounded installation lease could not be acquired.</summary>
public sealed class HostUpdateInstallationBusyException : IOException
{
    public HostUpdateInstallationBusyException() { }
    public HostUpdateInstallationBusyException(string message) : base(message) { }
    public HostUpdateInstallationBusyException(string message, Exception innerException) : base(message, innerException) { }
}

internal static class HostUpdateFileLease
{
    public static async Task<TLease> AcquireAsync<TLease>(
        string path,
        TimeSpan acquisitionTimeout,
        Func<IOException, IOException>? timeoutExceptionFactory,
        Func<string, TLease> leaseFactory,
        CancellationToken ct)
        where TLease : IAsyncDisposable
    {
        ct.ThrowIfCancellationRequested();
        DateTimeOffset deadline = DateTimeOffset.UtcNow + acquisitionTimeout;
        TimeSpan delay = TimeSpan.FromMilliseconds(25);
        while (true)
        {
            try
            {
                return leaseFactory(path);
            }
            catch (IOException exception) when (HostUpdateValidation.IsLockContention(exception))
            {
                TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw timeoutExceptionFactory?.Invoke(exception) ?? new IOException("The journal lock is busy.", exception);
                }

                await Task.Delay(delay <= remaining ? delay : remaining, ct);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 500));
            }
        }
    }
}

internal static class HostUpdateValidation
{
    internal static readonly JsonSerializerOptions JournalSerializerOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly HashSet<string> Channels = ["stable", "insider"];
    private static readonly HashSet<string> Providers = ["postgres", "sqlserver"];
    public const string RedactedComponent = "redacted";
    public const string RedactedPlatform = "redacted";
    public const string RedactedDigest = "sha256:0000000000000000000000000000000000000000000000000000000000000000";
    public const string RedactedHash = "0000000000000000000000000000000000000000000000000000000000000000";
    private const int WindowsSharingViolation = 32;
    private const int WindowsLockViolation = 33;
    private const int LinuxTryAgain = 11;
    private const int MacOsTryAgain = 35;
    public static bool IsIdentifier(string? value) => value is { Length: > 0 and <= 128 } && value is not "." and not ".." && value[0] != '.' &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    public static bool IsHostLocalStateDirectory(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal) ||
            value.StartsWith(@"\??\", StringComparison.Ordinal) || value.StartsWith(@"\.\", StringComparison.Ordinal) ||
            value.StartsWith("//?/", StringComparison.Ordinal) || value.StartsWith("//./", StringComparison.Ordinal))
        {
            return false;
        }

        bool isWindows = OperatingSystem.IsWindows();
        bool acceptedGrammar = isWindows
            ? value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/'
            : value[0] == '/' && !value.StartsWith("//", StringComparison.Ordinal);
        if (!acceptedGrammar || value.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(value);
            return Path.IsPathFullyQualified(value) && Path.IsPathFullyQualified(fullPath) &&
                (isWindows
                    ? fullPath.Length >= 3 && char.IsAsciiLetter(fullPath[0]) && fullPath[1] == ':' && fullPath[2] is '\\' or '/'
                    : fullPath[0] == '/' && !fullPath.StartsWith("//", StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
    public static bool IsChannel(string? value) => value is not null && Channels.Contains(value);
    public static bool IsProvider(string? value) => value is not null && Providers.Contains(value);
    public static bool IsDigest(string? value) => value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) && value[7..].All(Uri.IsHexDigit);
    public static bool IsHexHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    public static bool IsRejectionCode(string? value) => value is { Length: > 0 and <= 80 } &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    public static bool IsLockContention(IOException exception)
    {
        int errorCode = exception.HResult & 0xFFFF;
        return OperatingSystem.IsWindows()
            ? errorCode is WindowsSharingViolation or WindowsLockViolation
            : OperatingSystem.IsMacOS() ? errorCode == MacOsTryAgain : errorCode == LinuxTryAgain;
    }
    public static bool HashesEqual(string? left, string? right) => IsHexHash(left) && IsHexHash(right) &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left!), Convert.FromHexString(right!));
    public static string SafeIdentifier(string? value) => IsIdentifier(value) ? value! : RedactedComponent;
    public static string SafeHash(string? value) => IsHexHash(value) ? value! : RedactedHash;
    public static string SafeChannel(string? value) => IsChannel(value) ? value! : "redacted";
    public static string ComputeJournalIntegrityHash(HostUpdateJournalEntry entry, string? previousHash)
    {
        HostUpdateJournalEntry canonical = entry with { IntegrityHash = string.Empty };
        string payload = JsonSerializer.Serialize(canonical, JournalSerializerOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{previousHash ?? string.Empty}\n{payload}")));
    }
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
        IsIdentifier(identity.BuildMetadata) && identity.ReleaseId == identity.OciReleaseLabel && identity.Version == identity.OciVersionLabel &&
        IsDigest(identity.ManifestDigest);
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
        snapshot.RequiredComponents.All(component => snapshot.ComponentPlatformDigests.TryGetValue(
            SignedUpdateManifestValidator.PlatformKey(component, snapshot.Platform),
            out string? digest) && IsDigest(digest)) &&
        (string.IsNullOrEmpty(entry.IntegrityHash) || IsHexHash(entry.IntegrityHash)) && HasValidReceipt(entry.State, snapshot) && HasValidAuthorizationAudit(entry, snapshot);
    private static bool HasValidAuthorizationAudit(HostUpdateJournalEntry entry, HostUpdateJournalSnapshot snapshot) =>
        entry.State == HostUpdateLifecycle.Approved
            ? snapshot.AuthorizationAudit is { Accepted: true } accepted && HasValidAuthorizationAuditTuple(accepted) &&
                HasMatchingAcceptedAuthorizationAudit(entry, snapshot, accepted) && accepted.ExpiresAt > entry.RecordedAt && HasValidAcceptedPolicy(accepted)
            : entry.Code is "authorization_invalid" or "downgrade_not_authorized"
                ? snapshot.AuthorizationAudit is { Accepted: false } rejected && HasValidAuthorizationAuditTuple(rejected)
                : snapshot.AuthorizationAudit is null;
    private static bool HasValidAuthorizationAuditTuple(HostUpdateAuthorizationAudit audit) =>
        IsIdentifier(audit.ActorId) && IsIdentifier(audit.Nonce) && IsIdentifier(audit.InstallationId) && IsHexHash(audit.PlanHash) &&
        (IsChannel(audit.SourceChannel) || audit.SourceChannel == "redacted") &&
        (IsChannel(audit.TargetChannel) || audit.TargetChannel == "redacted") &&
        IsIdentifier(audit.ChannelPolicyRevision) && (!audit.Accepted || audit.ExpiresAt is not null);
    private static bool HasMatchingAcceptedAuthorizationAudit(HostUpdateJournalEntry entry, HostUpdateJournalSnapshot snapshot, HostUpdateAuthorizationAudit audit) =>
        audit.ActorId == snapshot.ActorId && audit.Nonce == snapshot.Nonce && audit.InstallationId == snapshot.InstallationId &&
        HashesEqual(audit.PlanHash, snapshot.PlanHash) && audit.SourceChannel == snapshot.SourceChannel && audit.TargetChannel == snapshot.TargetChannel &&
        audit.ChannelPolicyRevision == snapshot.ChannelPolicyRevision && audit.Kind switch
        {
            "manual" => entry.Code == "manual_authorized",
            "standingpolicy" => entry.Code == "standing_policy_authorized",
            _ => false,
        } && (audit.TargetChannel != "insider" || audit.InsiderWarningAcknowledged);
    private static bool HasValidAcceptedPolicy(HostUpdateAuthorizationAudit audit) => audit.Kind switch
    {
        "manual" => !audit.StandingPolicyActive && !audit.StandingPolicyRevoked,
        "standingpolicy" => audit.StandingPolicyActive && !audit.StandingPolicyRevoked && !audit.ExplicitDowngradeAllowed && audit.SourceChannel == audit.TargetChannel,
        _ => false,
    };
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
        snapshot.RequiredComponents.All(component => snapshot.ComponentPlatformDigests.TryGetValue(
            SignedUpdateManifestValidator.PlatformKey(component, snapshot.Platform),
            out string? digest) && IsDigest(digest));
    public static bool DictionaryEqual(IReadOnlyDictionary<string, string>? left, IReadOnlyDictionary<string, string>? right) =>
        left is not null && right is not null && left.Count == right.Count &&
        left.All(pair => right.TryGetValue(pair.Key, out string? value) && DigestsEqual(pair.Value, value));
    public static bool IsLifecycleTransition(HostUpdateJournalEntry? previous, HostUpdateJournalEntry? current) =>
        current is not null && (previous is null ? current.State == HostUpdateLifecycle.Planned :
        (previous.State, current.State) is (HostUpdateLifecycle.Planned, HostUpdateLifecycle.Approved or HostUpdateLifecycle.Failed or HostUpdateLifecycle.NeedsOperator) or
        (HostUpdateLifecycle.Approved, HostUpdateLifecycle.Staging or HostUpdateLifecycle.Failed or HostUpdateLifecycle.NeedsOperator) or
        (HostUpdateLifecycle.Staging, HostUpdateLifecycle.Staged or HostUpdateLifecycle.Failed or HostUpdateLifecycle.NeedsOperator));
}
