using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Modules.Gcode.Services.Gcode;

/// <summary>Only persisted ready/confirmed captures are eligible; restart never grants consent.</summary>
public sealed class PrinterFileQueueWorker(
    IServiceScopeFactory scopes, ILogger<PrinterFileQueueWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var next = await db.PrinterFileOperations.AsNoTracking()
                    .Where(x => x.Action == PrinterFileAction.Queue &&
                        x.State == PrinterFileOperationState.LocalReady && !x.CancellationRequested)
                    .OrderBy(x => x.UpdatedAtUtc).Select(x => new { x.Id, x.ActorId }).FirstOrDefaultAsync(stoppingToken);
                if (next is not null)
                {
                    await scope.ServiceProvider.GetRequiredService<PrinterFileQueueService>()
                        .AdmitAsync(next.ActorId, next.Id, stoppingToken);
                }

                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                logger.LogWarning("Printer-file queue admission deferred after an authority or storage conflict");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }
}
