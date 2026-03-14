namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Allows callers to trigger an immediate status refresh for a printer,
/// bypassing the normal subscription/polling cycle. Used after dispatch
/// to ensure the UI sees the new state without waiting for the next poll.
/// </summary>
public interface IPrinterStatusRefreshService
{
    /// <summary>
    /// Queries the printer's current status via HTTP and broadcasts it via SignalR.
    /// Implementations should be safe to call fire-and-forget.
    /// </summary>
    /// <param name="printerId">The printer to refresh.</param>
    /// <param name="delayMs">Optional delay before querying (gives the printer firmware time to transition state).</param>
    /// <param name="ct">Cancellation token.</param>
    Task RefreshPrinterStatusAsync(Guid printerId, int delayMs = 750, CancellationToken ct = default);
}
