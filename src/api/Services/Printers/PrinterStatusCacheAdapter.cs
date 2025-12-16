using System;
using System.Collections.Generic;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Web.Api.Services.Printers
{
    /// <summary>
    /// Adapter that wraps the Infrastructure PrinterStatusCache and exposes it as the API IPrinterStatusCache interface.
    /// This allows the API layer to depend on its own interface while using the shared Infrastructure cache.
    /// </summary>
    public class PrinterStatusCacheAdapter : IPrinterStatusCache
    {
        private readonly Farm.Infrastructure.Services.Printers.PrinterStatusCache _cache;

        public PrinterStatusCacheAdapter(Farm.Infrastructure.Services.Printers.PrinterStatusCache cache)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        // Reader methods
        public PrinterStatusDto? GetStatus(Guid printerId) => _cache.GetStatus(printerId);

        public IReadOnlyDictionary<Guid, PrinterStatusDto> GetAllStatuses() => _cache.GetAllStatuses();

        // Writer methods
        public void UpdateStatus(PrinterStatusDto status) => _cache.UpdateStatus(status);

        public void UpdateStatuses(IEnumerable<PrinterStatusDto> statuses) => _cache.UpdateStatuses(statuses);

        // API-specific clear methods
        public void ClearStatus(Guid printerId) => _cache.ClearStatus(printerId);

        public void ClearAllStatuses() => _cache.ClearAllStatuses();
    }
}
