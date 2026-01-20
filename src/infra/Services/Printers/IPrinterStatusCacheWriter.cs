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
}
