#pragma warning disable CA1849 // The replay store and policy fence deliberately force an OS-level disk flush after the async write completes.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Farm.Infrastructure.Settings;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Infrastructure.Services.HostUpdates;

public enum HostUpdateSchedulerReason
{
    Disabled,
    KillSwitch,
    NoCandidate,
    CandidateInvalid,
    ChannelMismatch,
    InsiderAcknowledgementRequired,
    MaintenanceWindowClosed,
    CompatibilityNotReady,
    InstallationUnavailable,
    SafetyCheckFailed,
    ReplayRejected,
    ReplaySuperseded,
    PolicyDrifted,
    PolicyFenceUnavailable,
    TooEarly,
    UpdateAlreadyRunning,
    Admitted,
    ExecutorRefused,
    ExecutorFailed,
    RecoveryRequired,
    HostShutdown,
    ReplayStoreUnavailable,
}

internal static class HostUpdateCanonical
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Hash(object value)
    {
        string json = JsonSerializer.Serialize(value, Options);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}

public sealed record HostUpdateSchedulerSettings(
    bool AutoEnabled = false,
    bool KillSwitch = false,
    string Channel = UpdateChannelSettings.StableChannel,
    bool InsiderAcknowledged = false,
    long PolicyRevision = 0,
    int PollIntervalSeconds = 3600,
    int? InsiderPollIntervalSeconds = null,
    int MaintenanceWindowStartHour = 0,
    int MaintenanceWindowEndHour = 24)
{
    public bool IsEffectivelyEnabled => AutoEnabled &&
        (Channel == UpdateChannelSettings.StableChannel ||
         (Channel == UpdateChannelSettings.InsiderChannel && InsiderAcknowledged));

    public TimeSpan EffectivePollInterval
    {
        get
        {
            int seconds = Channel == UpdateChannelSettings.InsiderChannel && InsiderAcknowledged && InsiderPollIntervalSeconds is int insider
                ? insider
                : PollIntervalSeconds;
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 60, 86400));
        }
    }

    public bool IsMaintenanceWindowOpen(DateTimeOffset now)
    {
        int start = Math.Clamp(MaintenanceWindowStartHour, 0, 23);
        int end = Math.Clamp(MaintenanceWindowEndHour, 1, 24);
        if (start == 0 && end == 24)
        {
            return true;
        }

        int hour = now.UtcDateTime.Hour;
        return start < end ? hour >= start && hour < end : hour >= start || hour < end;
    }

    public string Fingerprint => HostUpdateCanonical.Hash(new
    {
        AutoEnabled,
        KillSwitch,
        Channel,
        InsiderAcknowledged,
        PolicyRevision,
        PollIntervalSeconds,
        InsiderPollIntervalSeconds,
        MaintenanceWindowStartHour,
        MaintenanceWindowEndHour
    });
}

public sealed record HostUpdatePlatformDigests(
    string Api,
    string Frontend,
    string SlicerHost,
    string PrinterDiscovery,
    string OrcaslicerWorker,
    string Monolith)
{
    public IReadOnlyList<string> Values => [Api, Frontend, SlicerHost, PrinterDiscovery, OrcaslicerWorker, Monolith];

    public bool IsComplete => Values.Count == 6 && Values.Distinct(StringComparer.Ordinal).Count() == 6 && Values.All(IsSha256);

    private static bool IsSha256(string value) => value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && value.Length == 71 && value[7..].All(Uri.IsHexDigit);
}

