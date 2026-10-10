using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.HostUpdates;

public enum HostUpdateDaemonLifecycle
{
    Starting,
    Running,
    Stopping,
    Stopped,
    Failed,
}

/// <summary>
/// Redacted daemon health (issue #3114). Every field is a fixed code, count, state or time: no
/// paths, host or installation identity, key material, configuration values or exception text.
/// </summary>
public sealed record HostUpdateDaemonStatus(
    HostUpdateDaemonLifecycle Lifecycle,
    string Code,
    bool ExecutionEnabled,
    string ExecutionGateCode,
    bool Enrolled,
    HostUpdateDaemonIdentityStorageState IdentityStorage,
    string IdentityStorageCode,
    string JournalCode,
    int? ReleaseCount,
    int? InFlightCount,
    int? RecoveryRequiredCount,
    DateTimeOffset? LastCycleAt,
    int ConsecutiveFailures,
    int? NextCycleSeconds,
    string VerificationCode = "verification_not_read",
    HostUpdateDaemonCheckpointStatus? Checkpoint = null);

/// <summary>Receives each published daemon status (for example the CLI writes one line per status).</summary>
public interface IHostUpdateDaemonStatusSink
{
    void Publish(HostUpdateDaemonStatus status);
}

/// <summary>Journal-derived state the daemon loads each cycle, or a fixed failure/lock code.</summary>
public sealed record HostUpdateDaemonJournalSnapshot(
    string Code,
    bool Failed,
    int? ReleaseCount,
    int? InFlightCount,
    int? RecoveryRequiredCount,
    string? VerificationCode = null,
    HostUpdateDaemonCheckpointStatus? Checkpoint = null)
{
    public const string OkCode = "journal_ok";

    public const string NoVerificationCode = "verification_none";

    public static HostUpdateDaemonJournalSnapshot LockHeld { get; } = new(HostUpdateDaemonExecutionDispatcher.ExecutionLockHeldCode, false, null, null, null);
}

/// <summary>Reads durable state from the existing execution journal; the daemon keeps no store of its own.</summary>
public interface IHostUpdateDaemonJournalReader
{
    HostUpdateDaemonJournalSnapshot Read();
}

