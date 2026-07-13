namespace Farm.Infrastructure.Repositories.Maintenance;

/// <summary>
/// Immutable description of how a block of print-hours is distributed across the physical toolheads
/// that produced it (issue #711, round-7 Finding 3).
///
/// <para>
/// Before this type, external-history print-hour deltas were split equally across every physical
/// toolhead, which silently advanced the wear of idle toolheads (for example T1 while only T0 was
/// printing) and under-credited the toolhead that actually did the work. Callers that DO have
/// per-tool consumption telemetry can now build a weighted attribution via <c>FromWeights</c>;
/// callers that do not fall back to <see cref="EqualSplit"/>, which flags the result as
/// <see cref="IsApproximated"/> so the sync pipeline can emit an operator-visible diagnostic.
/// </para>
/// </summary>
public sealed class ToolheadHourAttribution
{
    private ToolheadHourAttribution(
        IReadOnlyDictionary<Guid, double> hours,
        IReadOnlyDictionary<Guid, double> weights,
        double sourceHours,
        bool isApproximated)
    {
        Hours = hours;
        Weights = weights;
        SourceHours = sourceHours;
        IsApproximated = isApproximated;
    }

    /// <summary>
    /// Toolhead ID → print-hours to credit to that toolhead. Only positive entries are applied by
    /// <see cref="IToolheadStatisticsRepository.ApplyToolheadHoursAsync"/>.
    /// </summary>
    public IReadOnlyDictionary<Guid, double> Hours { get; }

    /// <summary>
    /// Toolhead ID → fraction of <see cref="SourceHours"/> attributed to that toolhead. The sum
    /// may be less than one when telemetry identifies only part of the work; the unknown residual
    /// is intentionally left uncredited rather than assigned to an idle head.
    /// </summary>
    public IReadOnlyDictionary<Guid, double> Weights { get; }

    /// <summary>
    /// Total printer hours from which <see cref="Weights"/> were derived.
    /// </summary>
    public double SourceHours { get; }

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
        double sourceHours = hours.Values.Where(h => h > 0).Sum();
        IReadOnlyDictionary<Guid, double> weights = sourceHours > 0
            ? hours.ToDictionary(kvp => kvp.Key, kvp => Math.Max(0, kvp.Value) / sourceHours)
            : new Dictionary<Guid, double>();
        return new ToolheadHourAttribution(hours, weights, sourceHours, isApproximated: false);
    }

    /// <summary>
    /// Builds an attribution from normalized per-tool fractions of
    /// <paramref name="sourceHours"/>. Each weight must be between zero and one and their sum must
    /// not exceed one. A sum below one leaves the unknown residual uncredited.
    /// </summary>
    public static ToolheadHourAttribution FromWeights(
        IReadOnlyDictionary<Guid, double> weights,
        double sourceHours)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceHours);

        if (weights.Values.Any(weight => weight is < 0 or > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(weights), "Attribution weights must be between zero and one.");
        }

        double totalWeight = weights.Values.Sum();
        if (totalWeight > 1.0 + 1e-9)
        {
            throw new ArgumentException("Attribution weights must not sum to more than one.", nameof(weights));
        }

        Dictionary<Guid, double> hours = weights.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value * sourceHours);
        return new ToolheadHourAttribution(hours, weights, sourceHours, isApproximated: false);
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
            return new ToolheadHourAttribution(
                new Dictionary<Guid, double>(),
                new Dictionary<Guid, double>(),
                Math.Max(0, totalHours),
                isApproximated: true);
        }

        double perToolhead = totalHours / toolheadIds.Count;
        double perToolheadWeight = 1.0 / toolheadIds.Count;
        Dictionary<Guid, double> hours = new(toolheadIds.Count);
        Dictionary<Guid, double> weights = new(toolheadIds.Count);
        foreach (Guid id in toolheadIds)
        {
            hours[id] = perToolhead;
            weights[id] = perToolheadWeight;
        }

        return new ToolheadHourAttribution(hours, weights, totalHours, isApproximated: true);
    }
}
