using System;
using System.Collections.Generic;
using Farm.Infrastructure;

namespace Farm.Infrastructure.Services.Printers;

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
    /// <param name="status">The printer status data to cache.</param>
    void UpdateStatus(PrinterStatusDto status);

    /// <summary>
    /// Update multiple printer statuses at once.
    /// </summary>
    /// <param name="statuses">The collection of printer status data to cache.</param>
    void UpdateStatuses(IEnumerable<PrinterStatusDto> statuses);

    /// <summary>
    /// Atomically update only the SpoolInfo field for a cached printer status.
    /// If no cached status exists, creates a minimal entry with the spool info.
    /// </summary>
    /// <param name="printerId">The printer whose spool info to update.</param>
    /// <param name="spoolInfo">The new spool info, or null to clear it.</param>
    /// <returns>The updated status DTO (for broadcasting).</returns>
    PrinterStatusDto UpdateSpoolInfo(Guid printerId, PrinterSpoolInfoDto? spoolInfo);
}
