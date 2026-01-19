using System;
using System.Collections.Generic;
using Farm.Infrastructure;
using Farm.Infrastructure.Telemetry;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Receives printer status updates and stores them in the cache.
/// This service is called by backend polling services to update the shared status cache.
/// Backend plugins use this to update the cache without direct API layer dependencies.
/// </summary>
public interface IPrinterStatusUpdateReceiver
{
    /// <summary>
    /// Called when a printer status update is received from a backend service.
    /// </summary>
    /// <param name="status">The printer status data received from the backend.</param>
    void ReceiveStatusUpdate(PrinterStatusDto status);

    /// <summary>
    /// Called when multiple printer statuses are updated at once.
    /// </summary>
    /// <param name="statuses">The collection of printer status updates.</param>
    void ReceiveStatusUpdates(IEnumerable<PrinterStatusDto> statuses);
}

/// <summary>
/// Receives printer status updates from backend services and stores them in the shared cache.
/// Allows backend plugins to update the cache without direct dependencies on API services.
/// </summary>
public class PrinterStatusUpdateReceiver(IPrinterStatusCacheWriter cache, IUnifiedLoggingService logger) : IPrinterStatusUpdateReceiver
{
    private readonly IPrinterStatusCacheWriter _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    private readonly IUnifiedLoggingService _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public void ReceiveStatusUpdate(PrinterStatusDto status)
    {
        if (status == null)
        {
            return;
        }

        _cache.UpdateStatus(status);
        _logger.LogDebug($"[StatusCache] Updated printer {status.Id}: IsOnline={status.IsOnline}, State={status.State}");
    }

    public void ReceiveStatusUpdates(IEnumerable<PrinterStatusDto> statuses)
    {
        if (statuses == null)
        {
            return;
        }

        foreach (PrinterStatusDto status in statuses)
        {
            _cache.UpdateStatus(status);
        }

        _logger.LogDebug($"[StatusCache] Updated multiple printer statuses");
    }
}
