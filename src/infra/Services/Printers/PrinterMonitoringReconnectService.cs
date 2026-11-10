using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>Persists proven reconnects, including reconnects followed by another outage between ticks.</summary>
public sealed class PrinterMonitoringReconnectService(
    IServiceScopeFactory scopeFactory,
    IPrinterStatusCacheReader statusCache,
    ILogger<PrinterMonitoringReconnectService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unable to reconcile expected-offline printer reconnects; will retry");
            }
        }
    }

    /// <summary>Uses the same revision guard as operator edits; a concurrent acknowledgement wins.</summary>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        PrinterMonitoringService monitoring = scope.ServiceProvider.GetRequiredService<PrinterMonitoringService>();
        List<Printer> printers = await db.Printers
            .Where(printer => printer.ExpectedOfflineSinceUtc != null).ToListAsync(ct).ConfigureAwait(false);
        foreach (Printer printer in printers)
        {
            if (!PrinterMonitoringPolicy.HasReconnected(
                    printer.ExpectedOfflineSinceUtc!.Value, statusCache.GetSnapshot(printer.Id)))
            {
                continue;
            }

            printer.ExpectedOfflineSinceUtc = null;
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                logger.LogInformation("Printer {PrinterId} changed while clearing offline expectation; retrying next pass", printer.Id);
                db.Entry(printer).State = EntityState.Detached;
                continue;
            }

            await monitoring.NotifyChangedAsync(printer.Id, ct).ConfigureAwait(false);
        }
    }
}
