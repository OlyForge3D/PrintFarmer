namespace Farm.Infrastructure.Services.Cost;

/// <summary>
/// Abstraction for looking up filament cost data from an external source (e.g., Spoolman).
/// Implementations must degrade gracefully when the source is unavailable or unconfigured.
/// </summary>
public interface IFilamentCostProvider
{
    /// <summary>
    /// Returns the cost per gram for a specific physical spool.
    /// Price cascade: spool price / spool initial weight → filament price / filament weight.
    /// </summary>
    /// <param name="spoolId">The Spoolman spool ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Cost per gram in USD, or <c>null</c> if unavailable or Spoolman is unreachable.</returns>
    Task<decimal?> GetSpoolCostPerGramAsync(int spoolId, CancellationToken ct = default);

    /// <summary>
    /// Returns the cost per gram for a filament product definition.
    /// Uses filament price / filament weight.
    /// </summary>
    /// <param name="filamentId">The Spoolman filament product ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Cost per gram in USD, or <c>null</c> if unavailable or Spoolman is unreachable.</returns>
    Task<decimal?> GetFilamentCostPerGramAsync(int filamentId, CancellationToken ct = default);
}