public sealed record VerifiedHostUpdateCandidate(
    string ReleaseId,
    string SourceCommit,
    long Sequence,
    string ManifestDigest,
    string Channel,
    bool CryptographicallyVerified,
    bool CompatibilityReady,
    bool InstallationAvailable,
    bool SafetyPassed,
    bool MaintenanceWindowOpen,
    bool IsNewer,
    HostUpdatePlatformDigests PlatformDigests,
    bool EvidenceFresh = true,
    string TrustRoot = "default",
    DateTimeOffset? VerifiedAt = null,
    DateTimeOffset? ExpiresAt = null)
{
    public bool IsEligible => CryptographicallyVerified && CompatibilityReady && InstallationAvailable && MaintenanceWindowOpen && SafetyPassed && IsNewer &&
        !string.IsNullOrWhiteSpace(ReleaseId) && !string.IsNullOrWhiteSpace(SourceCommit) && Sequence >= 0 &&
        !string.IsNullOrWhiteSpace(ManifestDigest) && !string.IsNullOrWhiteSpace(Channel) && !string.IsNullOrWhiteSpace(TrustRoot) && PlatformDigests.IsComplete;

    public bool IsWithinFreshnessWindow(DateTimeOffset now) => EvidenceFresh && (ExpiresAt is null || now < ExpiresAt);

    public string Identity => HostUpdateCanonical.Hash(new { TrustRoot, Channel, ReleaseId, SourceCommit, Sequence, ManifestDigest });
}

public sealed record HostUpdateExecutorRequest(
    string RequestId,
    string ReleaseId,
    string SourceCommit,
    long Sequence,
    string ManifestDigest,
    string Channel,
    string TrustRoot,
    long PolicyRevision,
    string PolicyFingerprint,
    HostUpdatePlatformDigests PlatformDigests)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(RequestId) && !string.IsNullOrWhiteSpace(ReleaseId) &&
        !string.IsNullOrWhiteSpace(SourceCommit) && Sequence >= 0 && !string.IsNullOrWhiteSpace(ManifestDigest) &&
        (Channel == UpdateChannelSettings.StableChannel || Channel == UpdateChannelSettings.InsiderChannel) &&
        !string.IsNullOrWhiteSpace(TrustRoot) && PolicyRevision >= 0 && !string.IsNullOrWhiteSpace(PolicyFingerprint) && PlatformDigests.IsComplete;
}

[JsonConverter(typeof(JsonStringEnumConverter<HostUpdateExecutorResult>))]
public enum HostUpdateExecutorResult
{
    Accepted,
    Refused,
    Failed,
    RecoveryRequired
}

public sealed record HostUpdateExecutorResponse(HostUpdateExecutorResult Result, string? Reason = null);

public interface IHostUpdateSchedulerExecutor
{
    Task<HostUpdateExecutorResponse> ExecuteAsync(HostUpdateExecutorRequest request, CancellationToken ct);

    Task SignalSafeCheckpointCancellationAsync(string requestId, CancellationToken ct);
}

public interface IHostUpdateSchedulerSettings
{
    HostUpdateSchedulerSettings Current { get; }
}

public interface IHostUpdateSchedulerCandidateCache
{
    VerifiedHostUpdateCandidate? Current { get; }

    string? LastError { get; }
}

public interface IHostUpdateClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IHostUpdateJitter
{
    TimeSpan For(string identity, int attempt);
}

[JsonConverter(typeof(JsonStringEnumConverter<HostUpdateReplayDisposition>))]
public enum HostUpdateReplayDisposition
{
    Accepted,
    Rejected,
    Superseded
}

public enum HostUpdateReplayIntent
{
    Admit,
    Reject
}

public sealed record HostUpdateReplayDecision(HostUpdateReplayDisposition Disposition, string CorrelationId, bool Reused);

public interface IHostUpdateReplayStore
{
    Task<HostUpdateReplayDecision> DecideAsync(VerifiedHostUpdateCandidate candidate, HostUpdateReplayIntent intent, CancellationToken ct);
}

public interface IHostUpdateReplayAnchor
{
    Task<long> ReadEpochAsync(CancellationToken ct);

    Task AdvanceEpochAsync(long epoch, CancellationToken ct);
}

public sealed class UnavailableHostUpdateReplayAnchor : IHostUpdateReplayAnchor
{
    public Task<long> ReadEpochAsync(CancellationToken ct) => throw new NotSupportedException("host_update_replay_anchor_not_available");

    public Task AdvanceEpochAsync(long epoch, CancellationToken ct) => throw new NotSupportedException("host_update_replay_anchor_not_available");
}

public sealed class FileHostUpdateReplayStore(string rootPath, IHostUpdateReplayAnchor anchor) : IHostUpdateReplayStore, IDisposable
{
    private const int CurrentVersion = 1;

