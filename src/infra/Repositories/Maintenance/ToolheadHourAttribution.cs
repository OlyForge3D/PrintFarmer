namespace Farm.Infrastructure.Repositories.Maintenance;

/// <summary>
/// Immutable description of how a block of print-hours is distributed across the physical toolheads
/// that produced it (issue #711, round-7 Finding 3).
///
/// <para>
/// Before this type, external-history print-hour deltas were split equally across every physical
/// toolhead, which silently advanced the wear of idle toolheads (for example T1 while only T0 was
/// printing) and under-credited the toolhead that actually did the work. Callers that DO have
/// per-tool consumption telemetry can now build a weighted attribution via <see cref="FromWeights"/>;
/// callers that do not fall back to <see cref="EqualSplit"/>, which flags the result as
/// <see cref="IsApproximated"/> so the sync pipeline can emit an operator-visible diagnostic.
/// </para>
/// </summary>
public sealed class ToolheadHourAttribution
{
    private ToolheadHourAttribution(IReadOnlyDictionary<Guid, double> hours, bool isApproximated)
    {
        Hours = hours;
        IsApproximated = isApproximated;
    }

    /// <summary>
    /// Toolhead ID → print-hours to credit to that toolhead. Only positive entries are applied by
    /// <see cref="IToolheadStatisticsRepository.ApplyToolheadHoursAsync"/>.
    /// </summary>
    public IReadOnlyDictionary<Guid, double> Hours { get; }

    /// <summary>
    /// <c>true</c> when the distribution is an equal-split estimate rather than derived from
    /// real per-tool consumption. Approximated attributions should be logged so operators know the
    /// per-toolhead wear for the covered job is only an estimate.
    /// </summary>
    public bool IsApproximated { get; }

    /// <summary>
    /// Total hours represented by this attribution (sum of the positive per-toolhead weights).
    /// </summary>
    public double TotalHours => Hours.Values.Where(h => h > 0).Sum();

    /// <summary>
    /// Builds an attribution from explicit per-tool weights derived from real telemetry (per-tool
    /// filament consumption or G-code tool activity). Marked non-approximated.
    /// </summary>
    public static ToolheadHourAttribution FromWeights(IReadOnlyDictionary<Guid, double> hours)
    {
        ArgumentNullException.ThrowIfNull(hours);
        return new ToolheadHourAttribution(hours, isApproximated: false);
    }

    /// <summary>
    /// Distributes <paramref name="totalHours"/> equally across <paramref name="toolheadIds"/>. This
    /// is the conservative fallback used when no per-tool consumption telemetry is available; the
    /// result is flagged <see cref="IsApproximated"/>. Returns an empty attribution when there are no
    /// toolheads or the total is not positive.
    /// </summary>
    public static ToolheadHourAttribution EqualSplit(IReadOnlyCollection<Guid> toolheadIds, double totalHours)
    {
        ArgumentNullException.ThrowIfNull(toolheadIds);

        if (toolheadIds.Count == 0 || totalHours <= 0)
        {
            return new ToolheadHourAttribution(new Dictionary<Guid, double>(), isApproximated: true);
        }

        double perToolhead = totalHours / toolheadIds.Count;
        Dictionary<Guid, double> hours = new(toolheadIds.Count);
        foreach (Guid id in toolheadIds)
        {
            hours[id] = perToolhead;
        }

        return new ToolheadHourAttribution(hours, isApproximated: true);
    }
}
