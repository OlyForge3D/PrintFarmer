using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Harvest;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services.Gcode;

/// <summary>
/// Background service to monitor harvest operations and mark them as completed
/// when all files are processed
/// </summary>
public class HarvestCompletionService(
    IServiceProvider serviceProvider,
    ILogger<HarvestCompletionService> logger) : BackgroundService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ILogger<HarvestCompletionService> _logger = logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation($"HarvestCompletionService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckForCompletedOperationsAsync(stoppingToken);
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation($"HarvestCompletionService stopping due to cancellation");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in HarvestCompletionService");
                await Task.Delay(CheckInterval, stoppingToken);
            }
        }
    }

    private async Task CheckForCompletedOperationsAsync(CancellationToken ct)
    {
        // Use an async scope when awaiting EF Core calls to ensure proper async disposal
        await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await ProcessOperationsAsync(unitOfWork, ct);
    }

    /// <summary>
    /// Testable hook that processes a batch of operations using an already-resolved Unit of Work.
    /// </summary>
    /// <param name="unitOfWork">The Unit of Work instance for database operations.</param>
    /// <param name="ct">Cancellation token for the async operation.</param>
    internal async Task ProcessOperationsAsync(IUnitOfWork unitOfWork, CancellationToken ct)
    {
        // Find running operations that might be completed
        List<GcodeHarvestOperation> runningOperations = await unitOfWork.HarvestOperations.GetRunningOperationsWithFilesFoundAsync(ct);

        _logger.LogInformation("Found {RunningOperationsCount} running harvest operations to check", runningOperations.Count);

        foreach (GcodeHarvestOperation? operation in runningOperations)
        {
            // Count processed files (added + skipped + errored)
            int processedFiles = operation.FilesAdded + operation.FilesSkipped + operation.FilesErrored;

            _logger.LogInformation("Operation {OperationId}: Found={OperationFilesFound}, Added={OperationFilesAdded}, Skipped={OperationFilesSkipped}, Errored={OperationFilesErrored}, Processed={ProcessedFiles}", operation.Id, operation.FilesFound, operation.FilesAdded, operation.FilesSkipped, operation.FilesErrored, processedFiles);

            // Get the count of discovered files for this operation
            int discoveredFileCount = await unitOfWork.HarvestOperations.GetDiscoveredFilesCountAsync(operation.Id, ct);

            _logger.LogInformation("Operation {OperationId}: Found {DiscoveredFileCount} files in the DiscoveredGcodeFiles table", operation.Id, discoveredFileCount);

            if (processedFiles >= operation.FilesFound)
            {
                // All files have been processed
                operation.Status = GcodeHarvestStatus.Completed;
                operation.CompletedAt = DateTime.UtcNow;

                _logger.LogInformation("Marking operation {OperationId} as completed. Processed {ProcessedFiles}/{OperationFilesFound} files", operation.Id, processedFiles, operation.FilesFound);

                await unitOfWork.SaveChangesAsync(ct);
            }
        }
    }
}