    private readonly string _path = Path.Combine(rootPath ?? throw new ArgumentNullException(nameof(rootPath)), "host-update-replay.json");
    private readonly IHostUpdateReplayAnchor _anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<HostUpdateReplayDecision> DecideAsync(VerifiedHostUpdateCandidate candidate, HostUpdateReplayIntent intent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (!candidate.CryptographicallyVerified || string.IsNullOrWhiteSpace(candidate.TrustRoot))
        {
            return new(HostUpdateReplayDisposition.Rejected, "unauthenticated:" + candidate.Identity, false);
        }

        await _gate.WaitAsync(ct);
        try
        {
            long anchorEpoch = await ReadAnchorEpochAsync(ct);
            HostUpdateReplayFileState state = await LoadFileAsync(anchorEpoch, ct);
            string ns = Namespace(candidate);

            if (state.Identities.TryGetValue(candidate.Identity, out HostUpdateReplayIdentityRecord? existing))
            {
                return new(existing.Disposition, existing.CorrelationId, true);
            }

            state.HighWaterByNamespace.TryGetValue(ns, out HostUpdateReplayHighWater? highWater);

            if (highWater is not null && candidate.Sequence < highWater.Sequence)
            {
                return await PersistRejectionAsync(state, candidate, anchorEpoch, ct);
            }

            if (highWater is not null && candidate.Sequence == highWater.Sequence && !string.Equals(highWater.Identity, candidate.Identity, StringComparison.Ordinal))
            {
                return await PersistRejectionAsync(state, candidate, anchorEpoch, ct);
            }

            HostUpdateReplayDisposition disposition = intent == HostUpdateReplayIntent.Admit ? HostUpdateReplayDisposition.Accepted : HostUpdateReplayDisposition.Rejected;
            string correlationId = "decision:" + candidate.Identity;

            if (highWater is not null && candidate.Sequence > highWater.Sequence &&
                state.Identities.TryGetValue(highWater.Identity, out HostUpdateReplayIdentityRecord? previous) &&
                previous.Disposition == HostUpdateReplayDisposition.Accepted)
            {
                state.Identities[highWater.Identity] = previous with { Disposition = HostUpdateReplayDisposition.Superseded };
            }

            state.HighWaterByNamespace[ns] = new HostUpdateReplayHighWater(candidate.Sequence, candidate.Identity);
            state.Identities[candidate.Identity] = new HostUpdateReplayIdentityRecord(candidate.Sequence, disposition, correlationId);
            await SaveAsync(state, anchorEpoch, ct);
            return new(disposition, correlationId, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HostUpdateReplayDecision> PersistRejectionAsync(HostUpdateReplayFileState state, VerifiedHostUpdateCandidate candidate, long anchorEpoch, CancellationToken ct)
    {
        string correlationId = "decision:" + candidate.Identity;
        state.Identities[candidate.Identity] = new HostUpdateReplayIdentityRecord(candidate.Sequence, HostUpdateReplayDisposition.Rejected, correlationId);
        await SaveAsync(state, anchorEpoch, ct);
        return new(HostUpdateReplayDisposition.Rejected, correlationId, false);
    }

    private static string Namespace(VerifiedHostUpdateCandidate candidate) => candidate.TrustRoot + "::" + candidate.Channel;

    private async Task<long> ReadAnchorEpochAsync(CancellationToken ct)
    {
        try
        {
            return await _anchor.ReadEpochAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException("host_update_replay_anchor_unavailable", ex);
        }
    }

    private async Task<HostUpdateReplayFileState> LoadFileAsync(long anchorEpoch, CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            throw new InvalidDataException("host_update_replay_state_missing");
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(_path, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("host_update_replay_state_io_error", ex);
        }

        HostUpdateReplayFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<HostUpdateReplayFileDto>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("host_update_replay_state_invalid", ex);
        }

        if (dto is null || dto.HighWaterByNamespace is null || dto.Identities is null || dto.Version != CurrentVersion)
        {
            throw new InvalidDataException("host_update_replay_state_invalid");
        }

        HostUpdateReplayFileState state = new()
        {
            Epoch = dto.Epoch,
            HighWaterByNamespace = dto.HighWaterByNamespace.ToDictionary(kv => kv.Key, kv => new HostUpdateReplayHighWater(kv.Value.Sequence, kv.Value.Identity), StringComparer.Ordinal),
            Identities = dto.Identities.ToDictionary(kv => kv.Key, kv => new HostUpdateReplayIdentityRecord(kv.Value.Sequence, Enum.Parse<HostUpdateReplayDisposition>(kv.Value.Disposition), kv.Value.CorrelationId), StringComparer.Ordinal)
        };

        if (!string.Equals(ComputeChecksum(state), dto.Checksum, StringComparison.Ordinal))
        {
            throw new InvalidDataException("host_update_replay_state_corrupt");
        }

        if (state.Epoch < anchorEpoch)
        {
            throw new InvalidDataException("host_update_replay_state_rollback");
        }

        return state;
    }

    private async Task SaveAsync(HostUpdateReplayFileState state, long anchorEpoch, CancellationToken ct)
    {
        long epoch = Math.Max(state.Epoch, anchorEpoch) + 1;
        try
        {
            await _anchor.AdvanceEpochAsync(epoch, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException("host_update_replay_anchor_unavailable", ex);
        }

        state.Epoch = epoch;
        string checksum = ComputeChecksum(state);
        HostUpdateReplayFileDto dto = new(
            CurrentVersion,
            epoch,
            checksum,
            state.HighWaterByNamespace.ToDictionary(kv => kv.Key, kv => new HostUpdateReplayHighWaterDto(kv.Value.Sequence, kv.Value.Identity), StringComparer.Ordinal),
            state.Identities.ToDictionary(kv => kv.Key, kv => new HostUpdateReplayIdentityDto(kv.Value.Sequence, kv.Value.Disposition.ToString(), kv.Value.CorrelationId), StringComparer.Ordinal));

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            string json = JsonSerializer.Serialize(dto);
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                stream.Flush(true);
            }

            File.Move(temporary, _path, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                }
            }

            throw new InvalidDataException("host_update_replay_state_io_error", ex);
        }
    }

