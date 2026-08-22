using Farm.Slicer.Module.Domain;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// Repository for <see cref="Worker"/> entity operations within the slicer module.
/// </summary>
public interface IWorkerRepository
{
    /// <summary>Adds a new worker.</summary>
    /// <param name="worker">The worker entity to add.</param>
    Task AddAsync(Worker worker);

    /// <summary>Gets a worker by its unique identifier.</summary>
    /// <param name="id">The worker identifier.</param>
    Task<Worker?> GetByIdAsync(Guid id);

    /// <summary>Gets a worker by its service identifier from the registry.</summary>
    /// <param name="serviceId">The service identifier.</param>
    Task<Worker?> GetByServiceIdAsync(string serviceId);

    /// <summary>Gets all workers with pagination.</summary>
    /// <param name="limit">Maximum number of workers to return.</param>
    /// <param name="offset">Number of workers to skip.</param>
    Task<IReadOnlyList<Worker>> GetAllAsync(int limit = 100, int offset = 0);

    /// <summary>Gets workers filtered by status.</summary>
    /// <param name="status">The status to filter by.</param>
    /// <param name="limit">Maximum number of workers to return.</param>
    /// <param name="offset">Number of workers to skip.</param>
    Task<IReadOnlyList<Worker>> GetByStatusAsync(string status, int limit = 100, int offset = 0);

    /// <summary>Gets online workers with available processing slots.</summary>
    /// <param name="limit">Maximum number of workers to return.</param>
    Task<IReadOnlyList<Worker>> GetAvailableWorkersAsync(int limit = 100);

    /// <summary>Gets workers with the specified capabilities.</summary>
    /// <param name="requiredCapabilities">Required capability names.</param>
    /// <param name="limit">Maximum number of workers to return.</param>
    Task<IReadOnlyList<Worker>> GetWorkersByCapabilitiesAsync(string[] requiredCapabilities, int limit = 100);

    /// <summary>Gets workers that haven't sent a heartbeat within the timeout period.</summary>
    /// <param name="heartbeatTimeout">The timeout duration.</param>
    Task<IReadOnlyList<Worker>> GetStaleWorkersAsync(TimeSpan heartbeatTimeout);

    /// <summary>Updates a worker's status.</summary>
    /// <param name="id">The worker identifier.</param>
    /// <param name="status">The new status.</param>
    Task UpdateStatusAsync(Guid id, string status);

    /// <summary>Updates a worker's heartbeat and slot availability.</summary>
    /// <param name="id">The worker identifier.</param>
    /// <param name="freeSlots">Number of available processing slots.</param>
    /// <param name="totalSlots">Total number of processing slots.</param>
    Task UpdateHeartbeatAsync(Guid id, int freeSlots, int totalSlots);

    /// <summary>Increments the active job count for a worker.</summary>
    /// <param name="id">The worker identifier.</param>
    Task IncrementActiveJobsAsync(Guid id);

    /// <summary>Decrements the active job count and records job result metrics.</summary>
    /// <param name="id">The worker identifier.</param>
    /// <param name="success">Whether the job completed successfully.</param>
    /// <param name="processingTimeSeconds">Job processing time in seconds.</param>
    Task DecrementActiveJobsAsync(Guid id, bool success, double processingTimeSeconds);

    /// <summary>Disables a worker with a reason and an attributed source.</summary>
    /// <param name="id">The worker identifier.</param>
    /// <param name="reason">The human-readable reason for disabling. Display only.</param>
    /// <param name="source">
    /// What is disabling the worker. Only <see cref="WorkerDisableSource.Administrator"/>
    /// survives re-registration and the stale-worker sweep, so an automatic disabler must
    /// name itself rather than borrowing the administrator's privileges.
    /// </param>
    Task DisableWorkerAsync(Guid id, string reason, WorkerDisableSource source);

    /// <summary>Re-enables a disabled worker.</summary>
    /// <param name="id">The worker identifier.</param>
    Task EnableWorkerAsync(Guid id);

