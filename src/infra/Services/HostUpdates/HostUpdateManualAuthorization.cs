using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Settings;

namespace Farm.Infrastructure.Services.HostUpdates;

public sealed record HostUpdateAdmissionFenceStatus(bool BlocksAdmission, string Reason, string? OperationId = null);

public interface IHostUpdateAdmissionFence
{
    HostUpdateAdmissionFenceStatus GetStatus();
}

public sealed class InactiveHostUpdateAdmissionFence : IHostUpdateAdmissionFence
{
    public HostUpdateAdmissionFenceStatus GetStatus() => new(false, string.Empty);
}

public sealed class UnavailableHostUpdateAdmissionFence : IHostUpdateAdmissionFence
{
    public HostUpdateAdmissionFenceStatus GetStatus() => new(true, "host_update_recovery_unavailable");
}

public sealed record HostUpdateManualAuthorizationResponse(
    string AuthorizationId,
    string ReleaseId,
    long Sequence,
    string Channel,
    string CandidateFingerprint,
    long PolicyRevision,
    string PolicyFingerprint,
    DateTimeOffset ExpiresAt);

public sealed record HostUpdateManualAuthorizationIntent(
    string? AuthorizationId = null,
    long? ExpectedPolicyRevision = null,
    string? ExpectedPolicyFingerprint = null);

public sealed record HostUpdateManualAuthorizationRecord(
    string AuthorizationId,
    string CandidateFingerprint,
    string ReleaseId,
    long Sequence,
    string Channel,
    string ManifestDigest,
    string SourceCommit,
    string TrustRoot,
    string HostPlatform,
    HostUpdatePlatformDigests PlatformDigests,
    string ReplayCorrelationId,
    long PolicyRevision,
    string PolicyFingerprint,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ExecutionAdmissionStartedAt,
    DateTimeOffset? ConsumedAt,
    string BindingHash);

public sealed record HostUpdateAuthorizationConsumeResult(
    bool Succeeded,
    HostUpdateManualAuthorizationRecord? Authorization,
    string? Error)
{
    public static HostUpdateAuthorizationConsumeResult Ok(HostUpdateManualAuthorizationRecord authorization) => new(true, authorization, null);

    public static HostUpdateAuthorizationConsumeResult Fail(string error) => new(false, null, error);
}

public interface IHostUpdateManualAuthorizationStore
{
    Task<HostUpdateManualAuthorizationRecord> CreateAsync(
        VerifiedHostUpdateCandidate candidate,
        HostUpdateSchedulerSettings policy,
        HostUpdateReplayDecision replayDecision,
        DateTimeOffset now,
        CancellationToken ct);

    Task<HostUpdateAuthorizationConsumeResult> CreateConsumedAsync(
        VerifiedHostUpdateCandidate candidate,
        HostUpdateSchedulerSettings policy,
        HostUpdateReplayDecision replayDecision,
        DateTimeOffset now,
        CancellationToken ct);

    Task<HostUpdateAuthorizationConsumeResult> PrepareConsumeAsync(
        string authorizationId,
        VerifiedHostUpdateCandidate candidate,
        HostUpdateSchedulerSettings policy,
        HostUpdateReplayDecision replayDecision,
        DateTimeOffset now,
        CancellationToken ct);

    Task<HostUpdateAuthorizationConsumeResult> CompleteConsumeAsync(
        string authorizationId,
        VerifiedHostUpdateCandidate candidate,
        HostUpdateSchedulerSettings policy,
        HostUpdateReplayDecision replayDecision,
        DateTimeOffset now,
        CancellationToken ct);
}

