namespace Farm.Infrastructure.Repositories.Maintenance;

/// <summary>
/// Immutable description of how a block of print-hours is distributed across the physical toolheads
/// that produced it (issue #711, round-7 Finding 3).
///
/// <para>
/// Before this type, external-history print-hour deltas were split equally across every physical
/// toolhead, which silently advanced the wear of idle toolheads (for example T1 while only T0 was
/// printing) and under-credited the toolhead that actually did the work. Attributions are now built
/// only from real per-tool telemetry (per-tool consumption or G-code tool activity) via
/// <c>FromWeights</c>. When no such telemetry exists the caller leaves the delta unattributed rather
/// than fabricating an equal split (issue #711, round-10 Finding 1).
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
    /// <c>true</c> when the distribution is an estimate rather than derived from real per-tool
    /// consumption. Retained for telemetry/back-compat; every attribution built today is derived
    /// from real weights via <see cref="FromWeights(IReadOnlyDictionary{Guid, double})"/> and is
    /// therefore non-approximated (issue #711, round-10 Finding 1 removed the equal-split path).
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
}
