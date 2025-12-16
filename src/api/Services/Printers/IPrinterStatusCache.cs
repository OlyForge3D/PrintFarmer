using System;
using System.Collections.Generic;
using Farm.Infrastructure;

namespace Farm.Web.Api.Services.Printers
{
    /// <summary>
    /// Cache for storing the latest printer status updates from SignalR.
    /// Allows quick retrieval of printer status without making external API calls.
    /// </summary>
    public interface IPrinterStatusCache
    {
        /// <summary>
        /// Get the cached status for a specific printer, or null if no status is cached yet.
        /// </summary>
        PrinterStatusDto? GetStatus(Guid printerId);

        /// <summary>
        /// Get all cached printer statuses.
        /// </summary>
        IReadOnlyDictionary<Guid, PrinterStatusDto> GetAllStatuses();

        /// <summary>
        /// Update the cached status for a printer (typically called by SignalR hub).
        /// </summary>
        void UpdateStatus(PrinterStatusDto status);

        /// <summary>
        /// Update multiple printer statuses at once.
        /// </summary>
        void UpdateStatuses(IEnumerable<PrinterStatusDto> statuses);

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
