namespace Farm.Infrastructure.Services.Cost;

/// <summary>
/// Service for calculating detailed cost breakdowns for print jobs.
/// </summary>
public interface IJobCostCalculationService
{
    /// <summary>
    /// Calculates all cost components for a completed print job and updates the job record.
    /// </summary>
    /// <param name="jobId">The print job to calculate costs for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if costs were calculated and saved; false if calculation was skipped or failed.</returns>
    Task<bool> CalculateAndStoreCostsAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Recalculates costs for a job with manual overrides.
    /// </summary>
    /// <param name="jobId">The print job to recalculate.</param>
    /// <param name="materialCost">Manual material cost override (or null to auto-calculate).</param>
    /// <param name="energyCost">Manual energy cost override (or null to auto-calculate).</param>
    /// <param name="machineTimeCost">Manual machine time cost override (or null to auto-calculate).</param>
    /// <param name="laborCost">Manual labor cost override (or null to auto-calculate).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if costs were recalculated and saved; false otherwise.</returns>
    Task<bool> RecalculateCostsWithOverridesAsync(
        Guid jobId,
        decimal? materialCost = null,
        decimal? energyCost = null,
        decimal? machineTimeCost = null,
        decimal? laborCost = null,
        CancellationToken ct = default);

    /// <summary>
    /// Recalculates costs for all completed jobs that are missing cost data.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of jobs that were successfully recalculated.</returns>
    Task<int> RecalculateAllAsync(CancellationToken ct = default);
}
