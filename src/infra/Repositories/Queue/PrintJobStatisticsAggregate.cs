namespace Farm.Infrastructure.Repositories.Queue;

/// <summary>
/// Aggregate job-count and summed-duration statistics for a single printer model, produced by a
/// single grouped SQL query (<see cref="IPrintJobStatisticsRepository.GetAggregateByPrinterModelAsync"/>)
/// instead of materializing every matching <c>PrintJobStatistics</c> row (issue #2329).
/// </summary>
public sealed class PrintJobStatisticsAggregate
{
    /// <summary>Total number of jobs matching the query filters.</summary>
    public int JobCount { get; init; }

    /// <summary>
    /// Summed <c>ActualDurationMs</c> across all matching jobs that have a recorded duration.
    /// Jobs with a null duration are excluded from the sum, matching the prior in-memory
    /// computation's <c>.Where(j =&gt; j.ActualDurationMs.HasValue)</c> filter.
    /// </summary>
    public long TotalDurationMs { get; init; }

    /// <summary>Convenience conversion of <see cref="TotalDurationMs"/> to hours.</summary>
    public double TotalDurationHours => TotalDurationMs / 1000.0 / 3600.0;
}
