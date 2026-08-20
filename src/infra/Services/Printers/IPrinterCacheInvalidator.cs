using System;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Singleton pub/sub hook that lets code which edits a <see cref="Printer"/> row (server URL,
/// credentials, backend, etc. - see <c>PrintersController.UpdateAsync</c>) tell every backend
/// polling service to drop any cached copy of that row, forcing a fresh database read (with
/// decrypted credentials) on the very next poll tick instead of waiting for the next periodic
/// reconciliation pass (up to 30 seconds later).
/// </summary>
/// <remarks>
/// This exists because the polling services (PrusaLink, SDCP, FlashForge, OctoPrint) now cache
/// the <see cref="Printer"/> row on their per-printer polling state and only refresh it from the
/// 30-second reconciliation tick that already loads and decrypts full printer rows for the
/// backend (see issue #1763). Without an explicit invalidation hook, a printer whose URL or
/// credentials were just edited would keep being polled with stale values for up to 30 seconds,
/// which is a functional regression rather than a pure perf trade-off. Subscribe/Unsubscribe
/// methods are used instead of a public event to keep the callback plainly an
/// <see cref="Action{T}"/> without tripping CA1003 (events are conventionally expected to use
/// <c>EventHandler&lt;T&gt;</c>, which would require a meaningless <c>EventArgs</c> wrapper here).
/// </remarks>
public interface IPrinterCacheInvalidator
{
    /// <summary>
    /// Registers a callback to be invoked with the affected printer's Id whenever its persisted
    /// row may have changed. Backend polling services subscribe to drop their cached
    /// <see cref="Printer"/> snapshot so the next poll tick re-reads the row from the database.
    /// </summary>
    /// <param name="onInvalidated">Callback to invoke on invalidation.</param>
    void Subscribe(Action<Guid> onInvalidated);

    /// <summary>
    /// Unregisters a callback previously passed to <see cref="Subscribe"/>. Safe to call with a
    /// callback that was never subscribed (no-op).
    /// </summary>
    /// <param name="onInvalidated">The callback to remove.</param>
    void Unsubscribe(Action<Guid> onInvalidated);

    /// <summary>
    /// Notifies subscribers that the given printer's cached row is stale and must be refreshed.
    /// Safe to call even when no subscribers are currently listening.
    /// </summary>
    /// <param name="printerId">The printer whose cached row should be invalidated.</param>
    void Invalidate(Guid printerId);
}
