#pragma warning disable SA1516, SA1513, SA1408, SA1501, SA1515
#pragma warning disable CA1849 // The replay store and policy fence deliberately force an OS-level disk flush after the async write completes.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Farm.Infrastructure.Settings;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Infrastructure.Services.HostUpdates;

public enum HostUpdateSchedulerReason
{
    Disabled,
    KillSwitch,
    NoCandidate,
    CandidateInvalid,
    CandidateEvidenceStale,
    CandidateNotEligible,
    PolicyUnavailable,
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
    AdmissionFenceActive,
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
    DateTimeOffset? ExpiresAt = null,
    string HostPlatform = "")
{
    public bool IsEligible => CryptographicallyVerified && CompatibilityReady && InstallationAvailable && MaintenanceWindowOpen && SafetyPassed && IsNewer &&
        !string.IsNullOrWhiteSpace(ReleaseId) && !string.IsNullOrWhiteSpace(SourceCommit) && Sequence >= 1 &&
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
    HostUpdatePlatformDigests PlatformDigests,
    string OperationToken = "")
{
    public bool IsValid => !string.IsNullOrWhiteSpace(RequestId) && !string.IsNullOrWhiteSpace(ReleaseId) &&
        !string.IsNullOrWhiteSpace(SourceCommit) && Sequence >= 1 && !string.IsNullOrWhiteSpace(ManifestDigest) &&
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

/// <summary>
/// Identifies exactly one <see cref="IHostUpdateSchedulerExecutor.ExecuteAsync"/> invocation.
/// A request ID alone is not sufficient: the same ID can legitimately run again after the first
/// run finished, and a delayed cancellation signal captured for the earlier run must never cancel
/// the later one. The operation token is the immutable per-invocation generation that makes a
/// stale signal detectable.
/// </summary>
public sealed record HostUpdateCancellationSignal(string RequestId, string OperationToken);

public enum HostUpdateCancellationResult
{
    Signaled,
    AlreadySignaled,
    NoActiveExecution,
}

public interface IHostUpdateSchedulerExecutor
{
    Task<HostUpdateExecutorResponse> ExecuteAsync(HostUpdateExecutorRequest request, CancellationToken ct);
    void PreArmCancellation(HostUpdateCancellationSignal signal);

    /// <summary>
    /// Requests cancellation at the next safe checkpoint. Implementations must deliver the
    /// cancellation only when both the request ID and the operation token match the currently
    /// running generation.
    /// </summary>
    Task SignalSafeCheckpointCancellationAsync(HostUpdateCancellationSignal signal, CancellationToken ct);
}

public sealed class HostUpdateSchedulerCancellationBridge
{
    private readonly object _gate = new();
    private Func<CancellationToken, Task>? _cancel;
    private HostUpdateCancellationSignal? _pending;
    private DateTimeOffset _pendingAt;
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromSeconds(30);

    public IDisposable Register(Func<CancellationToken, Task> cancel)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        lock (_gate)
        {
            _cancel = cancel;
        }

        return new Registration(this, cancel);
    }

    public Task CancelAsync(HostUpdateCancellationSignal signal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        Func<CancellationToken, Task>? cancel;
        lock (_gate)
        {
            cancel = _cancel;
            if (cancel is null)
            {
                _pending = signal;
                _pendingAt = DateTimeOffset.UtcNow;
            }
        }

        return cancel is null ? Task.CompletedTask : cancel(ct);
    }

    public HostUpdateCancellationSignal? ConsumePending()
    {
        lock (_gate)
        {
            HostUpdateCancellationSignal? pending = _pending is not null && DateTimeOffset.UtcNow - _pendingAt <= PendingLifetime
                ? _pending
                : null;
            _pending = null;
            return pending;
        }
    }

    private void Unregister(Func<CancellationToken, Task> cancel)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_cancel, cancel))
            {
                _cancel = null;
            }
        }
    }

    private sealed class Registration(HostUpdateSchedulerCancellationBridge owner, Func<CancellationToken, Task> cancel) : IDisposable
    {
        public void Dispose() => owner.Unregister(cancel);
    }
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
    Reject,
    Reserve
}

public sealed record HostUpdateReplayDecision(HostUpdateReplayDisposition Disposition, string CorrelationId, bool Reused);

