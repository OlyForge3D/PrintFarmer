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
    private readonly AppDbContext _db = db;
    private readonly IFilamentCoverageSpoolResolver _spoolResolver = spoolResolver;
    private readonly ISettingsService _settingsService = settingsService;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<SpoolBurnRateProjectionDto> ProjectAsync(
        CanonicalSpoolIdentity identity,
        CancellationToken ct = default)
    {
        ShiftPlanSettings settings = _settingsService.Get<ShiftPlanSettings>();
        settings.Validate();

        DateTime evaluatedAtUtc = _clock.GetUtcNow().UtcDateTime;
        DateTime cutoffUtc = evaluatedAtUtc.AddDays(-settings.SpoolBurnRateLookbackDays);

        List<double> samples = await _db.PrintJobToolheadUsages
            .AsNoTracking()
            .Where(usage =>
                usage.SpoolSourceKind == identity.SourceKind
                && usage.SpoolSourceIdentity == identity.SourceIdentity
                && usage.SpoolmanSpoolId == identity.SpoolId
                && usage.IsFilamentUsageAuthoritative
                && usage.FilamentUsageGrams.HasValue
                && usage.FilamentUsageGrams.Value > 0
                && usage.PrintJob.Status == PrintJobStatus.Completed
                && usage.PrintJob.ActualEndTime.HasValue
                && usage.PrintJob.ActualEndTime.Value >= cutoffUtc
                && usage.PrintJob.ActualEndTime.Value <= evaluatedAtUtc)
            .Select(usage => usage.FilamentUsageGrams!.Value)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        double consumedGrams = samples.Sum();
        double? burnRate = samples.Count >= settings.SpoolBurnRateMinimumSamples
            ? consumedGrams / settings.SpoolBurnRateLookbackDays
            : null;
        if (burnRate is not > 0 || !double.IsFinite(burnRate.Value))
        {
            burnRate = null;
        }

        FilamentCoverageSpoolSnapshot snapshot = await _spoolResolver
            .ResolveSpoolAsync(identity, ct)
            .ConfigureAwait(false);
        double? remainingGrams = snapshot.Spool?.RemainingWeightG;

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
            state = SpoolBurnRateProjectionState.Ready;
            double gramsAboveThreshold = remainingGrams.Value - settings.SpoolReorderThresholdGrams;
            thresholdCrossingUtc = gramsAboveThreshold <= 0
                ? evaluatedAtUtc
                : evaluatedAtUtc.AddDays(gramsAboveThreshold / burnRate.Value);
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
            samples.Count,
            state);
    }
}