/// <summary>
/// Reconciles the shared hash-chained execution journal only while holding the shared execution lock,
/// exactly as the host-local <c>status</c> command does. A held lock (the executor, CLI or a manual
/// recovery is running) is reported and never waited on, and an existing lock file is not rewritten.
/// When a verification journal is supplied, the latest #3116 verification outcome is reported as a
/// fixed code; an unreadable or tampered verification journal fails the cycle closed. The
/// verification journal is read outside the execution lease because it serializes its own readers
/// and writers on a dedicated lock, so a concurrent append is waited on briefly, never observed torn.
/// An interrupted, request-bound execution is moved to RecoveryRequired without invoking any
/// execution or recovery side effect. At most one such transition is appended per cycle.
/// </summary>
public sealed partial class HostUpdateDaemonJournalReader(
    string stateDirectory,
    IHostUpdateExecutionLock executionLock,
    IHostUpdateExecutionJournal journal,
    IHostUpdateDaemonVerificationJournal? verificationJournal = null)
    : IHostUpdateDaemonJournalReader
{
    public HostUpdateDaemonJournalSnapshot Read()
    {
        HostUpdateDaemonJournalSnapshot execution = ReadExecution();
        if (verificationJournal is null)
        {
            return execution;
        }

        try
        {
            IReadOnlyList<HostUpdateDaemonVerificationEvidence> evidence = verificationJournal.ReadAll();
            if (evidence.Count == 0)
            {
                return execution with { VerificationCode = HostUpdateDaemonJournalSnapshot.NoVerificationCode };
            }

            HostUpdateDaemonVerificationEvidence last = evidence[^1];
            if (evidence.Count >= verificationJournal.Capacity)
            {
                // A full journal refuses every new verification; surface it instead of the last code.
                return execution with { Failed = true, VerificationCode = "journal_verification_full" };
            }

            string code = last.Outcome + ":" + last.Code;
            return execution with { VerificationCode = VerificationCodePattern().IsMatch(code) ? code : "verification_code_invalid" };
        }
        catch (Exception ex) when (IsStateFailure(ex))
        {
            string code = ex is InvalidDataException data && data.Message is { } message && JournalCode().IsMatch(message)
                ? message
                : "verification_state_unreadable";
            return execution with { Failed = true, VerificationCode = code };
        }
    }

    private HostUpdateDaemonJournalSnapshot ReadExecution()
    {
        IHostUpdateExecutionLease? lease;
        try
        {
            lease = FileHostUpdateExecutionLock.TryAcquireExisting(Path.Join(stateDirectory, FileHostUpdateExecutionLock.FileName))
                ?? executionLock.Acquire(TimeSpan.Zero, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            return HostUpdateDaemonJournalSnapshot.LockHeld;
        }
        catch (Exception ex) when (IsStateFailure(ex))
        {
            return Failure(ex);
        }

        using (lease)
        {
            try
            {
                int inFlight = 0;
                int recoveryRequired = 0;
                IReadOnlyList<HostUpdateExecutionActivity> activities = journal.ReadAll();
                var histories = new Dictionary<string, List<HostUpdateExecutionActivity>>(StringComparer.Ordinal);
                foreach (HostUpdateExecutionActivity activity in activities)
                {
                    if (!HostUpdateValidation.IsReleaseId(activity.ReleaseId) || !Enum.IsDefined(activity.State))
                    {
                        throw new InvalidDataException("journal_checkpoint_invalid");
                    }

                    if (!histories.TryGetValue(activity.ReleaseId, out List<HostUpdateExecutionActivity>? history))
                    {
                        history = [];
                        histories.Add(activity.ReleaseId, history);
                    }

                    history.Add(activity);
                }

                HostUpdateDaemonCheckpointStatus checkpoint = histories.Count == 0
                    ? HostUpdateDaemonCheckpointStatus.AwaitApproval
                    : new(HostUpdateDaemonCheckpointAction.Completed, "completed", null);
                bool recordedInterruption = false;
                foreach (List<HostUpdateExecutionActivity> history in histories.Values)
                {
                    HostUpdateDaemonCheckpointStatus decision = HostUpdateDaemonCheckpoints.Evaluate(history);
                    HostUpdateExecutionState last = history[^1].State;
                    if (!recordedInterruption && decision.Code == "execution_interrupted")
                    {
                        // No executor owns the shared lock. Journal the stop so the existing CLI
                        // can recover it; never fabricate a successful checkpoint or release a fence.
                        HostUpdateExecutionActivity stopped = history[^1] with
                        {
                            ActivityId = Guid.NewGuid().ToString("N"),
                            State = HostUpdateExecutionState.RecoveryRequired,
                            Phase = "failure:daemon_interrupted",
                            RecordedAt = DateTimeOffset.UtcNow,
                            AuthorizationBaseline = null,
                        };
                        journal.Append(stopped);
                        recordedInterruption = true;
                        last = stopped.State;
                    }

                    if (HostUpdateDaemonCheckpoints.Priority(decision.Action) > HostUpdateDaemonCheckpoints.Priority(checkpoint.Action))
                    {
                        checkpoint = decision;
                    }

                    if (last == HostUpdateExecutionState.RecoveryRequired)
                    {
                        recoveryRequired++;
                    }
                    else if (last is not (HostUpdateExecutionState.Completed or HostUpdateExecutionState.Refused))
                    {
                        inFlight++;
                    }
                }

                return new(HostUpdateDaemonJournalSnapshot.OkCode, false, histories.Count, inFlight, recoveryRequired, Checkpoint: checkpoint);
            }
            catch (Exception ex) when (IsStateFailure(ex))
            {
                return Failure(ex);
            }
        }
    }

    internal static bool IsStateFailure(Exception exception) =>
        exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException
            or SecurityException or NotSupportedException or HostUpdateSubsystemUnavailableException;

    // Only fixed snake_case journal codes pass through; anything else could embed a path.
    private static HostUpdateDaemonJournalSnapshot Failure(Exception exception)
    {
        string code = exception switch
        {
            InvalidDataException data when data.Message is { } message && JournalCode().IsMatch(message) => message,
            UnauthorizedAccessException => "state_access_denied",
            HostUpdateSubsystemUnavailableException => "state_unavailable",
            _ => "state_unreadable:" + exception.GetType().Name,
        };
        return new(code, true, null, null, null);
    }

    [GeneratedRegex(@"\Ajournal_[a-z_]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex JournalCode();

    [GeneratedRegex(@"\A[a-z0-9_:\-]{1,96}\z", RegexOptions.CultureInvariant)]
    private static partial Regex VerificationCodePattern();
}

/// <summary>
/// The enrolled host-update daemon service core (issue #3114): a thin host process around the
/// existing executor, journal and lock. It holds a single-instance lease, loads durable state
/// from the shared journal each cycle, and reports redacted health. It executes nothing: the
/// only production <see cref="IHostUpdateDaemonExecutionGate"/> is permanently disabled, the
/// daemon derives defer/operator recovery decisions from durable checkpoints (#3117), and has
/// no live approval transport yet (#3115). Execution, when later authorized, goes only through
/// <see cref="HostUpdateDaemonExecutionDispatcher"/>.
/// </summary>
public sealed class HostUpdateDaemon(
    HostUpdateDaemonOptions options,
    string stateDirectory,
    string executorRootDirectory,
    IHostUpdateDaemonJournalReader journalReader,
    IHostUpdateDaemonExecutionGate gate,
    IHostUpdateDaemonStatusSink sink,
    ILogger<HostUpdateDaemon> logger,
    TimeProvider? timeProvider = null,
    Func<double>? jitterSource = null)
{
    public const string InstanceLockFileName = "daemon.lock";

    public const string AlreadyRunningCode = "daemon_already_running";

    public const string InstanceLockUnavailableCode = "daemon_instance_lock_unavailable";

    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    private readonly Func<double> jitter = jitterSource ?? Random.Shared.NextDouble;

    private int consecutiveFailures;

    public HostUpdateDaemonStatus? Status { get; private set; }

    /// <summary>
    /// Runs until <paramref name="cancellationToken"/> is canceled (or after one cycle when
    /// <paramref name="once"/>). Returns <see langword="null"/> after a clean stop, or a fixed code
    /// when the single-instance lease cannot be taken (another daemon holds it, or it is unsafe).
    /// </summary>
    public async Task<string?> RunAsync(bool once, CancellationToken cancellationToken)
    {
        IHostUpdateExecutionLease instance;
        try
        {
            instance = new FileHostUpdateExecutionLock(Path.Join(stateDirectory, InstanceLockFileName)).Acquire(TimeSpan.Zero, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            Publish(Snapshot(HostUpdateDaemonLifecycle.Failed, AlreadyRunningCode, null, null, null));
            return AlreadyRunningCode;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            Publish(Snapshot(HostUpdateDaemonLifecycle.Failed, InstanceLockUnavailableCode, null, null, null));
            return InstanceLockUnavailableCode;
        }

        using (instance)
        {
            HostUpdateDaemonIdentityStorageStatus identity = HostUpdateDaemonIdentityStorage.Inspect(options.IdentityDirectory, executorRootDirectory);
            Publish(Snapshot(HostUpdateDaemonLifecycle.Starting, "daemon_starting", identity, null, null));
            try
            {
                while (true)
                {
                    identity = HostUpdateDaemonIdentityStorage.Inspect(options.IdentityDirectory, executorRootDirectory);
                    HostUpdateDaemonJournalSnapshot journal = journalReader.Read();

                    // Configured-but-unsafe identity storage is a failed cycle, not an unenrolled fallback.
                    bool failed = journal.Failed || identity.State == HostUpdateDaemonIdentityStorageState.Invalid;
                    consecutiveFailures = failed ? consecutiveFailures + 1 : 0;
                    TimeSpan delay = NextDelay();
                    Publish(Snapshot(HostUpdateDaemonLifecycle.Running, failed ? "daemon_cycle_failed" : "daemon_cycle_completed", identity, journal, once ? null : delay));
                    if (once)
                    {
                        break;
                    }

                    await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal shutdown. Nothing is in flight: the daemon starts no execution.
            }

            Publish(Snapshot(HostUpdateDaemonLifecycle.Stopping, "daemon_stopping", identity, null, null));
            Publish(Snapshot(HostUpdateDaemonLifecycle.Stopped, "daemon_stopped", identity, null, null));
        }

        return null;
    }

    internal TimeSpan NextDelay()
    {
        double seconds = options.PollIntervalSeconds;
        if (consecutiveFailures > 0)
        {
            seconds = Math.Min(options.PollIntervalSeconds * Math.Pow(2, Math.Min(consecutiveFailures, 16)), options.MaxBackoffSeconds);
        }

        // Up to 10% jitter, capped so the backoff ceiling is never exceeded.
        seconds = Math.Min(seconds + (seconds * 0.1 * Math.Clamp(jitter(), 0, 1)), options.MaxBackoffSeconds);
        return TimeSpan.FromSeconds(Math.Max(seconds, HostUpdateDaemonOptions.MinimumPollIntervalSeconds));
    }

    private HostUpdateDaemonStatus Snapshot(
        HostUpdateDaemonLifecycle lifecycle,
        string code,
        HostUpdateDaemonIdentityStorageStatus? identity,
        HostUpdateDaemonJournalSnapshot? journal,
        TimeSpan? nextDelay)
    {
        string? gateRefusal = gate.Evaluate();
        return new(
            lifecycle,
            code,
            ExecutionEnabled: gateRefusal is null,
            ExecutionGateCode: gateRefusal ?? "execution_permitted",
            Enrolled: false,
            identity?.State ?? HostUpdateDaemonIdentityStorageState.NotConfigured,
            identity?.Code ?? "identity_not_inspected",
            journal?.Code ?? Status?.JournalCode ?? "journal_not_read",
            journal is null ? Status?.ReleaseCount : journal.ReleaseCount,
            journal is null ? Status?.InFlightCount : journal.InFlightCount,
            journal is null ? Status?.RecoveryRequiredCount : journal.RecoveryRequiredCount,
            journal is null ? Status?.LastCycleAt : time.GetUtcNow(),
            consecutiveFailures,
            nextDelay is null ? null : (int)Math.Ceiling(nextDelay.Value.TotalSeconds),
            journal?.VerificationCode ?? Status?.VerificationCode ?? "verification_not_read",
            journal is null ? Status?.Checkpoint : journal.Checkpoint);
    }

    private void Publish(HostUpdateDaemonStatus status)
    {
        Status = status;
        logger.LogInformation(
            "Host-update daemon {Lifecycle} {Code}; execution gate {GateCode}; identity storage {IdentityCode}; journal {JournalCode}",
            status.Lifecycle,
            status.Code,
            status.ExecutionGateCode,
            status.IdentityStorageCode,
            status.JournalCode);
        sink.Publish(status);
    }
}
