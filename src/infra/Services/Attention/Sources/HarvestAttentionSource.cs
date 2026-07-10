using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Attention.Sources;

/// <summary>
/// Attention source that surfaces completed print jobs whose plate is presumed to be
/// awaiting operator harvest.
/// </summary>
/// <remarks>
/// <para>
/// Uses recent <see cref="PrintJobStatus.Completed"/> jobs on assigned printers as a
/// proxy for "print done, plate not yet cleared". This is intentionally minimal: F9/#714
/// adds an authoritative harvest ledger, at which point the source query can be
/// tightened to jobs whose ledger entry is missing. The item id remains
/// <c>harvest:{jobId}</c> so persisted snoozes survive the upgrade.
/// </para>
/// <para>
/// Only the most recent completed job per printer is surfaced; older jobs remain in the
/// job history and are not operator-actionable from the attention feed.
/// </para>
/// </remarks>
public sealed class HarvestAttentionSource(
    AppDbContext dbContext,
    TimeProvider? timeProvider = null) : IAttentionSource
{
    /// <summary>Only surface completions newer than this window.</summary>
    public static readonly TimeSpan HarvestWindow = TimeSpan.FromHours(48);

    /// <summary>Cap on completions considered per composition pass.</summary>
    private const int MaxCompletions = 100;

    private readonly AppDbContext _db = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public string SourceName => "harvest";

    /// <inheritdoc />
    public async Task<IReadOnlyList<AttentionItemDto>> GetItemsAsync(CancellationToken cancellationToken)
    {
        DateTime cutoff = _clock.GetUtcNow().UtcDateTime - HarvestWindow;

        List<PrintJob> completions = await _db.PrintJobs
            .AsNoTracking()
            .Where(j => j.Status == PrintJobStatus.Completed
                && j.AssignedPrinterId != null
                && j.ActualEndTime != null
                && j.ActualEndTime > cutoff)
            .Include(j => j.AssignedPrinter)
            .OrderByDescending(j => j.ActualEndTime)
            .Take(MaxCompletions)
            .ToListAsync(cancellationToken);

        List<AttentionItemDto> items = new();
        HashSet<Guid> printersSeen = new();

        foreach (PrintJob job in completions)
        {
            if (job.AssignedPrinterId is not Guid printerId)
            {
                continue;
            }

            // Only surface the most recent completion per printer to keep the feed sparse.
            if (!printersSeen.Add(printerId))
            {
                continue;
            }

            string printerName = job.AssignedPrinter?.Name ?? "Unknown printer";
            DateTime completedAt = DateTime.SpecifyKind(job.ActualEndTime!.Value, DateTimeKind.Utc);

            List<AttentionActionDto> actions = new(2)
            {
                new AttentionActionDto(AttentionActionKind.Harvest, "Harvest", RequiresConfirmation: false),
                new AttentionActionDto(AttentionActionKind.Snooze, "Snooze", RequiresConfirmation: false),
            };

            items.Add(new AttentionItemDto(
                Id: AttentionIdPrefixes.Build(AttentionIdPrefixes.Harvest, job.Id),
                Kind: AttentionKind.Harvest,
                Severity: AttentionSeverity.Info,
                PrinterId: printerId,
                PrinterName: printerName,
                Title: "Plate ready to harvest",
                Detail: $"{job.Name} finished on {printerName}. Action: harvest the plate and confirm the count.",
                OccurredAt: completedAt,
                Actions: actions));
        }

        return items;
    }
}
