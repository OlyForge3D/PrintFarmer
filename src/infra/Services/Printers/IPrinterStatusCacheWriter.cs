using System;
using System.Collections.Generic;
using Farm.Infrastructure;

namespace Farm.Infrastructure.Services.Printers
{
    /// <summary>
    /// Shared interface for updating printer status cache.
    /// Backend services can use this to store latest status updates in the cache.
    /// </summary>
    public interface IPrinterStatusCacheWriter
    {
        /// <summary>
        /// Update the cached status for a printer.
        /// Called by backend polling services after receiving status updates.
        /// </summary>
        void UpdateStatus(PrinterStatusDto status);

        /// <summary>
        /// Update multiple printer statuses at once.
        /// </summary>
        void UpdateStatuses(IEnumerable<PrinterStatusDto> statuses);
    }
}
