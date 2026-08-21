using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Default singleton implementation of <see cref="IPrinterCacheInvalidator"/>. Holds no state
/// beyond the subscriber list itself - it is a pure fan-out mechanism between the API layer
/// (which edits printers) and the backend polling services (which cache them).
/// </summary>
public sealed class PrinterCacheInvalidator(ILogger<PrinterCacheInvalidator> logger) : IPrinterCacheInvalidator
{
    private readonly ILogger<PrinterCacheInvalidator> _logger = logger;
    private readonly List<Action<Guid>> _subscribers = [];
    private readonly object _gate = new();

    /// <inheritdoc />
    public void Subscribe(Action<Guid> onInvalidated)
    {
        ArgumentNullException.ThrowIfNull(onInvalidated);
        lock (_gate)
        {
            _subscribers.Add(onInvalidated);
        }
    }

    /// <inheritdoc />
    public void Unsubscribe(Action<Guid> onInvalidated)
    {
        ArgumentNullException.ThrowIfNull(onInvalidated);
        lock (_gate)
        {
            _subscribers.Remove(onInvalidated);
        }
    }

    /// <inheritdoc />
    public void Invalidate(Guid printerId)
    {
        Action<Guid>[] snapshot;
        lock (_gate)
        {
            if (_subscribers.Count == 0)
            {
                return;
            }

            snapshot = [.. _subscribers];
        }

        // A subscriber throwing must never break the caller's edit flow (the row is already
        // durably saved by this point); log and continue notifying the rest.
        foreach (Action<Guid> handler in snapshot)
        {
            try
            {
                handler(printerId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Printer cache invalidation subscriber threw for printer {PrinterId}", printerId);
            }
        }
    }
}
