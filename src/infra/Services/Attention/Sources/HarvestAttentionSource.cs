using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Mutations;
using Farm.Infrastructure.Services.OperatorFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Infrastructure.Services.Attention.Sources;

/// <summary>Surfaces recently completed, unharvested print jobs as stable harvest attention items.</summary>
/// <remarks>
/// <para>
/// A stale completion window keeps the feed to actionable plates only. <c>HarvestedAt</c> was
/// introduced as a nullable column, so every job completed before printed-parts inventory
/// shipped reads as "never harvested". Without a window those archived jobs flood the feed with
/// cards whose advertised harvest action can never succeed (they have no part-output mappings).
/// Older completions remain queryable through queue history.
/// </para>
/// <para>
/// Selection is newest-first so that genuinely harvest-ready plates always win the item cap.
/// Ordering ascending here would spend the cap on the oldest rows and hide current work.
/// </para>
/// </remarks>
public sealed class HarvestAttentionSource(
    IDbContextFactory<AppDbContext> dbFactory,
    IOperatorFeatureGate featureGate,
    IMutationWatermarkReader? watermarkReader = null,
    ILogger<HarvestAttentionSource>? logger = null,
    TimeProvider? timeProvider = null) : IAttentionSource, IAttentionSourceWithOrigin
{
    /// <summary>Only surface plates completed within this window.</summary>
    public static readonly TimeSpan StaleWindow = TimeSpan.FromDays(7);

    private const int MaxItems = 100;
    private readonly ILogger<HarvestAttentionSource> _logger =
        logger ?? NullLogger<HarvestAttentionSource>.Instance;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public string SourceName => AttentionIdPrefixes.Harvest;

    /// <inheritdoc />
    public async Task<IReadOnlyList<AttentionItemDto>> GetItemsAsync(CancellationToken cancellationToken)
    {
        if (!await featureGate.IsEnabledAsync(OperatorFeature.PrintedPartsInventory, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        List<PrintJob> jobs = await GetJobsAsync(MaxItems, cancellationToken).ConfigureAwait(false);
        return MapItems(jobs);
    }

    /// <inheritdoc />
    public async Task<AttentionSourceResult> GetItemsWithOriginAsync(CancellationToken cancellationToken)
    {
        long? originWatermark = await OriginWatermark
            .CaptureAsync(watermarkReader, _logger, "harvest attention source", cancellationToken)
            .ConfigureAwait(false);
        bool enabledBefore = await featureGate
            .IsEnabledStrictAsync(OperatorFeature.PrintedPartsInventory, cancellationToken)
            .ConfigureAwait(false);
        if (!enabledBefore)
        {
            bool enabledAfter = await featureGate
                .IsEnabledStrictAsync(OperatorFeature.PrintedPartsInventory, cancellationToken)
                .ConfigureAwait(false);
            if (enabledAfter)
            {
                throw new InvalidOperationException(
                    "Printed-parts inventory feature changed during harvest observation.");
            }

            return CompleteResult([], originWatermark);
        }

        List<PrintJob> jobs = await GetJobsAsync(MaxItems + 1, cancellationToken).ConfigureAwait(false);
        bool enabledAfterQuery = await featureGate
            .IsEnabledStrictAsync(OperatorFeature.PrintedPartsInventory, cancellationToken)
            .ConfigureAwait(false);
        if (!enabledAfterQuery)
        {
            throw new InvalidOperationException(
                "Printed-parts inventory feature changed during harvest observation.");
        }

        bool isComplete = jobs.Count <= MaxItems;
        IReadOnlyList<AttentionItemDto> items = MapItems(jobs.Take(MaxItems));
        return new AttentionSourceResult(items, originWatermark)
        {
            AuthorityKind = AttentionKind.Harvest,
            IsAuthoritativeComplete = isComplete,
            IncompleteReasons = isComplete ? [] : ["harvest-item-cap"],
        };
    }

    private async Task<List<PrintJob>> GetJobsAsync(int take, CancellationToken cancellationToken)
    {
        DateTime cutoff = _clock.GetUtcNow().UtcDateTime - StaleWindow;
        await using AppDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.PrintJobs
            .AsNoTracking()
            .Include(job => job.AssignedPrinter)
            .Where(job => job.Status == PrintJobStatus.Completed
                && job.HarvestedAt == null
                && job.AssignedPrinterId != null
                && (job.ActualEndTime ?? job.UpdatedAt) >= cutoff)
            .OrderByDescending(job => job.ActualEndTime ?? job.UpdatedAt)
            .ThenBy(job => job.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    private static List<AttentionItemDto> MapItems(IEnumerable<PrintJob> jobs)
        => jobs
            .Where(job => job.AssignedPrinter is not null)
            .Select(job => new AttentionItemDto(
                Id: AttentionIdPrefixes.Build(AttentionIdPrefixes.Harvest, job.Id),
                Kind: AttentionKind.Harvest,
                Severity: AttentionSeverity.Info,
                PrinterId: job.AssignedPrinterId!.Value,
                PrinterName: job.AssignedPrinter!.Name,
                Title: "Completed plate ready to harvest",
                Detail: $"{job.Name} completed on {job.AssignedPrinter.Name}. Action: remove the plate and add mapped parts to inventory.",
                OccurredAt: DateTime.SpecifyKind(job.ActualEndTime ?? job.UpdatedAt, DateTimeKind.Utc),
                Actions:
                [
                    new AttentionActionDto(AttentionActionKind.Harvest, "Harvest", RequiresConfirmation: true),
                    new AttentionActionDto(AttentionActionKind.Snooze, "Snooze", RequiresConfirmation: false),
                ],
                JobId: job.Id))
            .ToList();

    private static AttentionSourceResult CompleteResult(
        IReadOnlyList<AttentionItemDto> items,
        long? originWatermark)
        => new(items, originWatermark)
        {
            AuthorityKind = AttentionKind.Harvest,
            IsAuthoritativeComplete = true,
        };
}
