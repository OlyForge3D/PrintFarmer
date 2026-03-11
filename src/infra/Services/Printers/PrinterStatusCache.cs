using System;
using System.Collections.Generic;
using System.IO;
using Farm.Infrastructure;

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
            _cache[status.Id] = StripJobNamePath(status);
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
                _cache[status.Id] = StripJobNamePath(status);
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

    /// <summary>
    /// Strip directory path prefixes from JobName (e.g. ".cache/file.gcode" → "file.gcode").
    /// </summary>
    private static PrinterStatusDto StripJobNamePath(PrinterStatusDto status)
    {
        if (string.IsNullOrEmpty(status.JobName))
        {
            return status;
        }

        string stripped = Path.GetFileName(status.JobName);
        return stripped == status.JobName ? status : status with { JobName = stripped };
    }
}