    /// <summary>
    /// Marks the worker paired with a slicer service offline and revokes its API key as part of
    /// that service deregistering, without disturbing an administrator's deliberate disable.
    /// </summary>
    /// <param name="serviceId">The paired <see cref="Worker.ServiceId"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when a paired worker existed and was revoked.</returns>
    /// <remarks>
    /// This is deliberately not a read-modify-write. An administrator can ban a worker in the
    /// window between a deregistration request loading the row and saving it; a read-modify-write
    /// would then write back the pre-ban state it had loaded, replacing the ban's attribution with
    /// <see cref="WorkerDisableSource.Deregistration"/> — and the next registration, seeing an
    /// automatic disable, would lift the ban. So the attribution is written by a conditional
    /// <c>UPDATE … WHERE DisableSource &lt;&gt; Administrator</c> evaluated by the database, which
    /// cannot observe a stale value. The offline/credential-revocation columns are applied
    /// unconditionally because they are correct for a banned worker too.
    /// </remarks>
    Task<bool> RevokeForDeregistrationAsync(string serviceId, CancellationToken ct = default);

    /// <summary>
    /// Lifts a disable that the system applied itself — deregistration or the circuit breaker —
    /// from the worker paired with a slicer service, leaving an administrator's ban untouched.
    /// </summary>
    /// <param name="serviceId">The paired <see cref="Worker.ServiceId"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when an automatic disable was actually lifted.</returns>
    /// <remarks>
    /// <para>
    /// This is the registration-side twin of <see cref="RevokeForDeregistrationAsync"/> and exists
    /// for the same reason. Deciding in memory whether a disable is automatic reads a snapshot
    /// taken when the row was loaded; an administrator can commit a ban after that read, and
    /// saving the tracked instance would then write <c>IsDisabled = false</c> straight over the
    /// ban — the worker would clear a sanction simply by registering with the right timing.
    /// Re-reading does not help, because EF returns the same stale tracked instance. So the test
    /// and the write happen together in one conditional <c>UPDATE</c> the database evaluates.
    /// </para>
    /// <para>
    /// Call this when the tracked <see cref="Worker"/> has no pending edits — before mutating it,
    /// or after the caller's save has succeeded. It bypasses the change tracker, so it refreshes
    /// unchanged tracked copies afterwards to keep any later edits layered on top of what was just
    /// committed rather than on a stale snapshot.
    /// </para>
    /// <para>
    /// It also commits on its own. A caller that re-enables a worker <i>before</i> persisting the
    /// registration that justifies it is fail-open: a circuit-breaker disable leaves
    /// <see cref="Worker.Status"/> Online, so a failure after the clear would return a worker the
    /// breaker had taken out of rotation straight to dispatch, with stale credentials. Clearing
    /// last keeps every failure direction disabled.
    /// </para>
    /// </remarks>
    Task<bool> ClearAutomaticDisableAsync(string serviceId, CancellationToken ct = default);

    /// <summary>Resets a worker's active job count to zero and sets status to Online.</summary>
    /// <param name="id">The worker identifier.</param>
    /// <returns>True if the worker was found and reset; false if not found.</returns>
    Task<bool> ResetAsync(Guid id);

    /// <summary>Deletes a worker.</summary>
    /// <param name="id">The worker identifier.</param>
    Task DeleteAsync(Guid id);

    /// <summary>
    /// Deletes a worker and its paired slicer service unless an administrator has disabled it.
    /// </summary>
    /// <param name="id">The worker identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when the worker was deleted.</returns>
    /// <remarks>
    /// <para>
    /// The stale-worker sweep selects its candidates from an <c>AsNoTracking</c> snapshot and then
    /// deletes them one at a time, so an administrator can ban a worker after it has been picked
    /// but before it is removed. An unconditional delete would erase that ban along with the row,
    /// and the worker could return, register as brand new and come back enabled — the sanction
    /// laundered by a background job. The exemption is therefore re-checked by the database in the
    /// same statement that performs the delete, rather than trusted from the snapshot.
    /// </para>
    /// <para>
    /// The worker and its paired service are removed inside one transaction. As two independent
    /// statements a failure between them would orphan the service permanently: the sweep
    /// enumerates workers, so a service with no worker is invisible to it and no later pass can
    /// collect it. When the caller already owns a transaction this enlists in it rather than
    /// opening a second.
    /// </para>
    /// </remarks>
    Task<bool> DeleteIfNotAdministrativelyDisabledAsync(Guid id, CancellationToken ct = default);

    /// <summary>Updates a worker's total processing slot count.</summary>
    /// <param name="id">The worker identifier.</param>
    /// <param name="totalSlots">The new total slot count.</param>
    Task UpdateTotalSlotsAsync(Guid id, int totalSlots);

    /// <summary>Saves pending changes to the database.</summary>
    Task SaveChangesAsync();
}