    private static string ComputeChecksum(HostUpdateReplayFileState state) => HostUpdateCanonical.Hash(new
    {
        state.Version,
        state.Epoch,
        HighWater = state.HighWaterByNamespace.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new { Namespace = kv.Key, kv.Value.Sequence, kv.Value.Identity }),
        Identities = state.Identities.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new { Identity = kv.Key, kv.Value.Sequence, Disposition = kv.Value.Disposition.ToString(), kv.Value.CorrelationId })
    });

    private sealed record HostUpdateReplayHighWater(long Sequence, string Identity);

    private sealed record HostUpdateReplayIdentityRecord(long Sequence, HostUpdateReplayDisposition Disposition, string CorrelationId);

    private sealed class HostUpdateReplayFileState
    {
        public int Version { get; init; } = CurrentVersion;

        public long Epoch { get; set; }

        public Dictionary<string, HostUpdateReplayHighWater> HighWaterByNamespace { get; set; } = new(StringComparer.Ordinal);

        public Dictionary<string, HostUpdateReplayIdentityRecord> Identities { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed record HostUpdateReplayHighWaterDto(long Sequence, string Identity);

    private sealed record HostUpdateReplayIdentityDto(long Sequence, string Disposition, string CorrelationId);

    private sealed record HostUpdateReplayFileDto(
        int Version,
        long Epoch,
        string Checksum,
        Dictionary<string, HostUpdateReplayHighWaterDto> HighWaterByNamespace,
        Dictionary<string, HostUpdateReplayIdentityDto> Identities);

    public void Dispose() => _gate.Dispose();
}

public interface IHostUpdatePolicyFence
{
    Task<bool> TryAdvanceAsync(long revision, string fingerprint, CancellationToken ct);
}

public sealed class FileHostUpdatePolicyFence(string rootPath) : IHostUpdatePolicyFence, IDisposable
{
    private readonly string _path = Path.Combine(rootPath ?? throw new ArgumentNullException(nameof(rootPath)), "host-update-policy-fence.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<bool> TryAdvanceAsync(long revision, string fingerprint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        await _gate.WaitAsync(ct);
        try
        {
            HostUpdatePolicyFenceState? state = await LoadAsync(ct);
            if (state is not null)
            {
                if (revision < state.Revision)
                {
                    return false;
                }

                if (revision == state.Revision)
                {
                    return string.Equals(fingerprint, state.Fingerprint, StringComparison.Ordinal);
                }
            }

            await SaveAsync(new HostUpdatePolicyFenceState(revision, fingerprint), ct);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HostUpdatePolicyFenceState?> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(_path, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("host_update_policy_fence_io_error", ex);
        }

        HostUpdatePolicyFenceDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<HostUpdatePolicyFenceDto>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("host_update_policy_fence_invalid", ex);
        }

        if (dto is null || string.IsNullOrWhiteSpace(dto.Fingerprint))
        {
            throw new InvalidDataException("host_update_policy_fence_invalid");
        }

        string expected = HostUpdateCanonical.Hash(new { dto.Revision, dto.Fingerprint });
        if (!string.Equals(expected, dto.Checksum, StringComparison.Ordinal))
        {
            throw new InvalidDataException("host_update_policy_fence_corrupt");
        }

        return new HostUpdatePolicyFenceState(dto.Revision, dto.Fingerprint);
    }

    private async Task SaveAsync(HostUpdatePolicyFenceState state, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        string checksum = HostUpdateCanonical.Hash(new { state.Revision, state.Fingerprint });
        string json = JsonSerializer.Serialize(new HostUpdatePolicyFenceDto(state.Revision, state.Fingerprint, checksum));
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                stream.Flush(true);
            }

            File.Move(temporary, _path, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                }
            }

            throw new InvalidDataException("host_update_policy_fence_io_error", ex);
        }
    }

    private sealed record HostUpdatePolicyFenceState(long Revision, string Fingerprint);

    private sealed record HostUpdatePolicyFenceDto(long Revision, string Fingerprint, string Checksum);

    public void Dispose() => _gate.Dispose();
}

