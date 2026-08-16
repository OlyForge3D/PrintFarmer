using Farm.Infrastructure;

namespace Farm.Infrastructure.Services.Printers;

public interface IPrinterVersionCache
{
    /// <summary>
    /// Retrieves version/firmware information for a printer, normally served from cache.
    /// </summary>
    /// <param name="printerId">The printer to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="forceRefresh">
    /// When <c>true</c>, bypasses any cached result (including a cached partial/faulted
    /// result) and queries the backend live, re-caching the fresh result under the normal
    /// cache policy. Automatic polling must leave this <c>false</c> so it keeps the normal
    /// cache policy; only an explicit operator-initiated refresh should set it to <c>true</c>.
    /// </param>
    Task<PrinterVersionInfoDto?> GetAsync(Guid printerId, CancellationToken ct, bool forceRefresh = false);
}
