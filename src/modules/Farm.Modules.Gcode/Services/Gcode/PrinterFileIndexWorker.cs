using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Modules.Gcode.Services.Gcode;

/// <summary>Enrolls registered printers and dispatches bounded idle scans.</summary>
public sealed class PrinterFileIndexWorker(
    IServiceScopeFactory scopes,
    IDbContextFactory<AppDbContext> dbFactory,
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
            catch (Exception ex) when (ex is DbUpdateException or System.Data.Common.DbException)
            {
                logger.LogError(ex, "Printer file index batch could not access its durable state");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunBatchAsync(CancellationToken ct = default)
    {
        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await PrinterFileIndexPolicy.ReadAsync(db, ct);
        if (!settings.Enabled)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        List<Printer> printers = await db.Printers.AsNoTracking().Where(x => x.IsEnabled).ToListAsync(ct);
        Dictionary<Guid, PrinterFileScanState> states = await db.PrinterFileScanStates.AsNoTracking()
            .ToDictionaryAsync(x => x.SourcePrinterId, ct);
        Guid[] due = printers.Where(printer =>
                !states.TryGetValue(printer.Id, out PrinterFileScanState? state) ||
                (state.Enabled && (state.NextScanAtUtc <= now ||
                 state.ConfigurationFingerprint != PrinterFileIndexPolicy.ConfigurationFingerprint(printer))))
            .OrderBy(printer => states.TryGetValue(printer.Id, out PrinterFileScanState? state)
                ? state.NextScanAtUtc : DateTime.MinValue)
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
                catch (Exception ex) when (ex is DbUpdateException or System.Data.Common.DbException or HttpRequestException)
                {
                    logger.LogWarning(ex, "Printer file scan failed for {PrinterId}", id);
                }
            });
    }
}
