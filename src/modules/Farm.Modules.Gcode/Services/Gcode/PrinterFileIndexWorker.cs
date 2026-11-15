using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Modules.Gcode.Services.Gcode;

/// <summary>Enrolls registered printers and dispatches bounded idle scans.</summary>
public sealed class PrinterFileIndexWorker(
    IServiceScopeFactory scopes,
    ILogger<PrinterFileIndexWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await RunBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Printer file index batch could not access its durable state");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunBatchAsync(CancellationToken ct = default)
    {
        await using AsyncServiceScope batchScope = scopes.CreateAsyncScope();
        IDbContextFactory<AppDbContext> dbFactory =
            batchScope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await PrinterFileIndexPolicy.ReadAsync(db, ct);
        if (settings.Enabled)
        {
            await ScanDueAsync(db, dbFactory, settings, ct);
        }

        // Local thumbnail garbage collection does no printer I/O, so disabling discovery must not stop reclamation.
        try
        {
            PrinterFileThumbnailStore? store = batchScope.ServiceProvider.GetService<PrinterFileThumbnailStore>();
            if (store is not null)
            {
                await store.CleanupAsync(ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Printer file thumbnail cleanup failed: {FailureType}", ex.GetType().Name);
        }
    }

    private async Task ScanDueAsync(
        AppDbContext db, IDbContextFactory<AppDbContext> dbFactory, PrinterFileIndexSettings settings, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        List<Printer> printers = await db.Printers.AsNoTracking().Where(x => x.IsEnabled).ToListAsync(ct);
        Dictionary<Guid, PrinterFileScanState> states = await db.PrinterFileScanStates.AsNoTracking()
            .ToDictionaryAsync(x => x.SourcePrinterId, ct);
        HashSet<Guid> enrichmentDue = (await PrinterFileEnrichmentSchedule.DuePrinterIdsAsync(db, now, ct)).ToHashSet();

        bool InventoryDue(Printer printer) =>
            !states.TryGetValue(printer.Id, out PrinterFileScanState? state) ||
            (state.Enabled && (state.NextScanAtUtc <= now ||
             state.ConfigurationFingerprint != PrinterFileIndexPolicy.ConfigurationFingerprint(printer)));

        List<Printer> inventoryDue = printers.Where(InventoryDue).ToList();
        List<Printer> enrichmentOnly = printers
            .Where(printer => !InventoryDue(printer) && enrichmentDue.Contains(printer.Id) &&
                states.TryGetValue(printer.Id, out PrinterFileScanState? state) && state.Enabled)
            .ToList();
        Dictionary<Guid, DateTime?> lastEnrichment = await PrinterFileEnrichmentSchedule.LastAttemptByPrinterAsync(
            db, enrichmentOnly.Select(printer => printer.Id).ToArray(), ct);

        // Overdue inventory scans keep schedule order ahead of enrichment-only work, which rotates by the
        // least recent per-file attempt so a large backlog cannot pin the same printers in every batch.
        Guid[] due = inventoryDue
            .OrderBy(printer => states.TryGetValue(printer.Id, out PrinterFileScanState? state)
                ? state.NextScanAtUtc : DateTime.MinValue)
            .ThenBy(printer => printer.Id)
            .Concat(enrichmentOnly
                .OrderBy(printer => lastEnrichment.GetValueOrDefault(printer.Id) ?? DateTime.MinValue)
                .ThenBy(printer => printer.Id))
            .Take(32).Select(printer => printer.Id).ToArray();
        await Parallel.ForEachAsync(
            due,
            new ParallelOptions { MaxDegreeOfParallelism = settings.MaxConcurrentIo, CancellationToken = ct },
            async (id, token) =>
            {
                try
                {
                    await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<PrinterFileCatalogService>().ScanAsync(id, token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    logger.LogInformation("Printer file scan lost authority for {PrinterId}", id);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    logger.LogWarning("Printer file scan failed for {PrinterId}: {FailureType}", id, ex.GetType().Name);
                    try
                    {
                        await using AppDbContext failureDb = await dbFactory.CreateDbContextAsync(token);
                        PrinterFileScanState? failed = await failureDb.PrinterFileScanStates.AsNoTracking()
                            .SingleOrDefaultAsync(x => x.SourcePrinterId == id, token);
                        string fingerprint = PrinterFileIndexPolicy.ConfigurationFingerprint(printers.Single(x => x.Id == id));
                        if (failed is not null && failed.ConfigurationFingerprint == fingerprint)
                        {
                            int failures = Math.Min(30, failed.ConsecutiveFailures + 1);
                            DateTime failedAt = DateTime.UtcNow;
                            DateTime next = failedAt + PrinterFileIndexPolicy.NextDelay(id, failures, settings);
                            await failureDb.PrinterFileScanStates.Where(x => x.Id == failed.Id &&
                                x.Revision == failed.Revision && x.ConfigurationFingerprint == fingerprint &&
                                (!x.LeaseOwner.HasValue || x.LeaseExpiresAtUtc <= failedAt))
                                .ExecuteUpdateAsync(
                                    update => update
                                    .SetProperty(x => x.Outcome, PrinterFileScanOutcome.Unavailable)
                                    .SetProperty(x => x.ReasonCode, "scan_failed")
                                    .SetProperty(x => x.ConsecutiveFailures, failures)
                                    .SetProperty(x => x.LastAttemptAtUtc, failedAt)
                                    .SetProperty(x => x.NextScanAtUtc, next)
                                    .SetProperty(x => x.Revision, x => x.Revision + 1), token);
                        }
                    }
                    catch (Exception recordingFailure) when (!token.IsCancellationRequested)
                    {
                        logger.LogWarning(
                            "Printer file scan failure could not be recorded for {PrinterId}: {FailureType}",
                            id, recordingFailure.GetType().Name);
                    }
                }
            });
    }
}
