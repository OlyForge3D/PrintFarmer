using System;
using System.Collections.Generic;
using System.Linq;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Web.Api.Services.Printers
{
    /// <summary>
    /// Thread-safe in-memory cache for printer status updates from SignalR.
    /// Stores the latest status for each printer to enable fast list operations without external API calls.
    /// </summary>
    public class PrinterStatusCache : IPrinterStatusCache, IPrinterStatusCacheWriter
    {
        private readonly Dictionary<Guid, PrinterStatusDto> _cache = new();
        private readonly object _lockObj = new();

        public PrinterStatusDto? GetStatus(Guid printerId)
        {
            lock (_lockObj)
            {
                _cache.TryGetValue(printerId, out var status);
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
                _cache[status.Id] = status;
            }
        }

        public void UpdateStatuses(IEnumerable<PrinterStatusDto> statuses)
        {
            if (statuses == null)
                return;

            lock (_lockObj)
            {
                foreach (var status in statuses)
                {
                    _cache[status.Id] = status;
                }
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
}