public interface IHostUpdateReplayStore
{
    Task<HostUpdateReplayDecision> DecideAsync(VerifiedHostUpdateCandidate candidate, HostUpdateReplayIntent intent, CancellationToken ct);
}

public interface IHostUpdateReplayAnchor
{
    Task<long> ReadEpochAsync(CancellationToken ct);

    Task<string> ReadStateHashAsync(CancellationToken ct);

    Task AdvanceEpochAsync(long epoch, string stateHash, CancellationToken ct);
}

public sealed class UnavailableHostUpdateReplayAnchor : IHostUpdateReplayAnchor, IHostUpdateAvailability
{
    public bool IsAvailable => false;

    public string UnavailableReason => "host_update_replay_anchor_not_available";

    public Task<long> ReadEpochAsync(CancellationToken ct) => throw new NotSupportedException("host_update_replay_anchor_not_available");

    public Task<string> ReadStateHashAsync(CancellationToken ct) => throw new NotSupportedException("host_update_replay_anchor_not_available");

    public Task AdvanceEpochAsync(long epoch, string stateHash, CancellationToken ct) => throw new NotSupportedException("host_update_replay_anchor_not_available");
}

public sealed class FileHostUpdateReplayStore(string rootPath, IHostUpdateReplayAnchor anchor, Action<string>? commitBoundary = null) : IHostUpdateReplayStore, IDisposable
{
    private readonly string _path = Path.Combine(rootPath ?? throw new ArgumentNullException(nameof(rootPath)), "host-update-replay.json");
    private readonly string _stagedPath = Path.Combine(rootPath, "host-update-replay.json.staged");
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
            string anchorStateHash = await ReadAnchorStateHashAsync(ct);
            HostUpdateReplayFileState state = await LoadAndRecoverAsync(anchorEpoch, anchorStateHash, ct);
            string ns = Namespace(candidate);
            if (state.Identities.TryGetValue(candidate.Identity, out HostUpdateReplayIdentityRecord? existing))
            {
                return new(existing.Disposition, existing.CorrelationId, true);
            }

            state.HighWaterByNamespace.TryGetValue(ns, out HostUpdateReplayHighWater? highWater);
            if (highWater is not null && (candidate.Sequence < highWater.Sequence ||
                (candidate.Sequence == highWater.Sequence && !string.Equals(highWater.Identity, candidate.Identity, StringComparison.Ordinal))))
            {
                return await PersistRejectionAsync(state, candidate, anchorEpoch, ct);
            }

            string correlationId = "decision:" + candidate.Identity;
            if (intent == HostUpdateReplayIntent.Reserve)
            {
                // Reserve is advisory only; crash-safe exclusion remains provided by the
                // execution lock and durable journal. Persisted reservations are tracked in #2757.
                return new(HostUpdateReplayDisposition.Accepted, correlationId, false);
            }

            HostUpdateReplayDisposition disposition = intent == HostUpdateReplayIntent.Admit ? HostUpdateReplayDisposition.Accepted : HostUpdateReplayDisposition.Rejected;
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

    private async Task<string> ReadAnchorStateHashAsync(CancellationToken ct)
    {
        try
        {
            return await _anchor.ReadStateHashAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException("host_update_replay_anchor_unavailable", ex);
        }
    }

