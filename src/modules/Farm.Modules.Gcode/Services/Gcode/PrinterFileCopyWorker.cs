using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Modules.Gcode.Services.Gcode;

/// <summary>Database-backed polling survives reconnects and process restarts; requests never perform I/O.</summary>
public sealed class PrinterFileCopyWorker(
    IServiceScopeFactory scopes, ILogger<PrinterFileCopyWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTime cleanupDue = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                PrinterFileCopyProcessor processor = scope.ServiceProvider.GetRequiredService<PrinterFileCopyProcessor>();
                if (DateTime.UtcNow >= cleanupDue)
                {
                    await processor.CleanupCompletedAttemptsAsync(stoppingToken);
                    cleanupDue = DateTime.UtcNow.AddMinutes(10);
                }

                bool found = await processor.ProcessNextAsync(stoppingToken);
                await Task.Delay(found ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Deliberately omit exception text: HTTP/storage exceptions can expose private paths/URLs.
                logger.LogWarning("Printer-file copy worker deferred after a storage or ownership conflict");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }
}