public sealed record HostUpdateSchedulerStatus(
    bool Enabled,
    bool EffectiveEnabled,
    bool KillSwitch,
    string Channel,
    long PolicyRevision,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? NextPollAt,
    int ConsecutiveFailures,
    HostUpdateSchedulerReason Reason);

public sealed class HostUpdateScheduler(
    IHostUpdateSchedulerSettings settings,
    IHostUpdateSchedulerCandidateCache cache,
    IHostUpdateReplayStore replayStore,
    IHostUpdatePolicyFence policyFence,
    IHostUpdateSchedulerExecutor executor,
    IHostUpdateClock clock,
    IHostUpdateJitter jitter,
    ILogger<HostUpdateScheduler>? logger = null) : IDisposable
{
    private static readonly TimeSpan MaxJitter = TimeSpan.FromMinutes(5);

    private readonly ILogger<HostUpdateScheduler> _logger = logger ?? NullLogger<HostUpdateScheduler>.Instance;
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private HostUpdateSchedulerStatus _status = new(false, false, false, UpdateChannelSettings.StableChannel, 0, null, null, 0, HostUpdateSchedulerReason.Disabled);
    private string? _activeRequestId;
    private string? _signaledRequestId;

    public HostUpdateSchedulerStatus Status => _status;

    public string? ActiveRequestId => _activeRequestId;

    public async Task<HostUpdateSchedulerStatus> TickAsync(CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return _status with { Reason = HostUpdateSchedulerReason.HostShutdown };
        }

        if (!await _tickGate.WaitAsync(0, ct))
        {
            return _status with { Reason = HostUpdateSchedulerReason.UpdateAlreadyRunning };
        }

        try
        {
            if (ct.IsCancellationRequested)
            {
                return _status with { Reason = HostUpdateSchedulerReason.HostShutdown };
            }

            DateTimeOffset now = clock.UtcNow;

            if (_status.NextPollAt is DateTimeOffset nextPollAt && now < nextPollAt)
            {
                return _status with { Reason = HostUpdateSchedulerReason.TooEarly };
            }

            HostUpdateSchedulerSettings current = settings.Current;
            _status = _status with
            {
                Enabled = current.AutoEnabled,
                EffectiveEnabled = current.IsEffectivelyEnabled,
                KillSwitch = current.KillSwitch,
                Channel = current.Channel,
                PolicyRevision = current.PolicyRevision,
                LastAttemptAt = now
            };

            if (!current.AutoEnabled || !current.IsEffectivelyEnabled)
            {
                return _status with { Reason = current.Channel == UpdateChannelSettings.InsiderChannel ? HostUpdateSchedulerReason.InsiderAcknowledgementRequired : HostUpdateSchedulerReason.Disabled };
            }

            if (current.KillSwitch)
            {
                return _status with { Reason = HostUpdateSchedulerReason.KillSwitch };
            }

            bool fenceAdvanced;
            try
            {
                fenceAdvanced = await policyFence.TryAdvanceAsync(current.PolicyRevision, current.Fingerprint, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "host_update_policy_fence_unavailable");
                return _status with { Reason = HostUpdateSchedulerReason.PolicyFenceUnavailable };
            }

            if (!fenceAdvanced)
            {
                return Backoff(HostUpdateSchedulerReason.PolicyDrifted, "policy:" + current.Fingerprint);
            }

            if (cache.LastError is not null)
            {
                return Backoff(HostUpdateSchedulerReason.CandidateInvalid, "cache_error");
            }

            VerifiedHostUpdateCandidate? candidate = cache.Current;
            if (candidate is null)
            {
                return _status with { Reason = HostUpdateSchedulerReason.NoCandidate };
            }

            HostUpdateSchedulerReason? gateFailure = Gate(candidate, current, now);
            if (gateFailure is not null)
            {
                try
                {
                    await replayStore.DecideAsync(candidate, HostUpdateReplayIntent.Reject, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "host_update_replay_store_unavailable");
                    return _status with { Reason = HostUpdateSchedulerReason.ReplayStoreUnavailable };
                }

                return Backoff(gateFailure.Value, candidate.Identity);
            }

            HostUpdateReplayDecision decision;
            try
            {
                decision = await replayStore.DecideAsync(candidate, HostUpdateReplayIntent.Admit, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "host_update_replay_store_unavailable");
                return _status with { Reason = HostUpdateSchedulerReason.ReplayStoreUnavailable };
            }

            if (decision.Disposition != HostUpdateReplayDisposition.Accepted)
            {
                HostUpdateSchedulerReason reason = decision.Disposition == HostUpdateReplayDisposition.Superseded
                    ? HostUpdateSchedulerReason.ReplaySuperseded
                    : HostUpdateSchedulerReason.ReplayRejected;
                return Backoff(reason, candidate.Identity);
            }

            HostUpdateSchedulerSettings fresh = settings.Current;
            VerifiedHostUpdateCandidate? freshCandidate = cache.Current;

            if (fresh.KillSwitch)
            {
                return Backoff(HostUpdateSchedulerReason.KillSwitch, candidate.Identity);
            }

            bool driftDetected = !fresh.AutoEnabled || !fresh.IsEffectivelyEnabled ||
                fresh.Channel != current.Channel || fresh.PolicyRevision != current.PolicyRevision ||
                !string.Equals(fresh.Fingerprint, current.Fingerprint, StringComparison.Ordinal) ||
                freshCandidate is null || !string.Equals(freshCandidate.Identity, candidate.Identity, StringComparison.Ordinal) ||
                !string.IsNullOrWhiteSpace(cache.LastError) ||
                Gate(freshCandidate, fresh, clock.UtcNow) is not null;

            if (driftDetected)
            {
                return Backoff(HostUpdateSchedulerReason.PolicyDrifted, candidate.Identity);
            }

            HostUpdateExecutorRequest request = new(
                decision.CorrelationId,
                candidate.ReleaseId,
                candidate.SourceCommit,
                candidate.Sequence,
                candidate.ManifestDigest,
                candidate.Channel,
                candidate.TrustRoot,
                fresh.PolicyRevision,
                fresh.Fingerprint,
                candidate.PlatformDigests);

            Interlocked.Exchange(ref _signaledRequestId, null);
            _activeRequestId = request.RequestId;
            try
            {
                HostUpdateExecutorResponse response = await executor.ExecuteAsync(request, ct);
                HostUpdateSchedulerReason reason = response.Result switch
                {
                    HostUpdateExecutorResult.Accepted => HostUpdateSchedulerReason.Admitted,
                    HostUpdateExecutorResult.Refused => HostUpdateSchedulerReason.ExecutorRefused,
                    HostUpdateExecutorResult.Failed => HostUpdateSchedulerReason.ExecutorFailed,
                    HostUpdateExecutorResult.RecoveryRequired => HostUpdateSchedulerReason.RecoveryRequired,
                    _ => HostUpdateSchedulerReason.ExecutorFailed
                };
                _status = response.Result == HostUpdateExecutorResult.Accepted
                    ? _status with { ConsecutiveFailures = 0, NextPollAt = SafeAdd(now, fresh.EffectivePollInterval), Reason = reason }
                    : Backoff(reason, candidate.Identity);
                return _status;
            }
            finally
            {
                _activeRequestId = null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return _status with { Reason = HostUpdateSchedulerReason.HostShutdown };
        }
        finally
        {
            _tickGate.Release();
        }
    }

    public Task SignalSafeCheckpointCancellationAsync(CancellationToken ct = default)
    {
        string? requestId = _activeRequestId;
        if (requestId is null)
        {
            return Task.CompletedTask;
        }

        if (Interlocked.CompareExchange(ref _signaledRequestId, requestId, null) is not null)
        {
            return Task.CompletedTask;
        }

        return executor.SignalSafeCheckpointCancellationAsync(requestId, ct);
    }

    public void Dispose() => _tickGate.Dispose();

    private HostUpdateSchedulerReason? Gate(VerifiedHostUpdateCandidate candidate, HostUpdateSchedulerSettings current, DateTimeOffset now)
    {
        if (!candidate.IsWithinFreshnessWindow(now) || !string.IsNullOrWhiteSpace(cache.LastError))
        {
            return HostUpdateSchedulerReason.CandidateInvalid;
        }

        if (candidate.Channel != current.Channel)
        {
            return HostUpdateSchedulerReason.ChannelMismatch;
        }

        if (candidate.Channel == UpdateChannelSettings.InsiderChannel && !current.InsiderAcknowledged)
        {
            return HostUpdateSchedulerReason.InsiderAcknowledgementRequired;
        }

        if (!candidate.CryptographicallyVerified)
        {
            return HostUpdateSchedulerReason.CandidateInvalid;
        }

        if (!candidate.CompatibilityReady)
        {
            return HostUpdateSchedulerReason.CompatibilityNotReady;
        }

        if (!candidate.InstallationAvailable)
        {
            return HostUpdateSchedulerReason.InstallationUnavailable;
        }

        if (!candidate.MaintenanceWindowOpen || !current.IsMaintenanceWindowOpen(now))
        {
            return HostUpdateSchedulerReason.MaintenanceWindowClosed;
        }

        if (!candidate.SafetyPassed)
        {
            return HostUpdateSchedulerReason.SafetyCheckFailed;
        }

        if (!candidate.IsNewer || !candidate.IsEligible)
        {
            return HostUpdateSchedulerReason.CandidateInvalid;
        }

        return null;
    }

    private HostUpdateSchedulerStatus Backoff(HostUpdateSchedulerReason reason, string identity)
    {
        int failures = _status.ConsecutiveFailures + 1;
        TimeSpan delay = PollInterval(failures) + BoundedJitter(identity, failures);
        _status = _status with { ConsecutiveFailures = failures, NextPollAt = SafeAdd(clock.UtcNow, delay), Reason = reason };
        return _status;
    }

    private TimeSpan BoundedJitter(string identity, int attempt)
    {
        TimeSpan raw = jitter.For(identity, attempt);
        if (raw < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return raw > MaxJitter ? MaxJitter : raw;
    }

    private static TimeSpan PollInterval(int failures) => TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Min(failures, 6))));

    private static DateTimeOffset SafeAdd(DateTimeOffset now, TimeSpan delay)
    {
        try
        {
            return now + delay;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MaxValue;
        }
    }
}

public sealed class SystemHostUpdateClock : IHostUpdateClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class ZeroHostUpdateJitter : IHostUpdateJitter
{
    public TimeSpan For(string identity, int attempt) => TimeSpan.Zero;
}
