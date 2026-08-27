namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Notifies queue-reader clients that their authorized SignalR subscription resource
/// snapshot (printers/jobs/projects they may subscribe to) may have changed, so they
/// should reconcile their subscriptions.
///
/// This is deliberately separate from <see cref="QueueOutboxPublisherService"/>: that
/// service only ever carries job/dispatch/bed-clear lifecycle events, none of which can
/// change subscription membership. Membership changes (printer create/delete/reassign,
/// printer-group membership, user role changes) call this notifier directly and
/// synchronously at the point of mutation instead (issue #1731) -- both more precise
/// (no false positives on every queue event) and lower latency than the outbox's poll
/// interval.
/// </summary>
public interface IQueueSubscriptionMembershipNotifier
{
    /// <summary>
    /// Broadcasts the payload-free "queueresourceschanged" discovery hint to all
    /// authorized queue-reader clients. Clients react by re-fetching their authorized
    /// resource snapshot via REST and reconciling their SignalR subscriptions.
    /// </summary>
    Task NotifyMembershipChangedAsync(CancellationToken ct = default);
}
