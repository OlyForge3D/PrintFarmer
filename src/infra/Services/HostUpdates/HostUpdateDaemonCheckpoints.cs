using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// A journal-derived action, not an execution grant. No API response or verification audit can
/// turn a checkpoint into permission to start another operation.
/// </summary>
public enum HostUpdateDaemonCheckpointAction
{
    AwaitApproval,
    AwaitConfirmation,
    NeedsOperator,
    Completed,
}

/// <summary>Redacted aggregate of durable checkpoints, suitable for local and reconnect status.</summary>
public sealed record HostUpdateDaemonCheckpointStatus(
    HostUpdateDaemonCheckpointAction Action,
    string Code,
    string? RecoveryHint)
{
    public static HostUpdateDaemonCheckpointStatus AwaitApproval { get; } =
        new(HostUpdateDaemonCheckpointAction.AwaitApproval, "approval_required", null);

    /// <summary>
    /// Rebuilds current status after connectivity returns; this is not a queue of commands or
    /// an acknowledgement of execution. The caller supplies the current protected policy revision.
    /// </summary>
    public HostUpdateDaemonStatusReportDto ToReport(long hostPolicyRevision, DateTimeOffset now) =>
        new()
        {
            DaemonState = Action switch
            {
                HostUpdateDaemonCheckpointAction.NeedsOperator => HostUpdateDaemonState.NeedsOperator,
                HostUpdateDaemonCheckpointAction.Completed => HostUpdateDaemonState.Idle,
                _ => HostUpdateDaemonState.Deferred,
            },
            ExecutionMode = HostUpdateDaemonExecutionMode.None,
            HostPolicyRevision = hostPolicyRevision,
            CurrentCheckpoint = Code,
            LastResult = new()
            {
                Outcome = Action == HostUpdateDaemonCheckpointAction.NeedsOperator
                    ? HostUpdateDaemonResultOutcome.RecoveryRequired
                    : HostUpdateDaemonResultOutcome.None,
            },
            DeferReasons = Action is HostUpdateDaemonCheckpointAction.AwaitApproval or HostUpdateDaemonCheckpointAction.AwaitConfirmation ? [Code] : [],
            RecoveryHints = RecoveryHint is null ? [] : [RecoveryHint],
            ReportedAt = now,
        };
}

/// <summary>
/// Reduces each release's durable history. Staging/preflight is not admission: without a durable
/// signed confirmation it must defer. Interrupted side effects require the existing host-local
/// recovery flow, never a replay by the polling loop. This deliberately grants no offline resume.
/// </summary>
internal static class HostUpdateDaemonCheckpoints
{
    internal const string PreflightRefusedCode = "preflight_refused";

    internal static HostUpdateDaemonCheckpointStatus Evaluate(IReadOnlyList<HostUpdateExecutionActivity> history)
    {
        HostUpdateExecutionActivity last = history[^1];
        if (last.State == HostUpdateExecutionState.Completed)
        {
            return new(HostUpdateDaemonCheckpointAction.Completed, "completed", null);
        }

        if (!HasBoundRequest(history))
        {
            return new(HostUpdateDaemonCheckpointAction.NeedsOperator, "checkpoint_binding_unproven", "host_local_status");
        }

        if (last.State == HostUpdateExecutionState.RecoveryRequired)
        {
            string code = last.Phase is "recovery:started" or "recovery:interrupted" or "recovery:unknown"
                ? "recovery_interrupted"
                : "recovery_required";
            return new(HostUpdateDaemonCheckpointAction.NeedsOperator, code, "host_local_recover");
        }

        // Inspect the whole history, not only the latest state: an incomplete fence or unsafe
        // operation must not become a staging retry if a later record regresses the state.
        if (!HostUpdateExecutor.IsBeforeAnyMutation(history))
        {
            return new(HostUpdateDaemonCheckpointAction.NeedsOperator, "execution_interrupted", "host_local_recover");
        }

        // A preflight refusal changed nothing; it needs a fresh approval, never operator recovery.
        if (last.State == HostUpdateExecutionState.Refused)
        {
            return new(HostUpdateDaemonCheckpointAction.AwaitApproval, PreflightRefusedCode, null);
        }

        return new(HostUpdateDaemonCheckpointAction.AwaitConfirmation, "confirmation_required", "host_local_status");
    }

    internal static bool HasBoundRequest(IReadOnlyList<HostUpdateExecutionActivity> history)
    {
        string? hash = history[0].RequestBindingHash;
        return !string.IsNullOrWhiteSpace(hash) && history.All(activity =>
            activity.RequestBinding is { } request &&
            request.IsValid(out _) &&
            Enum.IsDefined(request.Channel) &&
            Enum.IsDefined(request.AuthorizationKind) &&
            Enum.IsDefined(request.ImageSourceMode) &&
            string.Equals(request.ReleaseId, activity.ReleaseId, StringComparison.Ordinal) &&
            string.Equals(activity.RequestBindingHash, hash, StringComparison.Ordinal) &&
            string.Equals(HostUpdateRequestBinding.Compute(request), hash, StringComparison.Ordinal));
    }

    internal static int Priority(HostUpdateDaemonCheckpointAction action) => action switch
    {
        HostUpdateDaemonCheckpointAction.NeedsOperator => 3,
        HostUpdateDaemonCheckpointAction.AwaitConfirmation => 2,
        HostUpdateDaemonCheckpointAction.AwaitApproval => 1,
        _ => 0,
    };
}
