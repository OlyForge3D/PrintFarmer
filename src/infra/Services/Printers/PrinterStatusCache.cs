using System;
using System.Collections.Generic;
using System.IO;
using Farm.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Shared interface for reading printer status cache.
/// Used by API layer to retrieve cached status without external API calls.
/// </summary>
public interface IPrinterStatusCacheReader
{
    /// <summary>
    /// Get the cached status for a specific printer, or null if not cached.
    /// </summary>
    PrinterStatusDto? GetStatus(Guid printerId);

    /// <summary>
    /// Get all cached printer statuses.
    /// </summary>
    IReadOnlyDictionary<Guid, PrinterStatusDto> GetAllStatuses();
}

/// <summary>
/// Thread-safe in-memory cache for printer status updates from SignalR.
/// Stores the latest status for each printer to enable fast list operations without external API calls.
/// This cache is shared between:
/// - Backend services (MoonrakerSubscriptionService, PrusaLinkPollingService) - write updates
/// - API layer (PrintersService) - read cached data for list endpoints
/// </summary>
public class PrinterStatusCache : IPrinterStatusCacheReader, IPrinterStatusCacheWriter
{
    private readonly Dictionary<Guid, PrinterStatusDto> _cache = new();
    private readonly Lock _lockObj = new();
    private readonly ILogger<PrinterStatusCache> _logger;

    public PrinterStatusCache(ILogger<PrinterStatusCache> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public PrinterStatusDto? GetStatus(Guid printerId)
    {
        lock (_lockObj)
        {
            _cache.TryGetValue(printerId, out PrinterStatusDto? status);
            return status;
        }
    }

    public IReadOnlyDictionary<Guid, PrinterStatusDto> GetAllStatuses()
    {
        lock (_lockObj)
        {
            return new Dictionary<Guid, PrinterStatusDto>(_cache);
        }
    }

    public void UpdateStatus(PrinterStatusDto status)
    {
        lock (_lockObj)
        {
            string? previousState = null;
            bool previousOnline = false;
            if (_cache.TryGetValue(status.Id, out PrinterStatusDto? existing))
            {
                previousState = existing.State;
                previousOnline = existing.IsOnline;
            }

            _cache[status.Id] = status.WithNormalizedFileName();

            bool stateChanged = !string.Equals(previousState, status.State, StringComparison.OrdinalIgnoreCase);
            bool onlineChanged = previousOnline != status.IsOnline;

            if (stateChanged || onlineChanged)
            {
                _logger.LogWarning(
                    "[PrinterStateTransition] Printer {PrinterId}: State '{PreviousState}' -> '{NewState}', Online {PreviousOnline} -> {NewOnline}",
                    status.Id,
                    previousState ?? "(none)",
                    status.State ?? "(none)",
                    previousOnline,
                    status.IsOnline);
            }
        }
    }

    public void UpdateStatuses(IEnumerable<PrinterStatusDto> statuses)
    {
        if (statuses == null)
        {
            return;
        }

        lock (_lockObj)
        {
            foreach (PrinterStatusDto status in statuses)
            {
                string? previousState = null;
                bool previousOnline = false;
                if (_cache.TryGetValue(status.Id, out PrinterStatusDto? existing))
                {
                    previousState = existing.State;
                    previousOnline = existing.IsOnline;
                }

                _cache[status.Id] = status.WithNormalizedFileName();

                bool stateChanged = !string.Equals(previousState, status.State, StringComparison.OrdinalIgnoreCase);
                bool onlineChanged = previousOnline != status.IsOnline;

                if (stateChanged || onlineChanged)
                {
                    _logger.LogWarning(
                        "[PrinterStateTransition] Printer {PrinterId}: State '{PreviousState}' -> '{NewState}', Online {PreviousOnline} -> {NewOnline}",
                        status.Id,
                        previousState ?? "(none)",
                        status.State ?? "(none)",
                        previousOnline,
                        status.IsOnline);
                }
            }
        }
    }

    public PrinterStatusDto UpdateSpoolInfo(Guid printerId, PrinterSpoolInfoDto? spoolInfo)
    {
        lock (_lockObj)
        {
            _cache.TryGetValue(printerId, out PrinterStatusDto? existing);
            PrinterStatusDto updated = (existing ?? new PrinterStatusDto(Id: printerId, IsOnline: false, State: "Unknown"))
                with
            { SpoolInfo = spoolInfo };
            _cache[printerId] = updated;
            return updated;
        }
    }

    public void ClearStatus(Guid printerId)
    {
        lock (_lockObj)
        {
            _cache.Remove(printerId);
        }
    }

    public void ClearAllStatuses()
    {
        lock (_lockObj)
        {
            _cache.Clear();
        }
    }
}
