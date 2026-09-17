using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.HostUpdates;

#pragma warning disable CA1032 // These internal fault-code exceptions are only ever constructed with a code; standard constructors are not used.
/// <summary>Thrown when active work has not safely finished within the bounded drain timeout.</summary>
public sealed class HostUpdateDrainTimeoutException(string detail) : TimeoutException(detail);

/// <summary>
/// Thrown by a real submission/scheduling call site (Kane audit follow-up, issue #2663) when it
/// consults <see cref="IHostUpdateAdmissionGate.IsClosedAsync"/> and finds the gate closed by an
/// in-progress host update's drain step. Not thrown by the gate itself -- each protected call
/// site owns checking the gate and throwing this so the failure is attributable to exactly the
/// submission path that was rejected.
/// </summary>
public sealed class HostUpdateAdmissionClosedException() : InvalidOperationException("host_update_admission_closed");
#pragma warning restore CA1032

/// <summary>
/// Perimeter admission gate that rejects new print/slice submissions and scheduling once
/// closed. Consulted by API-facing admission points; never cancels or replays anything itself.
/// </summary>
public interface IHostUpdateAdmissionGate
{
    Task CloseAsync(CancellationToken cancellationToken);

    Task OpenAsync(CancellationToken cancellationToken);

    Task<bool> IsClosedAsync(CancellationToken cancellationToken);
}

/// <summary>Process-wide, thread-safe <see cref="IHostUpdateAdmissionGate"/>.</summary>
public sealed class InMemoryHostUpdateAdmissionGate : IHostUpdateAdmissionGate
{
    private volatile bool _closed;

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        _closed = true;
        return Task.CompletedTask;
    }

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        _closed = false;
        return Task.CompletedTask;
    }

    public Task<bool> IsClosedAsync(CancellationToken cancellationToken) => Task.FromResult(_closed);
}

/// <summary>
/// Reports how much work is still in flight that must be allowed to finish (or be explicitly
/// reconciled by an operator) before a coordinated backup/migration/apply may proceed.
/// </summary>
public interface IActiveWorkObservationPort
{
    /// <summary>Count of active prints/physical commands that block a safe drain.</summary>
    Task<int> CountActiveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Observes active print jobs (Assigned/Starting/Printing/Paused) and pending/processing
/// outbox physical commands via <see cref="AppDbContext"/>. Neither is ever cancelled here;
/// unresolved work simply blocks the drain until it completes or the bounded timeout expires.
/// </summary>
public sealed class DbActiveWorkObservationPort(AppDbContext db) : IActiveWorkObservationPort
{
    private static readonly PrintJobStatus[] ActiveStatuses =
    [
        PrintJobStatus.Assigned,
        PrintJobStatus.Starting,
        PrintJobStatus.Printing,
        PrintJobStatus.Paused,
    ];

    public async Task<int> CountActiveAsync(CancellationToken cancellationToken)
    {
        int activePrints = await db.PrintJobs
            .CountAsync(job => ActiveStatuses.Contains(job.Status), cancellationToken)
            .ConfigureAwait(false);
        int pendingCommands = await db.QueueDispatchOutbox
            .CountAsync(
                evt => evt.Status == QueueOutboxEventStatus.Pending || evt.Status == QueueOutboxEventStatus.Processing,
                cancellationToken)
            .ConfigureAwait(false);
        return activePrints + pendingCommands;
    }
}

/// <summary>
/// Closes new admission and waits, bounded, for active prints and pending physical commands to
/// finish naturally. Never blindly cancels in-flight work; a timeout leaves the executor in
/// <see cref="HostUpdateExecutionState.RecoveryRequired"/> for explicit operator reconciliation.
/// </summary>
public sealed class HostUpdateDrainCoordinator(
    IHostUpdateAdmissionGate admissionGate,
    IActiveWorkObservationPort activeWork,
    TimeSpan drainTimeout,
    TimeSpan pollInterval,
    TimeProvider? timeProvider = null) : IHostUpdateDrainCoordinator
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task RunAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await admissionGate.CloseAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset deadline = _timeProvider.GetUtcNow() + drainTimeout;
        while (true)
        {
            int active = await activeWork.CountActiveAsync(cancellationToken).ConfigureAwait(false);
            if (active == 0)
            {
                return;
            }

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                throw new HostUpdateDrainTimeoutException($"drain_timeout_active={active}");
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Runs the drain step of the host update executor.</summary>
public interface IHostUpdateDrainCoordinator
{
    Task RunAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken);
}
