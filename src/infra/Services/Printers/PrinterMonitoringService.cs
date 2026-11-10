using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>One registry printer, its observed status, and active-work protection.</summary>
public sealed record PrinterMonitoringSnapshot(Printer Printer, PrinterStatusCacheSnapshot? Status, bool HasActiveJob)
{
    /// <summary>Effective monitoring exemption, not a connectivity status.</summary>
    public bool IsExpectedOffline => PrinterMonitoringPolicy.IsExpectedOffline(Printer, Status, HasActiveJob);
}

/// <summary>Shared monitoring interpretation for health and attention consumers.</summary>
public interface IPrinterMonitoringReader
{
    /// <summary>Reads registered enabled printers and current safety exemptions.</summary>
    Task<IReadOnlyList<PrinterMonitoringSnapshot>> GetSnapshotsAsync(CancellationToken ct);
}

/// <summary>Composes monitoring intent with live evidence and active-job protection.</summary>
public sealed class PrinterMonitoringService(
    AppDbContext db,
    IPrinterStatusCacheReader statusCache,
    IPrinterCacheInvalidator invalidator,
    IHubContext<PrinterHub> hub,
    IAttentionBroadcaster attention) : IPrinterMonitoringReader
{
    /// <summary>Active work is never covered by an intentional-shutdown exemption.</summary>
    public Task<bool> HasActiveJobAsync(Guid printerId, CancellationToken ct) =>
        db.PrintJobs.AnyAsync(
            job => job.AssignedPrinterId == printerId
            && (job.Status == PrintJobStatus.Assigned
                || job.Status == PrintJobStatus.Starting
                || job.Status == PrintJobStatus.Printing
                || job.Status == PrintJobStatus.Paused), ct);

    /// <summary>Returns only exemptions that are currently safe to apply.</summary>
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

    /// <summary>Invalidates configuration and client projections after a committed intent change.</summary>
    public async Task NotifyChangedAsync(Guid printerId, CancellationToken ct)
    {
        invalidator.Invalidate(printerId);
        await hub.Clients.Group(Farm.Infrastructure.Security.AuthorizedHubGroups.Printer(printerId))
            .SendAsync("printermonitoringchanged", new { printerId }, ct).ConfigureAwait(false);
        await attention.NotifyChangedAsync(
            new AttentionChangedPayload($"offline:{printerId}", AttentionChangeKind.Updated, DateTime.UtcNow),
            ct).ConfigureAwait(false);
    }
}
