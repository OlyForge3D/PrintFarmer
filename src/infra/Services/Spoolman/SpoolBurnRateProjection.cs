using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Spoolman;

/// <summary>
/// Readiness state for a source-qualified spool burn-rate projection.
/// </summary>
public enum SpoolBurnRateProjectionState
{
    /// <summary>The projection has sufficient authoritative data and current stock.</summary>
    Ready = 0,

    /// <summary>The source is available, but authoritative history is insufficient.</summary>
    InsufficientData = 1,

    /// <summary>The owning source or current remaining weight is unavailable.</summary>
    SourceUnavailable = 2,
}

/// <summary>
/// Source-qualified burn-rate projection for one spool.
/// </summary>
public sealed record SpoolBurnRateProjectionDto(
    SpoolSourceKind SourceKind,
    string SourceIdentity,
    int SpoolId,
    double? RemainingGrams,
    double AuthoritativeGramsConsumed,
    double? BurnRateGramsPerDay,
    DateTime? ProjectedThresholdCrossingUtc,
    DateTime EvaluatedAtUtc,
    int SampleCount,
    SpoolBurnRateProjectionState State);

/// <summary>
/// Computes source-qualified spool burn-rate projections.
/// </summary>
public interface ISpoolBurnRateProjectionService
{
    /// <summary>Projects burn rate and reorder-threshold crossing for one spool.</summary>
    Task<SpoolBurnRateProjectionDto> ProjectAsync(
        CanonicalSpoolIdentity identity,
        CancellationToken ct = default);

    /// <summary>
    /// Projects burn rate and reorder-threshold crossing for source-qualified spools,
    /// aggregating usage and resolving inventory in source batches.
    /// </summary>
    Task<IReadOnlyDictionary<CanonicalSpoolIdentity, SpoolBurnRateProjectionDto>> ProjectAsync(
        IReadOnlyCollection<CanonicalSpoolIdentity> identities,
        CancellationToken ct = default);
}

