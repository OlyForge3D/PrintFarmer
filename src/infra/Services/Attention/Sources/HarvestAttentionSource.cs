using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.OperatorFeatures;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Attention.Sources;

/// <summary>Surfaces completed, unharvested print jobs as stable harvest attention items.</summary>
public sealed class HarvestAttentionSource(
    IDbContextFactory<AppDbContext> dbFactory,
    IOperatorFeatureGate featureGate) : IAttentionSource
{
    private const int MaxItems = 100;

    /// <inheritdoc />
    public string SourceName => AttentionIdPrefixes.Harvest;

    /// <inheritdoc />
    public async Task<IReadOnlyList<AttentionItemDto>> GetItemsAsync(CancellationToken cancellationToken)
    {
        if (!featureGate.IsEnabled(OperatorFeature.PrintedPartsInventory))
        {
            return [];
        }

        await using AppDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        List<PrintJob> jobs = await db.PrintJobs
            .AsNoTracking()
            .Include(job => job.AssignedPrinter)
            .Where(job => job.Status == PrintJobStatus.Completed
                && job.HarvestedAt == null
                && job.AssignedPrinterId != null)
            .OrderBy(job => job.ActualEndTime ?? job.UpdatedAt)
            .ThenBy(job => job.Id)
            .Take(MaxItems)
            .ToListAsync(cancellationToken);

        return jobs
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
    }
}
