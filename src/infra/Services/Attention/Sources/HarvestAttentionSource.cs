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
/// A completed job is only surfaced when it is the most recent job on its printer AND
/// no subsequent activity (Starting/Printing/Paused, or any later ActualStartTime)
/// indicates the operator has already cleared the plate to make room for a newer print.
/// F9/#714 adds an authoritative harvest ledger, at which point the query can be
/// tightened to jobs whose ledger entry is missing; the id shape remains
/// <c>harvest:{jobId}</c> so persisted snoozes survive the upgrade.
/// </para>
/// <para>
/// The <see cref="AttentionActionKind.Harvest"/> action is intentionally NOT advertised
/// until F9/#714 wires the harvest ledger — advertising it would return 501 and violate
/// the "no advertised action returns 501" contract from #707. Only Snooze is offered
/// for now.
/// </para>
/// </remarks>
public sealed class HarvestAttentionSource(
    AppDbContext dbContext,
    TimeProvider? timeProvider = null) : IAttentionSource
{
    /// <summary>Only surface completions newer than this window.</summary>
    public static readonly TimeSpan HarvestWindow = TimeSpan.FromHours(48);

    /// <summary>Cap on rows considered per composition pass.</summary>
    private const int MaxRows = 400;

    private readonly AppDbContext _db = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public string SourceName => "harvest";

    /// <inheritdoc />
    public async Task<IReadOnlyList<AttentionItemDto>> GetItemsAsync(CancellationToken cancellationToken)
    {
        DateTime cutoff = _clock.GetUtcNow().UtcDateTime - HarvestWindow;

        // Load recent completions and any live/subsequent activity for the same printers
        // in one round-trip; correlation happens in memory. The extra rows are bounded by
        // MaxRows and by the completion window; a farm producing more churn than that in
        // 48h will still get the freshest cards.
        List<PrintJob> jobs = await _db.PrintJobs
            .AsNoTracking()
            .Where(j => j.AssignedPrinterId != null
                && ((j.Status == PrintJobStatus.Completed && j.ActualEndTime != null && j.ActualEndTime > cutoff)
                    || j.Status == PrintJobStatus.Starting
                    || j.Status == PrintJobStatus.Printing
                    || j.Status == PrintJobStatus.Paused
                    || (j.ActualStartTime != null && j.ActualStartTime > cutoff)))
            .Include(j => j.AssignedPrinter)
            .OrderByDescending(j => j.ActualEndTime ?? j.ActualStartTime ?? j.QueuedAt)
            .Take(MaxRows)
            .ToListAsync(cancellationToken);

        List<AttentionItemDto> items = new();

        IEnumerable<IGrouping<Guid, PrintJob>> byPrinter = jobs
            .Where(j => j.AssignedPrinterId is not null)
            .GroupBy(j => j.AssignedPrinterId!.Value);

        foreach (IGrouping<Guid, PrintJob> group in byPrinter)
        {
            PrintJob? latestCompletion = group
                .Where(j => j.Status == PrintJobStatus.Completed && j.ActualEndTime != null && j.ActualEndTime > cutoff)
                .OrderByDescending(j => j.ActualEndTime)
                .FirstOrDefault();
            if (latestCompletion?.ActualEndTime is not DateTime completedAtUtc)
            {
                continue;
            }

            // Suppress when a newer print exists on this printer: either currently in
            // flight, or a strictly-newer completed/finished job. That means the operator
            // has already cleared the plate; the harvest card would be stale.
            bool hasFresherActivity = group.Any(j =>
                j.Id != latestCompletion.Id
                && (j.Status is PrintJobStatus.Starting or PrintJobStatus.Printing or PrintJobStatus.Paused
                    || (j.ActualStartTime is DateTime started && started > completedAtUtc)
                    || (j.ActualEndTime is DateTime otherEnd && otherEnd > completedAtUtc)));
            if (hasFresherActivity)
            {
                continue;
            }

            Guid printerId = latestCompletion.AssignedPrinterId!.Value;
            string printerName = latestCompletion.AssignedPrinter?.Name ?? "Unknown printer";
            DateTime completedAt = DateTime.SpecifyKind(completedAtUtc, DateTimeKind.Utc);

            List<AttentionActionDto> actions = new(1)
            {
                new AttentionActionDto(AttentionActionKind.Snooze, "Snooze", RequiresConfirmation: false),
            };

            items.Add(new AttentionItemDto(
                Id: AttentionIdPrefixes.Build(AttentionIdPrefixes.Harvest, latestCompletion.Id),
                Kind: AttentionKind.Harvest,
                Severity: AttentionSeverity.Info,
                PrinterId: printerId,
                PrinterName: printerName,
                Title: "Plate ready to harvest",
                Detail: $"{latestCompletion.Name} finished on {printerName}. Action: harvest the plate and confirm the count.",
                OccurredAt: completedAt,
                Actions: actions,
                JobId: latestCompletion.Id));
        }

        return items;
    }
}
