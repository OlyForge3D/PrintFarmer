using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Farm.Infrastructure.Settings;

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
    UpdateAlreadyRunning,
    ExecutorRefused,
    ExecutorFailed,
    RecoveryRequired,
    HostShutdown,
    ReplayStoreUnavailable,
}

public sealed record HostUpdateSchedulerSettings(
    bool AutoEnabled = false,
    bool KillSwitch = false,
    string Channel = UpdateChannelSettings.StableChannel,
    bool InsiderAcknowledged = false,
    long PolicyRevision = 0)
{
    public bool IsEffectivelyEnabled => AutoEnabled &&
        (Channel == UpdateChannelSettings.StableChannel ||
         (Channel == UpdateChannelSettings.InsiderChannel && InsiderAcknowledged));
}

public sealed record HostUpdatePlatformDigests(
    string Api,
    string Frontend,
    string Worker,
    string Slicer,
    string Database,
    string Host)
{
    public IReadOnlyList<string> Values => [Api, Frontend, Worker, Slicer, Database, Host];

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
    string TrustRoot = "default")
{
    public bool IsEligible => CryptographicallyVerified && CompatibilityReady && InstallationAvailable && MaintenanceWindowOpen && SafetyPassed && IsNewer &&
        !string.IsNullOrWhiteSpace(ReleaseId) && !string.IsNullOrWhiteSpace(SourceCommit) && Sequence >= 0 &&
        !string.IsNullOrWhiteSpace(ManifestDigest) && !string.IsNullOrWhiteSpace(Channel) && !string.IsNullOrWhiteSpace(TrustRoot) && PlatformDigests.IsComplete;

    public string Identity => string.Join('|', TrustRoot, Channel, ReleaseId, SourceCommit, Sequence, ManifestDigest);
}

public sealed record HostUpdateExecutorRequest(
    string RequestId,
    string ReleaseId,
    string SourceCommit,
    long Sequence,
    string ManifestDigest,
    string Channel,
    long PolicyRevision,
    HostUpdatePlatformDigests PlatformDigests)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(RequestId) && !string.IsNullOrWhiteSpace(ReleaseId) &&
        !string.IsNullOrWhiteSpace(SourceCommit) && Sequence >= 0 && !string.IsNullOrWhiteSpace(ManifestDigest) &&
        (Channel == UpdateChannelSettings.StableChannel || Channel == UpdateChannelSettings.InsiderChannel) &&
        PolicyRevision >= 0 && PlatformDigests.IsComplete;
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

public sealed record HostUpdateReplayState(IReadOnlyDictionary<string, long> HighWaterByChannel, IReadOnlySet<string> RejectedIdentities)
{
    public static HostUpdateReplayState Empty => new(new Dictionary<string, long>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
}

public interface IHostUpdateReplayStore
{
    Task<HostUpdateReplayState> LoadAsync(CancellationToken ct);

    Task<bool> TryAcceptAsync(VerifiedHostUpdateCandidate candidate, CancellationToken ct);

    Task RecordRejectedAsync(VerifiedHostUpdateCandidate candidate, CancellationToken ct);

