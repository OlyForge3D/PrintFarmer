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
    /// Total duration in hours, using the SAME per-row-divide-then-sum FORMULA as the prior
    /// in-memory computation (<c>printerJobs.Sum(j =&gt; j.ActualDurationMs!.Value / 1000.0 /
    /// 3600.0)</c>) rather than deriving it from <see cref="TotalDurationMs"/> in one division
    /// (<c>TotalDurationMs / 1000.0 / 3600.0</c>), which measurably diverges from the old value
    /// for non-uniform datasets. This is NOT guaranteed to be bit-for-bit identical to the old
    /// value: the underlying SQL engine's row-iteration order for a <c>SUM</c> aggregate is
    /// engine-defined and not provably the same as the old code's
    /// <c>OrderByDescending(s =&gt; s.CompletedAtUtc)</c> list order, and floating-point addition
    /// is not associative, so different summation orders can differ in the least-significant
    /// bits. Bit-for-bit identity is not achievable by any grouped-SQL rewrite short of
    /// re-materializing every row and re-aggregating in memory, which would defeat the purpose of
    /// issue #2329's fix. <c>EfPrintJobStatisticsRepositoryAggregateTests</c> and
    /// <c>PrintStatsSyncModelBatchAggregateTests</c> (specifically
    /// <c>GetAggregateByPrinterModelAsync_MixedDurations_MatchesOldPerRowSummationWithinFloatingPointEpsilon</c>)
    /// empirically bound this divergence, for adversarial non-uniform duration values, to well
    /// under 1e-9 hours (~3.6 microseconds) - the defined, tested tolerance for issue #2329's
    /// "identical aggregate values" correctness requirement, several orders of magnitude below any
    /// print-duration measurement this system records.
    /// </summary>
    public double TotalDurationHours { get; init; }
}