    private static string StateHash(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private async Task<HostUpdateReplayFileState> LoadAndRecoverAsync(long anchorEpoch, string anchorStateHash, CancellationToken ct)
    {
        HostUpdateReplayFileState? current = File.Exists(_path) ? await ReadStateAsync(_path, ct) : null;

        if (current is not null && current.Epoch > anchorEpoch)
        {
            throw new InvalidDataException("host_update_replay_anchor_rollback");
        }

        if (current?.Epoch == anchorEpoch)
        {
            if (!string.Equals(StateHash(_path), anchorStateHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("host_update_replay_state_anchor_hash_mismatch");
            }

            DeleteUncommittedStage();
            return current;
        }
        // The anchor journal append is the commit record. A snapshot exactly one committed epoch
        // behind may move forward only from the durable, checksummed stage for that anchor head.
        HostUpdateReplayFileState? staged = File.Exists(_stagedPath) ? await ReadStateAsync(_stagedPath, ct) : null;
        if ((current is null && anchorEpoch == 0 || current?.Epoch == anchorEpoch - 1) && staged?.Epoch == anchorEpoch &&
            string.Equals(StateHash(_stagedPath), anchorStateHash, StringComparison.Ordinal))
        {
            HostStateFileSecurity.RejectReparseTarget(_path);
            File.Move(_stagedPath, _path, true);
            commitBoundary?.Invoke("forward-recovered");
            return staged;
        }

        if (current is not null && current.Epoch < anchorEpoch)
        {
            throw new InvalidDataException("host_update_replay_state_rollback");
        }

        throw new InvalidDataException("host_update_replay_state_missing");
    }

    private static async Task<HostUpdateReplayFileState> ReadStateAsync(string path, CancellationToken ct)
    {
        HostStateFileSecurity.RejectReparseTarget(path);
        try
        {
            return HostUpdateReplayPersistenceCodec.Deserialize(await File.ReadAllTextAsync(path, ct));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            throw new InvalidDataException("host_update_replay_state_invalid", ex);
        }
    }

    private async Task SaveAsync(HostUpdateReplayFileState state, long anchorEpoch, CancellationToken ct)
    {
        long epoch = Math.Max(state.Epoch, anchorEpoch) + 1;
        state.Epoch = epoch;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        HostStateFileSecurity.RejectReparseTarget(_stagedPath);
        try
        {
            await WriteDurablyAsync(_stagedPath, HostUpdateReplayPersistenceCodec.Serialize(state), ct);
            _ = await ReadStateAsync(_stagedPath, ct);
            commitBoundary?.Invoke("stage-durable");
            try
            {
                await _anchor.AdvanceEpochAsync(epoch, StateHash(_stagedPath), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidDataException("host_update_replay_anchor_unavailable", ex);
            }

            commitBoundary?.Invoke("anchor-committed");
            HostStateFileSecurity.RejectReparseTarget(_path);
            File.Move(_stagedPath, _path, true);
            commitBoundary?.Invoke("snapshot-replaced");
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not InvalidDataException)
        {
            throw new InvalidDataException("host_update_replay_state_io_error", ex);
        }
        finally
        {
            // Before anchor commit, this is safely discardable. After anchor commit, retain it for
            // startup forward recovery if snapshot replacement did not happen.
            long committed = await ReadAnchorEpochBestEffortAsync();
            if (committed < epoch || File.Exists(_path) && TryReadEpoch(_path) == epoch)
            {
                DeleteUncommittedStage();
            }
        }
    }

    private async Task<long> ReadAnchorEpochBestEffortAsync()
    {
        try
        { return await _anchor.ReadEpochAsync(CancellationToken.None); }
        catch { return -1; }
    }

    private static long TryReadEpoch(string path)
    {
        try
        { return HostUpdateReplayPersistenceCodec.Deserialize(File.ReadAllText(path)).Epoch; }
        catch { return -1; }
    }

    internal static async Task WriteDurablyAsync(string path, string json, CancellationToken ct)
    {
        await using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(json), ct);
        await stream.FlushAsync(ct);
        stream.Flush(true);
    }

    private void DeleteUncommittedStage()
    {
        if (File.Exists(_stagedPath))
        {
            HostStateFileSecurity.RejectReparseTarget(_stagedPath);
            File.Delete(_stagedPath);
        }
    }

    public void Dispose() => _gate.Dispose();
}

internal sealed record HostUpdateReplayHighWater(long Sequence, string Identity);
internal sealed record HostUpdateReplayIdentityRecord(long Sequence, HostUpdateReplayDisposition Disposition, string CorrelationId);

internal sealed class HostUpdateReplayFileState
{
    public int Version { get; init; } = HostUpdateReplayPersistenceCodec.CurrentVersion;
    public long Epoch { get; set; }
    public Dictionary<string, HostUpdateReplayHighWater> HighWaterByNamespace { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, HostUpdateReplayIdentityRecord> Identities { get; set; } = new(StringComparer.Ordinal);
}

internal static class HostUpdateReplayPersistenceCodec
{
    internal const int CurrentVersion = 1;
    private sealed record HighWaterDto(long Sequence, string Identity);
    private sealed record IdentityDto(long Sequence, string Disposition, string CorrelationId);
    private sealed record FileDto(int Version, long Epoch, string Checksum, Dictionary<string, HighWaterDto> HighWaterByNamespace, Dictionary<string, IdentityDto> Identities);

    internal static HostUpdateReplayFileState Empty(long epoch = 0) => new() { Epoch = epoch };

    internal static string Serialize(HostUpdateReplayFileState state)
    {
        FileDto dto = new(CurrentVersion, state.Epoch, ComputeChecksum(state),
            state.HighWaterByNamespace.ToDictionary(kv => kv.Key, kv => new HighWaterDto(kv.Value.Sequence, kv.Value.Identity), StringComparer.Ordinal),
            state.Identities.ToDictionary(kv => kv.Key, kv => new IdentityDto(kv.Value.Sequence, kv.Value.Disposition.ToString(), kv.Value.CorrelationId), StringComparer.Ordinal));
        return JsonSerializer.Serialize(dto);
    }

    internal static HostUpdateReplayFileState Deserialize(string json)
    {
        FileDto? dto = JsonSerializer.Deserialize<FileDto>(json);
        if (dto is null || dto.Version != CurrentVersion || dto.Epoch < 0 || dto.HighWaterByNamespace is null || dto.Identities is null)
        {
            throw new InvalidDataException("host_update_replay_state_invalid");
        }

        HostUpdateReplayFileState state = new()
        {
            Epoch = dto.Epoch,
            HighWaterByNamespace = dto.HighWaterByNamespace.ToDictionary(kv => kv.Key, kv => new HostUpdateReplayHighWater(kv.Value.Sequence, kv.Value.Identity), StringComparer.Ordinal),
            Identities = dto.Identities.ToDictionary(kv => kv.Key, kv => new HostUpdateReplayIdentityRecord(kv.Value.Sequence, Enum.Parse<HostUpdateReplayDisposition>(kv.Value.Disposition), kv.Value.CorrelationId), StringComparer.Ordinal),
        };
        if (!string.Equals(ComputeChecksum(state), dto.Checksum, StringComparison.Ordinal))
        {
            throw new InvalidDataException("host_update_replay_state_corrupt");
        }

        return state;
    }

    private static string ComputeChecksum(HostUpdateReplayFileState state) => HostUpdateCanonical.Hash(new
    {
        state.Version,
        state.Epoch,
        HighWater = state.HighWaterByNamespace.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new { Namespace = kv.Key, kv.Value.Sequence, kv.Value.Identity }),
        Identities = state.Identities.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new { Identity = kv.Key, kv.Value.Sequence, Disposition = kv.Value.Disposition.ToString(), kv.Value.CorrelationId }),
    });
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
            HostStateFileSecurity.RejectReparseTarget(_path);
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

            HostStateFileSecurity.RejectReparseTarget(temporary);
            HostStateFileSecurity.RejectReparseTarget(_path);
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

/// <summary>Retains the latest automatic scheduler observation for read-only status surfaces.</summary>
public sealed class HostUpdateSchedulerStatusHolder
{
    private readonly object _gate = new();
    private HostUpdateSchedulerStatus _current = new(
        false,
        false,
        false,
        UpdateChannelSettings.StableChannel,
        0,
        null,
        null,
        0,
        HostUpdateSchedulerReason.Disabled);

    public HostUpdateSchedulerStatus Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Update(HostUpdateSchedulerStatus status)
    {
        lock (_gate)
        {
            _current = status;
        }
    }
}

/// <summary>
/// Runs policy-driven scheduling in a scope per tick. The scheduler itself remains fail-closed
/// when replay, policy-fence, admission, or execution facilities are unavailable.
/// </summary>
public sealed class HostUpdateSchedulerHostedService(
    HostUpdateScheduler scheduler,
    HostUpdateSchedulerStatusHolder statusHolder,
    ILogger<HostUpdateSchedulerHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan PollCadence = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                HostUpdateSchedulerStatus status = await scheduler.TickAsync(stoppingToken).ConfigureAwait(false);
                statusHolder.Update(status);
                await Task.Delay(PollCadence, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "host_update_scheduler_tick_failed");
                await Task.Delay(PollCadence, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}

public sealed class HostUpdateScheduler(
    IHostUpdateSchedulerSettings settings,
    IHostUpdateSchedulerCandidateCache cache,
    IHostUpdateReplayStore replayStore,
    IHostUpdatePolicyFence policyFence,
    IHostUpdateSchedulerExecutor? executor,
    IHostUpdateClock clock,
    IHostUpdateJitter jitter,
    ILogger<HostUpdateScheduler>? logger = null,
    IHostUpdateAdmissionFence? admissionFence = null,
    IServiceScopeFactory? scopeFactory = null,
    HostUpdateSchedulerCancellationBridge? cancellationBridge = null) : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan MaxJitter = TimeSpan.FromMinutes(5);

    private readonly ILogger<HostUpdateScheduler> _logger = logger ?? NullLogger<HostUpdateScheduler>.Instance;
    private readonly IHostUpdateAdmissionFence _admissionFence = admissionFence ?? new InactiveHostUpdateAdmissionFence();
    private readonly IServiceScopeFactory? _scopeFactory = scopeFactory;
    private readonly HostUpdateSchedulerCancellationBridge? _cancellationBridge = cancellationBridge;
    private readonly IHostUpdateSchedulerExecutor? _directExecutor = executor;
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private HostUpdateSchedulerStatus _status = new(false, false, false, UpdateChannelSettings.StableChannel, 0, null, null, 0, HostUpdateSchedulerReason.Disabled);
    private readonly object _cancellationGate = new();
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _activeRequestId;
    private string? _activeOperationToken;
    private bool _activeRequestSignaled;
    private int _disposed;
    private int _tickGateDisposed;

    public HostUpdateSchedulerStatus Status => _status;

    public string? ActiveRequestId
    {
        get
        {
            lock (_cancellationGate)
            {
                return _activeRequestId;
            }
        }
    }

    public async Task<HostUpdateSchedulerStatus> TickAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0 || ct.IsCancellationRequested)
        {
            return _status with { Reason = HostUpdateSchedulerReason.HostShutdown };
        }

        bool entered;
        try
        {
            entered = await _tickGate.WaitAsync(0, ct);
        }
        catch (ObjectDisposedException)
        {
            return _status with { Reason = HostUpdateSchedulerReason.HostShutdown };
        }

        if (!entered)
        {
            return _status with { Reason = HostUpdateSchedulerReason.UpdateAlreadyRunning };
        }

        try
        {
            if (Volatile.Read(ref _disposed) != 0 || ct.IsCancellationRequested)
            {
                return _status with { Reason = HostUpdateSchedulerReason.HostShutdown };
            }

            DateTimeOffset now = clock.UtcNow;

            if (_status.NextPollAt is DateTimeOffset nextPollAt && now < nextPollAt)
            {
                return _status;
            }

            if (settings is IHostUpdatePolicyBackedSchedulerSettings policyBackedSettings)
            {
                HostUpdatePolicyReadResult policyResult = policyBackedSettings.ReadPolicy();
                if (!policyResult.Available)
                {
                    return _status with { Reason = HostUpdateSchedulerReason.PolicyUnavailable };
                }
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
                return Backoff(HostUpdateSchedulerReason.CandidateEvidenceStale, "cache_error");
            }

            VerifiedHostUpdateCandidate? candidate = cache.Current;
            if (candidate is null)
            {
                return _status with { Reason = HostUpdateSchedulerReason.NoCandidate };
            }

            HostUpdateSchedulerReason? gateFailure = Gate(candidate, current, now);
            if (gateFailure is not null)
            {
                if (ShouldPersistTerminalRejection(candidate, gateFailure.Value))
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
                }

                return Backoff(gateFailure.Value, candidate.Identity);
            }

            HostUpdateReplayDecision decision;
            try
            {
                decision = await replayStore.DecideAsync(candidate, HostUpdateReplayIntent.Reserve, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "host_update_replay_store_unavailable");
                return _status with { Reason = HostUpdateSchedulerReason.ReplayStoreUnavailable };
            }

            if (decision.Disposition != HostUpdateReplayDisposition.Accepted || decision.Reused)
            {
                HostUpdateSchedulerReason reason = decision.Disposition == HostUpdateReplayDisposition.Superseded
                    ? HostUpdateSchedulerReason.ReplaySuperseded
                    : HostUpdateSchedulerReason.ReplayRejected;
                return Backoff(reason, candidate.Identity);
            }

            if (settings is IHostUpdatePolicyBackedSchedulerSettings freshPolicyBackedSettings)
            {
                HostUpdatePolicyReadResult freshPolicyResult = freshPolicyBackedSettings.ReadPolicy();
                if (!freshPolicyResult.Available)
                {
                    return Backoff(HostUpdateSchedulerReason.PolicyUnavailable, candidate.Identity);
                }
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

            HostUpdateAdmissionFenceStatus admission = _admissionFence.GetStatus();
            if (admission.BlocksAdmission)
            {
                return Backoff(HostUpdateSchedulerReason.AdmissionFenceActive, admission.OperationId ?? candidate.Identity);
            }

            // Allocated per invocation so a cancellation signal captured for an earlier run of
            // the same request ID cannot be delivered to this one.
            string operationToken = Guid.NewGuid().ToString("N");
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
                candidate.PlatformDigests,
                operationToken);

            lock (_cancellationGate)
            {
                _activeRequestId = request.RequestId;
                _activeOperationToken = operationToken;
                _activeRequestSignaled = false;
            }
            try
            {
                using IServiceScope? executionScope = _scopeFactory?.CreateScope();
                IHostUpdateSchedulerExecutor executionAdapter = _directExecutor ?? executionScope?.ServiceProvider.GetRequiredService<IHostUpdateSchedulerExecutor>()
                    ?? throw new InvalidOperationException("host_update_scheduler_executor_scope_unavailable");
                using IDisposable? cancellationRegistration = _cancellationBridge?.Register(
                    cancellationToken => executionAdapter.SignalSafeCheckpointCancellationAsync(
                        new HostUpdateCancellationSignal(request.RequestId, operationToken), cancellationToken));
                HostUpdateCancellationSignal? pending = _cancellationBridge?.ConsumePending();
                if (pending is not null && pending == new HostUpdateCancellationSignal(request.RequestId, operationToken))
                {
                    executionAdapter.PreArmCancellation(pending);
                }
                HostUpdateExecutorResponse response = await executionAdapter.ExecuteAsync(request, ct);
                HostUpdateSchedulerReason reason = response.Result switch
                {
                    HostUpdateExecutorResult.Accepted => HostUpdateSchedulerReason.Admitted,
                    HostUpdateExecutorResult.Refused => HostUpdateSchedulerReason.ExecutorRefused,
                    HostUpdateExecutorResult.Failed => HostUpdateSchedulerReason.ExecutorFailed,
                    HostUpdateExecutorResult.RecoveryRequired => HostUpdateSchedulerReason.RecoveryRequired,
                    _ => HostUpdateSchedulerReason.ExecutorFailed
                };
                _status = response.Result == HostUpdateExecutorResult.Accepted
                    ? await CommitSuccessfulExecutionAsync(candidate, now, fresh, reason, ct)
                    : Backoff(reason, candidate.Identity);
                return _status;
            }
            finally
            {
                lock (_cancellationGate)
                {
                    if (IsActiveGeneration(request.RequestId, operationToken))
                    {
                        _activeRequestId = null;
                        _activeOperationToken = null;
                        _activeRequestSignaled = false;
                    }
                }
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

    private async Task<HostUpdateSchedulerStatus> CommitSuccessfulExecutionAsync(
        VerifiedHostUpdateCandidate candidate,
        DateTimeOffset now,
        HostUpdateSchedulerSettings fresh,
        HostUpdateSchedulerReason reason,
        CancellationToken ct)
    {
        try
        {
            HostUpdateReplayDecision committed = await replayStore.DecideAsync(candidate, HostUpdateReplayIntent.Admit, ct);
            if (committed.Disposition != HostUpdateReplayDisposition.Accepted)
            {
                _logger.LogError(
                    "host_update_replay_commit_rejected_after_success: {Disposition}",
                    committed.Disposition);
                return Backoff(HostUpdateSchedulerReason.ReplayRejected, candidate.Identity);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "host_update_replay_commit_failed");
            return Backoff(HostUpdateSchedulerReason.ReplayStoreUnavailable, candidate.Identity);
        }

        return _status with
        {
            ConsecutiveFailures = 0,
            NextPollAt = SafeAdd(now, fresh.EffectivePollInterval),
            Reason = reason
        };
    }

    /// <summary>
    /// Signals the currently running execution to stop at its next safe checkpoint. The captured
    /// (request ID, operation token) pair pins the signal to exactly one execution generation, so
    /// a delayed delivery cannot cancel a later run that reuses the same request ID, and a failed
    /// delivery for a finished generation cannot clear the newer generation's signalled state.
    /// </summary>
    public async Task<HostUpdateCancellationResult> SignalSafeCheckpointCancellationAsync(CancellationToken ct = default)
    {
        HostUpdateCancellationSignal signal;
        lock (_cancellationGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || _activeRequestId is null || _activeOperationToken is null)
            {
                return HostUpdateCancellationResult.NoActiveExecution;
            }

            if (_activeRequestSignaled)
            {
                return HostUpdateCancellationResult.AlreadySignaled;
            }

            signal = new HostUpdateCancellationSignal(_activeRequestId, _activeOperationToken);

            // Claimed up-front so concurrent callers cannot deliver twice; released again below
            // only when delivery fails and this generation is still the active one.
            _activeRequestSignaled = true;
        }

        try
        {
            if (_directExecutor is not null)
            {
                await _directExecutor.SignalSafeCheckpointCancellationAsync(signal, ct).ConfigureAwait(false);
            }
            else if (_cancellationBridge is not null)
            {
                await _cancellationBridge.CancelAsync(signal, ct).ConfigureAwait(false);
            }
            return HostUpdateCancellationResult.Signaled;
        }
        catch
        {
            lock (_cancellationGate)
            {
                if (IsActiveGeneration(signal.RequestId, signal.OperationToken))
                {
                    _activeRequestSignaled = false;
                }
            }
            throw;
        }
    }

    private bool IsActiveGeneration(string requestId, string operationToken) =>
        string.Equals(_activeRequestId, requestId, StringComparison.Ordinal) &&
        string.Equals(_activeOperationToken, operationToken, StringComparison.Ordinal);

#pragma warning disable VSTHRD002
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
#pragma warning disable VSTHRD003
            await _disposeCompletion.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            return;
        }

