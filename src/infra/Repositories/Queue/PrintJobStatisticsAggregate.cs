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

    /// <summary>
    /// Total duration in hours, summed row-by-row (each row converted from milliseconds to hours
    /// before summing) rather than derived from <see cref="TotalDurationMs"/> in one division.
    /// This mirrors the exact summation order of the prior in-memory computation
    /// (<c>printerJobs.Sum(j =&gt; j.ActualDurationMs!.Value / 1000.0 / 3600.0)</c>) instead of
    /// <c>TotalDurationMs / 1000.0 / 3600.0</c>, which can differ in the least-significant bits
    /// due to floating-point summation order - see issue #2329's "identical aggregate values"
    /// correctness requirement.
    /// </summary>
    public double TotalDurationHours { get; init; }
}