public sealed class FileHostUpdateManualAuthorizationStore(string rootPath, TimeSpan? ttl = null)
    : IHostUpdateManualAuthorizationStore, IDisposable
{
    private readonly string _path = Path.Combine(rootPath ?? throw new ArgumentNullException(nameof(rootPath)), "host-update-manual-authorizations.json");
    private readonly string _stagedPath = Path.Combine(rootPath, "host-update-manual-authorizations.json.staged");
    private readonly TimeSpan _ttl = ttl ?? TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<HostUpdateManualAuthorizationRecord> CreateAsync(
        VerifiedHostUpdateCandidate candidate,
        HostUpdateSchedulerSettings policy,
        HostUpdateReplayDecision replayDecision,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(policy);
        await _gate.WaitAsync(ct);
        try
        {
            HostUpdateManualAuthorizationState state = await LoadAsync(ct);
            HostUpdateManualAuthorizationRecord record = NewRecord(candidate, policy, replayDecision, now, consumedAt: null);
            state.Authorizations[record.AuthorizationId] = record;
            await SaveAsync(state, ct);
            return record;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HostUpdateAuthorizationConsumeResult> CreateConsumedAsync(
        VerifiedHostUpdateCandidate candidate,
        HostUpdateSchedulerSettings policy,
        HostUpdateReplayDecision replayDecision,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(policy);
        await _gate.WaitAsync(ct);
        try
        {
            HostUpdateManualAuthorizationState state = await LoadAsync(ct);
            HostUpdateManualAuthorizationRecord record = NewRecord(candidate, policy, replayDecision, now, consumedAt: now);
            state.Authorizations[record.AuthorizationId] = record;
            await SaveAsync(state, ct);
            return HostUpdateAuthorizationConsumeResult.Ok(record);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<HostUpdateAuthorizationConsumeResult> PrepareConsumeAsync(
        string authorizationId,
        VerifiedHostUpdateCandidate candidate,
        HostUpdateSchedulerSettings policy,
        HostUpdateReplayDecision replayDecision,
        DateTimeOffset now,
        CancellationToken ct) => UpdateConsumptionAsync(
            authorizationId,
            candidate,
            policy,
            replayDecision,
            now,
            record => record.ExecutionAdmissionStartedAt is null ? record with { ExecutionAdmissionStartedAt = now } : record,
            ct);

    public Task<HostUpdateAuthorizationConsumeResult> CompleteConsumeAsync(
        string authorizationId,
        VerifiedHostUpdateCandidate candidate,
        HostUpdateSchedulerSettings policy,
        HostUpdateReplayDecision replayDecision,
        DateTimeOffset now,
        CancellationToken ct) => UpdateConsumptionAsync(
            authorizationId,
            candidate,
            policy,
            replayDecision,
            now,
            record => record with { ExecutionAdmissionStartedAt = record.ExecutionAdmissionStartedAt ?? now, ConsumedAt = now },
            ct);

    private async Task<HostUpdateAuthorizationConsumeResult> UpdateConsumptionAsync(
        string authorizationId,
        VerifiedHostUpdateCandidate candidate,
        HostUpdateSchedulerSettings policy,
        HostUpdateReplayDecision replayDecision,
        DateTimeOffset now,
        Func<HostUpdateManualAuthorizationRecord, HostUpdateManualAuthorizationRecord> update,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationId);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(policy);
        await _gate.WaitAsync(ct);
        try
        {
            HostUpdateManualAuthorizationState state = await LoadAsync(ct);
            if (!state.Authorizations.TryGetValue(authorizationId, out HostUpdateManualAuthorizationRecord? record))
            {
                return HostUpdateAuthorizationConsumeResult.Fail("authorization_not_found");
            }

            string? rejection = Validate(record, candidate, policy, replayDecision, now);
            if (rejection is not null)
            {
                return HostUpdateAuthorizationConsumeResult.Fail(rejection);
            }

            HostUpdateManualAuthorizationRecord updated = update(record);
            state.Authorizations[authorizationId] = updated;
            await SaveAsync(state, ct);
            return HostUpdateAuthorizationConsumeResult.Ok(updated);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string ComputeBindingHash(HostUpdateManualAuthorizationRecord record) => HostUpdateCanonical.Hash(new
    {
        record.AuthorizationId,
        record.CandidateFingerprint,
        record.ReleaseId,
        record.Sequence,
        record.Channel,
        record.ManifestDigest,
        record.SourceCommit,
        record.TrustRoot,
        record.HostPlatform,
        record.PlatformDigests,
        record.ReplayCorrelationId,
        record.PolicyRevision,
        record.PolicyFingerprint,
        record.CreatedAt,
        record.ExpiresAt,
    });

    private HostUpdateManualAuthorizationRecord NewRecord(
        VerifiedHostUpdateCandidate candidate,
        HostUpdateSchedulerSettings policy,
        HostUpdateReplayDecision replayDecision,
        DateTimeOffset now,
        DateTimeOffset? consumedAt)
    {
        string id = "manual_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        HostUpdateManualAuthorizationRecord unsigned = new(
            id,
            candidate.Identity,
            candidate.ReleaseId,
            candidate.Sequence,
            candidate.Channel,
            candidate.ManifestDigest,
            candidate.SourceCommit,
            candidate.TrustRoot,
            candidate.HostPlatform,
            candidate.PlatformDigests,
            replayDecision.CorrelationId,
            policy.PolicyRevision,
            policy.Fingerprint,
            now,
            now + _ttl,
            consumedAt,
            consumedAt,
            string.Empty);
        return unsigned with { BindingHash = ComputeBindingHash(unsigned) };
    }

    private static string? Validate(
        HostUpdateManualAuthorizationRecord record,
        VerifiedHostUpdateCandidate candidate,
        HostUpdateSchedulerSettings policy,
        HostUpdateReplayDecision replayDecision,
        DateTimeOffset now)
    {
        if (!string.Equals(record.BindingHash, ComputeBindingHash(record), StringComparison.Ordinal))
        {
            return "authorization_binding_invalid";
        }

        if (record.ConsumedAt is not null)
        {
            return "authorization_consumed";
        }

        if (now >= record.ExpiresAt)
        {
            return "authorization_expired";
        }

        if (!string.Equals(record.CandidateFingerprint, candidate.Identity, StringComparison.Ordinal) ||
            !string.Equals(record.ReleaseId, candidate.ReleaseId, StringComparison.Ordinal) ||
            record.Sequence != candidate.Sequence ||
            !string.Equals(record.Channel, candidate.Channel, StringComparison.Ordinal) ||
            !string.Equals(record.ManifestDigest, candidate.ManifestDigest, StringComparison.Ordinal) ||
            !string.Equals(record.SourceCommit, candidate.SourceCommit, StringComparison.Ordinal) ||
            !string.Equals(record.TrustRoot, candidate.TrustRoot, StringComparison.Ordinal) ||
            !string.Equals(record.HostPlatform, candidate.HostPlatform, StringComparison.Ordinal) ||
            !Equals(record.PlatformDigests, candidate.PlatformDigests))
        {
            return "authorization_candidate_drift";
        }

        if (record.PolicyRevision != policy.PolicyRevision || !string.Equals(record.PolicyFingerprint, policy.Fingerprint, StringComparison.Ordinal))
        {
            return "authorization_policy_drift";
        }

        if (replayDecision.Disposition != HostUpdateReplayDisposition.Accepted || !string.Equals(record.ReplayCorrelationId, replayDecision.CorrelationId, StringComparison.Ordinal))
        {
            return "authorization_replay_drift";
        }

        return null;
    }

    private async Task<HostUpdateManualAuthorizationState> LoadAsync(CancellationToken ct)
    {
        DeleteUncommittedStage();
        if (!File.Exists(_path))
        {
            return new();
        }

        HostStateFileSecurity.RejectReparseTarget(_path);
        try
        {
            string json = await File.ReadAllTextAsync(_path, ct);
            HostUpdateManualAuthorizationEnvelope? envelope = JsonSerializer.Deserialize<HostUpdateManualAuthorizationEnvelope>(json);
            if (envelope is null)
            {
                throw new InvalidDataException("manual_authorization_state_invalid");
            }

            string expected = StateChecksum(envelope.Authorizations);
            if (!string.Equals(expected, envelope.Checksum, StringComparison.Ordinal))
            {
                throw new InvalidDataException("manual_authorization_state_corrupt");
            }

            return new HostUpdateManualAuthorizationState
            {
                Authorizations = envelope.Authorizations.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            throw new InvalidDataException("manual_authorization_state_invalid", ex);
        }
    }

    private async Task SaveAsync(HostUpdateManualAuthorizationState state, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        HostStateFileSecurity.RejectReparseTarget(_stagedPath);
        HostUpdateManualAuthorizationEnvelope envelope = new(state.Authorizations, StateChecksum(state.Authorizations));
        string json = JsonSerializer.Serialize(envelope);
        try
        {
            await FileHostUpdateReplayStore.WriteDurablyAsync(_stagedPath, json, ct);
            _ = JsonSerializer.Deserialize<HostUpdateManualAuthorizationEnvelope>(await File.ReadAllTextAsync(_stagedPath, ct))
                ?? throw new InvalidDataException("manual_authorization_state_invalid");
            HostStateFileSecurity.RejectReparseTarget(_path);
            File.Move(_stagedPath, _path, true);
        }
        finally
        {
            DeleteUncommittedStage();
        }
    }

    private static string StateChecksum(IReadOnlyDictionary<string, HostUpdateManualAuthorizationRecord> authorizations) => HostUpdateCanonical.Hash(new
    {
        Version = 1,
        Authorizations = authorizations.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value),
    });

    private void DeleteUncommittedStage()
    {
        if (File.Exists(_stagedPath))
        {
            HostStateFileSecurity.RejectReparseTarget(_stagedPath);
            File.Delete(_stagedPath);
        }
    }

    public void Dispose() => _gate.Dispose();

    private sealed class HostUpdateManualAuthorizationState
    {
        public Dictionary<string, HostUpdateManualAuthorizationRecord> Authorizations { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed record HostUpdateManualAuthorizationEnvelope(
        Dictionary<string, HostUpdateManualAuthorizationRecord> Authorizations,
        string Checksum);
}

public sealed record HostUpdateExecutionResolutionResult(
    bool Succeeded,
    HostUpdateExecutionRequest? Request,
    HostUpdateManualAuthorizationRecord? Authorization,
    string? Error)
{
    public static HostUpdateExecutionResolutionResult Ok(HostUpdateExecutionRequest request, HostUpdateManualAuthorizationRecord authorization) => new(true, request, authorization, null);

    public static HostUpdateExecutionResolutionResult Fail(string error) => new(false, null, null, error);
}

public interface IHostUpdateExecutionRequestResolver
{
    Task<HostUpdateManualAuthorizationResponse> AuthorizeCurrentAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct);

    Task<HostUpdateExecutionResolutionResult> ResolveManualAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct);
}

public sealed class UnavailableHostUpdateExecutor : IHostUpdateExecutor, IHostUpdateAvailability
{
    public bool IsAvailable => false;

    public string UnavailableReason => HostUpdateSchedulingAvailability.ExecutorNotProvisionedReason;

    public Task<HostUpdateExecutionResult> ExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(new HostUpdateExecutionResult(
            request.ReleaseId,
            HostUpdateExecutionState.RecoveryRequired,
            HostUpdateSchedulingAvailability.ExecutorNotProvisionedReason,
            []));
    }
}

public sealed class HostUpdateExecutionRequestResolver(
    IHostUpdateSchedulerSettings settings,
    IHostUpdateSchedulerCandidateCache cache,
    IHostUpdateReplayStore replayStore,
    IVerifiedReleaseManifestBindingStore manifestBindingStore,
    IHostUpdateManualAuthorizationStore authorizationStore,
    IHostUpdateAdmissionFence admissionFence,
    IHostUpdateClock clock) : IHostUpdateExecutionRequestResolver
{
    public async Task<HostUpdateManualAuthorizationResponse> AuthorizeCurrentAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct)
    {
        (HostUpdateSchedulerSettings policy, VerifiedHostUpdateCandidate candidate) = ResolveCurrentCandidate(intent);
        try
        {
            await manifestBindingStore.EnsureBoundAsync(candidate.ReleaseId, candidate.ManifestDigest, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException("manifest_binding_conflict", ex);
        }

        HostUpdateReplayDecision replay = await DecideReplayAsync(candidate, HostUpdateReplayIntent.Reserve, ct).ConfigureAwait(false);

        if (replay.Disposition != HostUpdateReplayDisposition.Accepted || replay.Reused)
        {
            throw new InvalidOperationException("candidate_replay_rejected");
        }

        HostUpdateManualAuthorizationRecord record = await authorizationStore.CreateAsync(candidate, policy, replay, clock.UtcNow, ct).ConfigureAwait(false);
        return ToResponse(record);
    }

    public async Task<HostUpdateExecutionResolutionResult> ResolveManualAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct)
    {
        HostUpdateAdmissionFenceStatus fence = admissionFence.GetStatus();
        if (fence.BlocksAdmission)
        {
            return HostUpdateExecutionResolutionResult.Fail(fence.Reason);
        }

        HostUpdateSchedulerSettings policy;
        VerifiedHostUpdateCandidate candidate;
        try
        {
            (policy, candidate) = ResolveCurrentCandidate(intent);
        }
        catch (InvalidOperationException ex)
        {
            return HostUpdateExecutionResolutionResult.Fail(ex.Message);
        }

        try
        {
            await manifestBindingStore.EnsureBoundAsync(candidate.ReleaseId, candidate.ManifestDigest, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return HostUpdateExecutionResolutionResult.Fail("manifest_binding_conflict");
        }

        bool hasAuthorizationId = !string.IsNullOrWhiteSpace(intent.AuthorizationId);
        HostUpdateReplayDecision replay = await DecideReplayAsync(
            candidate,
            hasAuthorizationId ? HostUpdateReplayIntent.Reserve : HostUpdateReplayIntent.Admit,
            ct).ConfigureAwait(false);

        if (replay.Disposition != HostUpdateReplayDisposition.Accepted || replay.Reused)
        {
            return HostUpdateExecutionResolutionResult.Fail("candidate_replay_rejected");
        }

        HostUpdateAuthorizationConsumeResult consumed;
        if (!hasAuthorizationId)
        {
            try
            {
                consumed = await authorizationStore.CreateConsumedAsync(candidate, policy, replay, clock.UtcNow, ct).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return HostUpdateExecutionResolutionResult.Fail("authorization_state_unavailable");
            }
        }
        else
        {
            try
            {
                consumed = await authorizationStore.PrepareConsumeAsync(intent.AuthorizationId!, candidate, policy, replay, clock.UtcNow, ct).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return HostUpdateExecutionResolutionResult.Fail("authorization_state_unavailable");
            }

            if (!consumed.Succeeded || consumed.Authorization is null)
            {
                return HostUpdateExecutionResolutionResult.Fail(consumed.Error ?? "authorization_invalid");
            }

            replay = await DecideReplayAsync(candidate, HostUpdateReplayIntent.Admit, ct).ConfigureAwait(false);

            if (replay.Disposition != HostUpdateReplayDisposition.Accepted || replay.Reused)
            {
                return HostUpdateExecutionResolutionResult.Fail("candidate_replay_rejected");
            }

            try
            {
                consumed = await authorizationStore.CompleteConsumeAsync(intent.AuthorizationId!, candidate, policy, replay, clock.UtcNow, ct).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return HostUpdateExecutionResolutionResult.Fail("authorization_state_unavailable");
            }
        }

        if (!consumed.Succeeded || consumed.Authorization is null)
        {
            return HostUpdateExecutionResolutionResult.Fail(consumed.Error ?? "authorization_invalid");
        }

        HostUpdateManualAuthorizationRecord authorization = consumed.Authorization;
        HostUpdateExecutorRequest executorRequest = new(
            authorization.AuthorizationId,
            authorization.ReleaseId,
            authorization.SourceCommit,
            authorization.Sequence,
            authorization.ManifestDigest,
            authorization.Channel,
            authorization.TrustRoot,
            authorization.PolicyRevision,
            authorization.PolicyFingerprint,
            authorization.PlatformDigests);
        HostUpdateExecutionRequest request;
        try
        {
            request = HostUpdateExecutionRequestBuilder.FromExecutorRequest(
                executorRequest,
                authorization.HostPlatform,
                HostUpdateAuthorizationKind.Manual);
        }
        catch (ArgumentException)
        {
            return HostUpdateExecutionResolutionResult.Fail("resolved_request_invalid");
        }

        return request.IsValid(out _)
            ? HostUpdateExecutionResolutionResult.Ok(request, authorization)
            : HostUpdateExecutionResolutionResult.Fail("resolved_request_invalid");
    }

    /// <summary>
    /// Single durable-availability boundary for protected anti-replay state. A store that is
    /// registered but not provisioned, or whose state cannot be read or committed, is an
    /// availability failure (503), never a replay conflict (409).
    /// </summary>
    private async Task<HostUpdateReplayDecision> DecideReplayAsync(
        VerifiedHostUpdateCandidate candidate,
        HostUpdateReplayIntent intent,
        CancellationToken ct)
    {
        if (replayStore is IHostUpdateAvailability { IsAvailable: false } unavailable)
        {
            throw new HostUpdateSubsystemUnavailableException(unavailable.UnavailableReason, null);
        }

        try
        {
            return await replayStore.DecideAsync(candidate, intent, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            throw new HostUpdateSubsystemUnavailableException(HostUpdateAvailabilityCodes.ReplayUnavailable, ex);
        }
    }

    private (HostUpdateSchedulerSettings Policy, VerifiedHostUpdateCandidate Candidate) ResolveCurrentCandidate(HostUpdateManualAuthorizationIntent intent)
    {
        HostUpdateSchedulerSettings policy;
        if (settings is IHostUpdatePolicyBackedSchedulerSettings policyBackedSettings)
        {
            HostUpdatePolicyReadResult result = policyBackedSettings.ReadPolicy();
            if (!result.Available)
            {
                throw new HostUpdateSubsystemUnavailableException(HostUpdateAvailabilityCodes.PolicyUnavailable, null);
            }

            policy = HostStateHostUpdateSchedulerSettings.ToSchedulerSettings(result.Policy);
        }
        else
        {
            policy = settings.Current;
        }

        if (intent.ExpectedPolicyRevision is long expectedRevision && expectedRevision != policy.PolicyRevision)
        {
            throw new InvalidOperationException("policy_revision_drift");
        }

        if (!string.IsNullOrWhiteSpace(intent.ExpectedPolicyFingerprint) && !string.Equals(intent.ExpectedPolicyFingerprint, policy.Fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("policy_fingerprint_drift");
        }

        if (!string.IsNullOrWhiteSpace(cache.LastError))
        {
            throw new InvalidOperationException("candidate_cache_error");
        }

        VerifiedHostUpdateCandidate candidate = cache.Current ?? throw new InvalidOperationException("candidate_missing");
        if (candidate.Channel != policy.Channel)
        {
            throw new InvalidOperationException("candidate_channel_mismatch");
        }

        if (!candidate.IsWithinFreshnessWindow(clock.UtcNow) || !candidate.IsEligible)
        {
            throw new InvalidOperationException("candidate_not_eligible");
        }

        if (!SignedUpdateManifestValidator.IsPlatform(candidate.HostPlatform))
        {
            throw new InvalidOperationException("host_platform_invalid");
        }

        return (policy, candidate);
    }

    private static HostUpdateManualAuthorizationResponse ToResponse(HostUpdateManualAuthorizationRecord record) => new(
        record.AuthorizationId,
        record.ReleaseId,
        record.Sequence,
        record.Channel,
        record.CandidateFingerprint,
        record.PolicyRevision,
        record.PolicyFingerprint,
        record.ExpiresAt);
}