        HostUpdateCancellationSignal? signal = null;
        lock (_cancellationGate)
        {
            if (_activeRequestId is not null && _activeOperationToken is not null && !_activeRequestSignaled)
            {
                signal = new HostUpdateCancellationSignal(_activeRequestId, _activeOperationToken);
                _activeRequestSignaled = true;
            }
        }

        try
        {
            if (signal is not null)
            {
                try
                {
                    if (_directExecutor is not null)
                    {
                        await _directExecutor.SignalSafeCheckpointCancellationAsync(signal, CancellationToken.None).ConfigureAwait(false);
                    }
                    else if (_cancellationBridge is not null)
                    {
                        await _cancellationBridge.CancelAsync(signal, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "host_update_scheduler_shutdown_cancellation_failed");
                }
            }

            if (!await _tickGate.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false))
            {
                _logger.LogWarning("host_update_scheduler_shutdown_wait_timeout");
                return;
            }
            if (Interlocked.Exchange(ref _tickGateDisposed, 1) == 0)
            {
                _tickGate.Dispose();
            }
        }
        finally
        {
            _disposeCompletion.TrySetResult();
        }
    }

    private static bool ShouldPersistTerminalRejection(VerifiedHostUpdateCandidate candidate, HostUpdateSchedulerReason reason) =>
        candidate.CryptographicallyVerified && reason is
            HostUpdateSchedulerReason.CandidateInvalid or
            HostUpdateSchedulerReason.ChannelMismatch;

    private HostUpdateSchedulerReason? Gate(VerifiedHostUpdateCandidate candidate, HostUpdateSchedulerSettings current, DateTimeOffset now)
    {
        if (!candidate.IsWithinFreshnessWindow(now) || !string.IsNullOrWhiteSpace(cache.LastError))
        {
            return HostUpdateSchedulerReason.CandidateEvidenceStale;
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

        if (!candidate.IsNewer)
        {
            return HostUpdateSchedulerReason.CandidateNotEligible;
        }

        if (!candidate.IsEligible)
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

public sealed class InstallationSeededHostUpdateJitter : IHostUpdateJitter
{
    private readonly string _installationSeed;

    public InstallationSeededHostUpdateJitter(string installationSeed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationSeed);
        _installationSeed = installationSeed;
    }

    public TimeSpan For(string identity, int attempt)
    {
        string seed = $"{_installationSeed}:{identity}:{attempt}";
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        uint value = BitConverter.ToUInt32(digest, 0);
        return TimeSpan.FromSeconds(value % (5 * 60));
    }
}

public sealed class ZeroHostUpdateJitter : IHostUpdateJitter
{
    public TimeSpan For(string identity, int attempt) => TimeSpan.Zero;
}
