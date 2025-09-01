using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services;

/// <summary>
/// Background service to monitor harvest operations and mark them as completed
/// when all files are processed
/// </summary>
public class HarvestCompletionService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<HarvestCompletionService> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);

    public HarvestCompletionService(
        IServiceProvider serviceProvider,
        ILogger<HarvestCompletionService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HarvestCompletionService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckForCompletedOperationsAsync(stoppingToken);
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("HarvestCompletionService stopping due to cancellation");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in HarvestCompletionService");
                await Task.Delay(CheckInterval, stoppingToken);
            }
        }
    }

    private async Task CheckForCompletedOperationsAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Find running operations that might be completed
        var runningOperations = await db.GcodeHarvestOperations
            .Where(o => o.Status == GcodeHarvestStatus.Running && o.FilesFound > 0)
            .ToListAsync(ct);

        _logger.LogInformation("Found {OperationCount} running harvest operations to check", runningOperations.Count);

        foreach (var operation in runningOperations)
        {
            // Count processed files (added + skipped + errored)
            var processedFiles = operation.FilesAdded + operation.FilesSkipped + operation.FilesErrored;

            _logger.LogInformation(
                "Operation {OperationId}: Found={FilesFound}, Added={FilesAdded}, Skipped={FilesSkipped}, Errored={FilesErrored}, Processed={ProcessedFiles}",
                operation.Id, operation.FilesFound, operation.FilesAdded, operation.FilesSkipped, operation.FilesErrored, processedFiles);

            // Get the count of discovered files for this operation
            var discoveredFileCount = await db.DiscoveredGcodeFiles
                .Where(d => d.HarvestOperationId == operation.Id)
                .CountAsync(ct);

            _logger.LogInformation(
                "Operation {OperationId}: Found {DiscoveredFileCount} files in the DiscoveredGcodeFiles table",
                operation.Id, discoveredFileCount);

            if (processedFiles >= operation.FilesFound)
            {
                // All files have been processed
                operation.Status = GcodeHarvestStatus.Completed;
                operation.CompletedAt = DateTime.UtcNow;

                _logger.LogInformation(
                    "Marking operation {OperationId} as completed. Processed {ProcessedFiles}/{TotalFiles} files",
                    operation.Id, processedFiles, operation.FilesFound);

                await db.SaveChangesAsync(ct);
            }
        }
    }
}
