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
    /// <param name="originWatermark">Committed watermark captured before observing the status.</param>
    void UpdateStatus(PrinterStatusDto status, long? originWatermark = null);

    /// <summary>
    /// Update multiple printer statuses at once.
    /// </summary>
    /// <param name="statuses">The collection of printer status data to cache.</param>
    /// <param name="originWatermark">Committed watermark captured before observing the batch.</param>
    void UpdateStatuses(IEnumerable<PrinterStatusDto> statuses, long? originWatermark = null);

    /// <summary>
    /// Atomically update only the SpoolInfo field for a cached printer status.
    /// If no cached status exists, creates a minimal entry with the spool info.
    /// </summary>
    /// <param name="printerId">The printer whose spool info to update.</param>
    /// <param name="spoolInfo">The new spool info, or null to clear it.</param>
    /// <returns>The updated status DTO (for broadcasting).</returns>
    PrinterStatusDto UpdateSpoolInfo(Guid printerId, PrinterSpoolInfoDto? spoolInfo);

    /// <summary>
    /// Atomically updates the backend-neutral sensor list of a cached printer status.
    /// Sensors are bounded and deduplicated by <see cref="PrinterSensorNormalizer"/>.
    /// </summary>
    /// <param name="printerId">The printer whose sensors changed.</param>
    /// <param name="sensors">A complete snapshot when <paramref name="replaceAll"/> is true; otherwise a
    /// partial update whose omitted sensors and readings are preserved.</param>
    /// <param name="replaceAll">Whether <paramref name="sensors"/> replaces the whole list.</param>
    /// <returns>The updated status, or null when no status is cached or the sensor content is unchanged.</returns>
    PrinterStatusDto? UpdateSensors(Guid printerId, IEnumerable<PrinterSensorDto> sensors, bool replaceAll) => null;

    /// <summary>Invalidates runtime factor observations without refreshing unrelated status evidence.</summary>
    PrinterStatusDto? ClearRuntimeFactors(Guid printerId);
}
