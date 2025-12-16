using System;
using System.Collections.Generic;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Web.Api.Services.Printers
{
    /// <summary>
    /// API convenience wrapper that exposes the Infrastructure cache with read/write and clear operations.
    /// This interface combines both read and write access plus API-specific clear operations.
    /// The real implementation is in Infrastructure.Services.Printers.PrinterStatusCache.
    /// </summary>
    public interface IPrinterStatusCache : IPrinterStatusCacheReader, IPrinterStatusCacheWriter
    {
        /// <summary>
        /// Clear cached status for a specific printer.
        /// </summary>
        void ClearStatus(Guid printerId);

        /// <summary>
        /// Clear all cached statuses.
        /// </summary>
        void ClearAllStatuses();
    }
}
