using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services;

/// <summary>
/// Background service to monitor harvest operations and mark them as completed
/// when all files are processed
/// </summary>
public class HarvestCompletionService(
    IServiceProvider serviceProvider,
    IUnifiedLoggingService logger) : BackgroundService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IUnifiedLoggingService _logger = logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation($"HarvestCompletionService started", null, null);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckForCompletedOperationsAsync(stoppingToken);
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation($"HarvestCompletionService stopping due to cancellation", null, null);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in HarvestCompletionService", null, null);
                await Task.Delay(CheckInterval, stoppingToken);
            }
        }
    }

    private async Task CheckForCompletedOperationsAsync(CancellationToken ct)
    {
        // Use an async scope when awaiting EF Core calls to ensure proper async disposal
        await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Find running operations that might be completed
        List<GcodeHarvestOperation> runningOperations = await db.GcodeHarvestOperations
            .Where(o => o.Status == GcodeHarvestStatus.Running && o.FilesFound > 0)
            .ToListAsync(ct);

        _logger.LogInformation($"Found {runningOperations.Count} running harvest operations to check", null, null);

        foreach (GcodeHarvestOperation? operation in runningOperations)
        {
            // Count processed files (added + skipped + errored)
            int processedFiles = operation.FilesAdded + operation.FilesSkipped + operation.FilesErrored;

            _logger.LogInformation($"Operation {operation.Id}: Found={operation.FilesFound}, Added={operation.FilesAdded}, Skipped={operation.FilesSkipped}, Errored={operation.FilesErrored}, Processed={processedFiles}", null, null);

            // Get the count of discovered files for this operation
            int discoveredFileCount = await db.HarvestDiscoveredFiles
                .Where(d => d.HarvestOperationId == operation.Id)
                .CountAsync(ct);

            _logger.LogInformation($"Operation {operation.Id}: Found {discoveredFileCount} files in the DiscoveredGcodeFiles table", null, null);

            if (processedFiles >= operation.FilesFound)
            {
                // All files have been processed
                operation.Status = GcodeHarvestStatus.Completed;
                operation.CompletedAt = DateTime.UtcNow;

                _logger.LogInformation($"Marking operation {operation.Id} as completed. Processed {processedFiles}/{operation.FilesFound} files", null, null);

                _ = await db.SaveChangesAsync(ct);
            }
        }
    }
}
