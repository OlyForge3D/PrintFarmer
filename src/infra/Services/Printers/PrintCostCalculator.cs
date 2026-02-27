using Farm.Infrastructure.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Calculates print job costs from Spoolman spool price and filament weight.
/// Formula: cost = (usageGrams / spoolWeightGrams) * spoolPrice
/// </summary>
public class PrintCostCalculator : IPrintCostCalculator
{
    private readonly ISpoolmanService _spoolmanService;
    private readonly ILogger<PrintCostCalculator> _logger;

    public PrintCostCalculator(ISpoolmanService spoolmanService, ILogger<PrintCostCalculator> logger)
    {
        _spoolmanService = spoolmanService ?? throw new ArgumentNullException(nameof(spoolmanService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<decimal?> CalculateEstimatedCostAsync(int? spoolmanFilamentId, double? estimatedFilamentUsageGrams, CancellationToken ct = default)
    {
        return await CalculateCostAsync(spoolmanFilamentId, estimatedFilamentUsageGrams, ct);
    }

    /// <inheritdoc />
    public async Task<decimal?> CalculateActualCostAsync(int? spoolmanFilamentId, double? actualFilamentUsageGrams, double? estimatedFilamentUsageGrams, CancellationToken ct = default)
    {
        double? usageGrams = actualFilamentUsageGrams ?? estimatedFilamentUsageGrams;
        return await CalculateCostAsync(spoolmanFilamentId, usageGrams, ct);
    }

    private async Task<decimal?> CalculateCostAsync(int? spoolmanFilamentId, double? usageGrams, CancellationToken ct)
    {
        if (!spoolmanFilamentId.HasValue || !usageGrams.HasValue || usageGrams.Value <= 0)
        {
            return null;
        }

        try
        {
            SpoolmanFilamentDto? filament = await _spoolmanService.GetFilamentByIdAsync(spoolmanFilamentId.Value, ct);

            if (filament == null)
            {
                _logger.LogDebug("Spoolman filament {FilamentId} not found, cannot calculate cost", spoolmanFilamentId.Value);
                return null;
            }

            if (!filament.Price.HasValue || filament.Price.Value <= 0)
            {
                _logger.LogDebug("Spoolman filament {FilamentId} has no price, cannot calculate cost", spoolmanFilamentId.Value);
                return null;
            }

            // Get the spool weight (total filament weight per spool)
            double spoolWeightGrams = filament.Weight ?? 1000.0; // Default to 1kg if not set
            if (spoolWeightGrams <= 0)
            {
                spoolWeightGrams = 1000.0;
            }

            // cost = (usageGrams / spoolWeightGrams) * price
            decimal cost = (decimal)(usageGrams.Value / spoolWeightGrams) * (decimal)filament.Price.Value;

            // Round to 2 decimal places
            cost = Math.Round(cost, 2);

            _logger.LogTrace(
                "Calculated cost for filament {FilamentId}: {Usage:F1}g / {SpoolWeight:F0}g * {Price:F2} = {Cost:F2}",
                spoolmanFilamentId.Value, usageGrams.Value, spoolWeightGrams, filament.Price.Value, cost);

            return cost;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to calculate cost for filament {FilamentId}", spoolmanFilamentId.Value);
            return null;
        }
    }
}
