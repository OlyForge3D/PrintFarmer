using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Attention;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>One in-service printer, its observed status, and active-work protection.</summary>
public sealed record PrinterMonitoringSnapshot(Printer Printer, PrinterStatusCacheSnapshot? Status, bool HasActiveJob);

/// <summary>An active job preserved for operator review during availability migration.</summary>
public sealed record PrinterReviewRequiredJob(Guid JobId, string JobName, Guid PrinterId, string PrinterName);

/// <summary>Shared printer-status and review snapshots for health and attention consumers.</summary>
public interface IPrinterMonitoringReader
{
    /// <summary>Returns whether a printer has assigned work or cached active-print evidence.</summary>
    Task<bool> HasActiveWorkAsync(Guid printerId, CancellationToken ct);

    /// <summary>Reads in-service printers and active-work state.</summary>
    Task<IReadOnlyList<PrinterMonitoringSnapshot>> GetSnapshotsAsync(CancellationToken ct);

    /// <summary>Reads active jobs marked for operator review, including out-of-service printers.</summary>
    Task<IReadOnlyList<PrinterReviewRequiredJob>> GetReviewRequiredJobsAsync(CancellationToken ct);
}

/// <summary>Composes printer status with persisted active-job review markers.</summary>
public sealed class PrinterMonitoringService(
    AppDbContext db,
    IPrinterStatusCacheReader statusCache) : IPrinterMonitoringReader
{
    /// <summary>Returns whether a job is assigned, starting, printing, or paused.</summary>
    public Task<bool> HasActiveJobAsync(Guid printerId, CancellationToken ct) =>
        db.PrintJobs.AnyAsync(
            job => job.AssignedPrinterId == printerId
            && (job.Status == PrintJobStatus.Assigned
                || job.Status == PrintJobStatus.Starting
                || job.Status == PrintJobStatus.Printing
                || job.Status == PrintJobStatus.Paused), ct);

    /// <summary>Includes cached active print evidence so an operator cannot disable a printing machine.</summary>
    public async Task<bool> HasActiveWorkAsync(Guid printerId, CancellationToken ct)
    {
        if (await HasActiveJobAsync(printerId, ct).ConfigureAwait(false))
        {
            return true;
        }

        PrinterStatusCacheSnapshot? snapshot = statusCache.GetSnapshot(printerId);
        return snapshot?.Status is { IsOnline: true } status
            && PrinterStateClassifier.IsActivePrintingJob(status.RawActivityState ?? status.State);
    }

    /// <summary>Returns in-service printers and their active-work state.</summary>
    public async Task<IReadOnlyList<PrinterMonitoringSnapshot>> GetSnapshotsAsync(CancellationToken ct)
    {
        List<Printer> printers = await db.Printers.AsNoTracking()
            .Where(printer => printer.IsEnabled)
            .ToListAsync(ct).ConfigureAwait(false);
        List<Guid?> active = await db.PrintJobs.AsNoTracking()
            .Where(job => job.AssignedPrinterId != null
                && (job.Status == PrintJobStatus.Assigned || job.Status == PrintJobStatus.Starting
                    || job.Status == PrintJobStatus.Printing || job.Status == PrintJobStatus.Paused))
            .Select(job => job.AssignedPrinterId).Distinct().ToListAsync(ct).ConfigureAwait(false);
        HashSet<Guid?> activeIds = active.ToHashSet();
        return printers.Select(printer => new PrinterMonitoringSnapshot(
            printer, statusCache.GetSnapshot(printer.Id), activeIds.Contains(printer.Id))).ToList();
    }

    /// <summary>Returns marked jobs only while they occupy a printer.</summary>
    public async Task<IReadOnlyList<PrinterReviewRequiredJob>> GetReviewRequiredJobsAsync(CancellationToken ct) =>
        await (
            from job in db.PrintJobs.AsNoTracking()
            join printer in db.Printers.AsNoTracking() on job.AssignedPrinterId equals printer.Id
            where job.RequiresOperatorReview
                && (job.Status == PrintJobStatus.Assigned
                    || job.Status == PrintJobStatus.Starting
                    || job.Status == PrintJobStatus.Printing
                    || job.Status == PrintJobStatus.Paused)
            select new PrinterReviewRequiredJob(job.Id, job.Name, printer.Id, printer.Name))
        .ToListAsync(ct).ConfigureAwait(false);
}