    Task RecordSupersededAsync(VerifiedHostUpdateCandidate candidate, CancellationToken ct);
}

/// <summary>Protected host-local replay continuity; missing or unreadable state is never bootstrapped.</summary>
public sealed class FileHostUpdateReplayStore(string rootPath) : IHostUpdateReplayStore, IDisposable
{
    private readonly string _path = Path.Combine(rootPath ?? throw new ArgumentNullException(nameof(rootPath)), "host-update-replay.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<HostUpdateReplayState> LoadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_path))
            {
                throw new InvalidDataException("host_update_replay_state_missing");
            }

            return Deserialize(await File.ReadAllTextAsync(_path, ct));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("host_update_replay_state_invalid", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task RecordRejectedAsync(VerifiedHostUpdateCandidate candidate, CancellationToken ct) => RecordIdentityAsync(candidate, ct);

    public Task RecordSupersededAsync(VerifiedHostUpdateCandidate candidate, CancellationToken ct) => RecordIdentityAsync(candidate, ct);

    private async Task RecordIdentityAsync(VerifiedHostUpdateCandidate candidate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await _gate.WaitAsync(ct);
        try
        {
            HostUpdateReplayState current = await LoadWithoutLockAsync(ct);
            if (current.RejectedIdentities.Contains(candidate.Identity))
            {
                return;
            }

            HashSet<string> rejected = new(current.RejectedIdentities, StringComparer.Ordinal) { candidate.Identity };
            await AtomicWriteAsync(new HostUpdateReplayState(current.HighWaterByChannel, rejected), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryAcceptAsync(VerifiedHostUpdateCandidate candidate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!candidate.IsEligible)
        {
            return false;
        }

        await _gate.WaitAsync(ct);
        try
        {
            HostUpdateReplayState current = await LoadWithoutLockAsync(ct);
            if (current.RejectedIdentities.Contains(candidate.Identity) ||
                (current.HighWaterByChannel.TryGetValue(candidate.Channel, out long highWater) && candidate.Sequence <= highWater))
            {
                return false;
            }

            Dictionary<string, long> highWaterByChannel = new(current.HighWaterByChannel, StringComparer.Ordinal) { [candidate.Channel] = candidate.Sequence };
            await AtomicWriteAsync(new HostUpdateReplayState(highWaterByChannel, current.RejectedIdentities), ct);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HostUpdateReplayState> LoadWithoutLockAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            throw new InvalidDataException("host_update_replay_state_missing");
        }

        try
        {
            return Deserialize(await File.ReadAllTextAsync(_path, ct));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("host_update_replay_state_invalid", exception);
        }
    }

    private static HostUpdateReplayState Deserialize(string json)
    {
        HostUpdateReplayFile? file = JsonSerializer.Deserialize<HostUpdateReplayFile>(json);
        if (file is null || file.HighWaterByChannel is null || file.RejectedIdentities is null)
        {
            throw new JsonException();
        }

        return new(file.HighWaterByChannel, new HashSet<string>(file.RejectedIdentities, StringComparer.Ordinal));
    }

    private async Task AtomicWriteAsync(HostUpdateReplayState state, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        string json = JsonSerializer.Serialize(new HostUpdateReplayFile(new Dictionary<string, long>(state.HighWaterByChannel, StringComparer.Ordinal), [.. state.RejectedIdentities]));
        await File.WriteAllTextAsync(temporary, json, Encoding.UTF8, ct);
        File.Move(temporary, _path, true);
    }

    private sealed record HostUpdateReplayFile(Dictionary<string, long>? HighWaterByChannel, List<string>? RejectedIdentities);

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
    IHostUpdateSchedulerExecutor executor,
    IHostUpdateClock clock,
    IHostUpdateJitter jitter) : IDisposable
{
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private HostUpdateSchedulerStatus _status = new(false, false, false, UpdateChannelSettings.StableChannel, 0, null, null, 0, HostUpdateSchedulerReason.Disabled);

    public HostUpdateSchedulerStatus Status => _status;

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

            HostUpdateSchedulerSettings current = settings.Current;
            DateTimeOffset now = clock.UtcNow;
            _status = _status with { Enabled = current.AutoEnabled, EffectiveEnabled = current.IsEffectivelyEnabled, KillSwitch = current.KillSwitch, Channel = current.Channel, PolicyRevision = current.PolicyRevision, LastAttemptAt = now };
            if (!current.AutoEnabled || !current.IsEffectivelyEnabled)
            {
                return _status with { Reason = current.Channel == UpdateChannelSettings.InsiderChannel ? HostUpdateSchedulerReason.InsiderAcknowledgementRequired : HostUpdateSchedulerReason.Disabled };
            }

            if (current.KillSwitch)
            {
                return _status with { Reason = HostUpdateSchedulerReason.KillSwitch };
            }

            VerifiedHostUpdateCandidate? candidate = cache.Current;
            if (candidate is null)
            {
                return _status with { Reason = HostUpdateSchedulerReason.NoCandidate };
            }

            HostUpdateSchedulerReason? gateFailure = Gate(candidate, current);
            if (gateFailure is not null)
            {
                if (candidate.CryptographicallyVerified)
                {
                    try
                    {
                        await replayStore.RecordRejectedAsync(candidate, ct);
                    }
                    catch (InvalidDataException)
                    {
                        return _status with { Reason = HostUpdateSchedulerReason.ReplayStoreUnavailable };
                    }
                }

                return Backoff(gateFailure.Value, candidate.Identity);
            }

            bool accepted;
            try
            {
                accepted = await replayStore.TryAcceptAsync(candidate, ct);
            }
            catch (InvalidDataException)
            {
                return _status with { Reason = HostUpdateSchedulerReason.ReplayStoreUnavailable };
            }

            if (!accepted)
            {
                return Backoff(HostUpdateSchedulerReason.ReplayRejected, candidate.Identity);
            }

            HostUpdateExecutorRequest request = new($"auto:{candidate.Identity}", candidate.ReleaseId, candidate.SourceCommit, candidate.Sequence, candidate.ManifestDigest, candidate.Channel, current.PolicyRevision, candidate.PlatformDigests);
            HostUpdateExecutorResponse response = await executor.ExecuteAsync(request, ct);
            HostUpdateSchedulerReason reason = response.Result switch { HostUpdateExecutorResult.Accepted => HostUpdateSchedulerReason.NoCandidate, HostUpdateExecutorResult.Refused => HostUpdateSchedulerReason.ExecutorRefused, HostUpdateExecutorResult.Failed => HostUpdateSchedulerReason.ExecutorFailed, HostUpdateExecutorResult.RecoveryRequired => HostUpdateSchedulerReason.RecoveryRequired, _ => HostUpdateSchedulerReason.ExecutorFailed };
            _status = response.Result == HostUpdateExecutorResult.Accepted ? _status with { ConsecutiveFailures = 0, NextPollAt = now + PollInterval(0), Reason = reason } : Backoff(reason, candidate.Identity);
            return _status;
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

    public Task SignalSafeCheckpointCancellationAsync(CancellationToken ct = default) => executor.SignalSafeCheckpointCancellationAsync("automatic", ct);

    public void Dispose() => _tickGate.Dispose();

    private HostUpdateSchedulerReason? Gate(VerifiedHostUpdateCandidate candidate, HostUpdateSchedulerSettings current)
    {
        if (!candidate.EvidenceFresh || !string.IsNullOrWhiteSpace(cache.LastError))
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

        if (!candidate.MaintenanceWindowOpen)
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
        TimeSpan delay = PollInterval(failures) + jitter.For(identity, failures);
        return _status with { ConsecutiveFailures = failures, NextPollAt = clock.UtcNow + delay, Reason = reason };
    }

    private static TimeSpan PollInterval(int failures) => TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Min(failures, 6))));
}

public sealed class SystemHostUpdateClock : IHostUpdateClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class ZeroHostUpdateJitter : IHostUpdateJitter
{
    public TimeSpan For(string identity, int attempt) => TimeSpan.Zero;
}