/// <summary>
/// EF-backed implementation that includes only completed, positive,
/// authoritative, source-qualified usage rows.
/// </summary>
public sealed class SpoolBurnRateProjectionService(
    AppDbContext db,
    IFilamentCoverageSpoolResolver spoolResolver,
    ISettingsService settingsService,
    TimeProvider? clock = null) : ISpoolBurnRateProjectionService
{
    // Keep each ID-IN query below SQLite's variable limit even when sources are large.
    private const int UsageSpoolIdBatchSize = 500;

    private readonly AppDbContext _db = db;
    private readonly IFilamentCoverageSpoolResolver _spoolResolver = spoolResolver;
    private readonly ISettingsService _settingsService = settingsService;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<SpoolBurnRateProjectionDto> ProjectAsync(
        CanonicalSpoolIdentity identity,
        CancellationToken ct = default)
    {
        IReadOnlyDictionary<CanonicalSpoolIdentity, SpoolBurnRateProjectionDto> projections =
            await ProjectAsync([identity], ct).ConfigureAwait(false);
        if (!projections.TryGetValue(identity, out SpoolBurnRateProjectionDto? projection))
        {
            throw new InvalidOperationException(
                "The spool burn-rate batch omitted the requested source-qualified identity.");
        }

        return projection;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<CanonicalSpoolIdentity, SpoolBurnRateProjectionDto>> ProjectAsync(
        IReadOnlyCollection<CanonicalSpoolIdentity> identities,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identities);

        CanonicalSpoolIdentity[] distinctIdentities = identities.Distinct().ToArray();
        if (distinctIdentities.Length == 0)
        {
            return new Dictionary<CanonicalSpoolIdentity, SpoolBurnRateProjectionDto>();
        }

        ShiftPlanSettings settings = _settingsService.Get<ShiftPlanSettings>();
        settings.Validate();

        DateTime evaluatedAtUtc = _clock.GetUtcNow().UtcDateTime;
        DateTime cutoffUtc = evaluatedAtUtc.AddDays(-settings.SpoolBurnRateLookbackDays);
        Dictionary<CanonicalSpoolIdentity, UsageAggregate> usageByIdentity =
            await LoadUsageAggregatesAsync(
                distinctIdentities,
                cutoffUtc,
                evaluatedAtUtc,
                ct).ConfigureAwait(false);
        IReadOnlyDictionary<CanonicalSpoolIdentity, FilamentCoverageSpoolSnapshot> snapshots =
            await _spoolResolver.ResolveAsync(distinctIdentities, ct).ConfigureAwait(false);

        Dictionary<CanonicalSpoolIdentity, SpoolBurnRateProjectionDto> projections =
            new(distinctIdentities.Length);
        foreach (CanonicalSpoolIdentity identity in distinctIdentities)
        {
            ct.ThrowIfCancellationRequested();
            if (!snapshots.TryGetValue(identity, out FilamentCoverageSpoolSnapshot? snapshot))
            {
                throw new InvalidOperationException(
                    "The spool inventory batch omitted a requested source-qualified identity.");
            }

            usageByIdentity.TryGetValue(identity, out UsageAggregate usage);
            projections.Add(identity, Project(identity, usage, snapshot, settings, evaluatedAtUtc));
        }

        return projections;
    }

    private async Task<Dictionary<CanonicalSpoolIdentity, UsageAggregate>> LoadUsageAggregatesAsync(
        IReadOnlyCollection<CanonicalSpoolIdentity> identities,
        DateTime cutoffUtc,
        DateTime evaluatedAtUtc,
        CancellationToken ct)
    {
        Dictionary<CanonicalSpoolIdentity, UsageAggregate> usageByIdentity = [];
        foreach (IGrouping<(SpoolSourceKind SourceKind, string SourceIdentity), CanonicalSpoolIdentity> sourceGroup
            in identities.GroupBy(identity => (identity.SourceKind, identity.SourceIdentity)))
        {
            int[] spoolIds = sourceGroup
                .Select(identity => identity.SpoolId)
                .Distinct()
                .ToArray();
            foreach (int[] spoolIdBatch in spoolIds.Chunk(UsageSpoolIdBatchSize))
            {
                ct.ThrowIfCancellationRequested();
                List<UsageAggregate> aggregates = await _db.PrintJobToolheadUsages
                    .AsNoTracking()
                    .Where(usage =>
                        usage.SpoolSourceKind == sourceGroup.Key.SourceKind
                        && usage.SpoolSourceIdentity == sourceGroup.Key.SourceIdentity
                        && usage.SpoolmanSpoolId.HasValue
                        && spoolIdBatch.Contains(usage.SpoolmanSpoolId.Value)
                        && usage.IsFilamentUsageAuthoritative
                        && usage.FilamentUsageGrams.HasValue
                        && usage.FilamentUsageGrams.Value > 0
                        && usage.PrintJob.Status == PrintJobStatus.Completed
                        && usage.PrintJob.ActualEndTime.HasValue
                        && usage.PrintJob.ActualEndTime.Value >= cutoffUtc
                        && usage.PrintJob.ActualEndTime.Value <= evaluatedAtUtc)
                    .GroupBy(usage => usage.SpoolmanSpoolId!.Value)
                    .Select(group => new UsageAggregate(
                        group.Key,
                        group.Sum(usage => usage.FilamentUsageGrams!.Value),
                        group.Count()))
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                foreach (UsageAggregate aggregate in aggregates)
                {
                    CanonicalSpoolIdentity identity = new(
                        sourceGroup.Key.SourceKind,
                        sourceGroup.Key.SourceIdentity,
                        aggregate.SpoolId);
                    usageByIdentity[identity] = aggregate;
                }
            }
        }

        return usageByIdentity;
    }

    private static SpoolBurnRateProjectionDto Project(
        CanonicalSpoolIdentity identity,
        UsageAggregate usage,
        FilamentCoverageSpoolSnapshot snapshot,
        ShiftPlanSettings settings,
        DateTime evaluatedAtUtc)
    {
        double consumedGrams = usage.ConsumedGrams;
        bool hasFiniteConsumption = double.IsFinite(consumedGrams);
        if (!hasFiniteConsumption)
        {
            consumedGrams = double.MaxValue;
        }

        double? burnRate = hasFiniteConsumption
            && usage.SampleCount >= settings.SpoolBurnRateMinimumSamples
            ? consumedGrams / settings.SpoolBurnRateLookbackDays
            : null;
        if (burnRate is not > 0 || !double.IsFinite(burnRate.Value))
        {
            burnRate = null;
        }

        double? remainingGrams = snapshot.Spool?.RemainingWeightG;
        if (remainingGrams is < 0
            || (remainingGrams.HasValue && !double.IsFinite(remainingGrams.Value)))
        {
            remainingGrams = null;
        }

        SpoolBurnRateProjectionState state;
        DateTime? thresholdCrossingUtc = null;
        if (snapshot.ErrorReason is not null || remainingGrams is null)
        {
            state = SpoolBurnRateProjectionState.SourceUnavailable;
        }
        else if (burnRate is null)
        {
            state = SpoolBurnRateProjectionState.InsufficientData;
        }
        else
        {
            double gramsAboveThreshold = remainingGrams.Value - settings.SpoolReorderThresholdGrams;
            double projectedDays = gramsAboveThreshold / burnRate.Value;
            double maximumProjectedDays = (DateTime.MaxValue - evaluatedAtUtc).TotalDays;
            if (gramsAboveThreshold <= 0)
            {
                state = SpoolBurnRateProjectionState.Ready;
                thresholdCrossingUtc = evaluatedAtUtc;
            }
            else if (!double.IsFinite(projectedDays)
                || projectedDays >= maximumProjectedDays)
            {
                state = SpoolBurnRateProjectionState.InsufficientData;
            }
            else
            {
                state = SpoolBurnRateProjectionState.Ready;
                thresholdCrossingUtc = evaluatedAtUtc.AddDays(projectedDays);
            }
        }

        return new SpoolBurnRateProjectionDto(
            identity.SourceKind,
            identity.SourceIdentity,
            identity.SpoolId,
            remainingGrams,
            consumedGrams,
            burnRate,
            thresholdCrossingUtc,
            evaluatedAtUtc,
            usage.SampleCount,
            state);
    }

    private readonly record struct UsageAggregate(int SpoolId, double ConsumedGrams, int SampleCount);
}
