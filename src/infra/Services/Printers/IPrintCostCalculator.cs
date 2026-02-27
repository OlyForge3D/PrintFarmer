namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Calculates print job costs based on Spoolman spool price and filament usage.
/// </summary>
public interface IPrintCostCalculator
{
    /// <summary>
    /// Calculates the estimated cost of a print job from spool price and estimated filament usage.
    /// </summary>
    /// <param name="spoolmanFilamentId">The Spoolman filament ID (for price lookup).</param>
    /// <param name="estimatedFilamentUsageGrams">Estimated filament usage in grams.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Estimated cost, or null if price data is unavailable.</returns>
    Task<decimal?> CalculateEstimatedCostAsync(int? spoolmanFilamentId, double? estimatedFilamentUsageGrams, CancellationToken ct = default);

    /// <summary>
    /// Calculates the actual cost of a print job from spool price and actual filament usage.
    /// Falls back to estimated usage if actual is not available.
    /// </summary>
    /// <param name="spoolmanFilamentId">The Spoolman filament ID (for price lookup).</param>
    /// <param name="actualFilamentUsageGrams">Actual filament usage in grams (nullable, falls back to estimated).</param>
    /// <param name="estimatedFilamentUsageGrams">Estimated filament usage in grams (fallback).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Actual cost, or null if price data is unavailable.</returns>
    Task<decimal?> CalculateActualCostAsync(int? spoolmanFilamentId, double? actualFilamentUsageGrams, double? estimatedFilamentUsageGrams, CancellationToken ct = default);
}
