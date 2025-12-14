using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Gcode;
using Farm.Infrastructure.Repositories.Harvest;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.Models;
using Farm.Web.Api.Services.Printers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Services;

/// <summary>
/// Background service that processes harvest file jobs from the queue
/// </summary>
public partial class HarvestWorkerService(
    IHarvestQueue queue,
    IServiceScopeFactory scopeFactory,
    IUnifiedLoggingService logger,
    IHubContext<HarvestHub> harvestHub) : BackgroundService
{
    private readonly IHarvestQueue _queue = queue;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly SemaphoreSlim _workerSemaphore = new(MaxConcurrentWorkers, MaxConcurrentWorkers);
    private readonly IHubContext<HarvestHub> _harvestHub = harvestHub;
    private const int MaxConcurrentWorkers = 3; // Configurable

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation($"HarvestWorkerService started with {MaxConcurrentWorkers} concurrent workers", null, null);

        // List to track running tasks
        List<Task> runningTasks = new();

        try
        {
            await foreach (HarvestFileJob job in _queue.DequeueAsync(stoppingToken))
            {
                // Process jobs with limited concurrency
                Task processTask = Task.Run(async () =>
                {
                    await _workerSemaphore.WaitAsync(stoppingToken);
                    try
                    {
                        _logger.LogInformation($"Starting processing of file {job.FileName} for operation {job.OperationId}", null, null);
                        await ProcessFileJobAsync(job, stoppingToken);
                        _logger.LogInformation($"Completed processing of file {job.FileName} for operation {job.OperationId}", null, null);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error processing file {job.FileName} for operation {job.OperationId}", null, null);
                    }
                    finally
                    {
                        _ = _workerSemaphore.Release();
                    }
                }, stoppingToken);

                // Add to tracking list and clean up completed tasks
                runningTasks.Add(processTask);

                // Clean up completed tasks periodically
                _ = runningTasks.RemoveAll(t => t.IsCompleted);

                if (runningTasks.Count % 10 == 0)
                {
                    _logger.LogInformation($"Currently tracking {runningTasks.Count} active processing tasks", null, null);
                }
            }

            // Wait for all remaining tasks to complete
            _logger.LogInformation($"Queue enumeration complete. Waiting for {runningTasks.Count} remaining tasks to complete", null, null);
            await Task.WhenAll(runningTasks);
            _logger.LogInformation("All harvest processing tasks have completed", null, null);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation($"HarvestWorkerService stopping due to cancellation. {runningTasks.Count} tasks still running", null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"HarvestWorkerService encountered an error with {runningTasks.Count} tasks still running", null, null);
        }
    }

    private async Task ProcessFileJobAsync(HarvestFileJob job, CancellationToken ct)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IHarvestRepository harvestRepo = scope.ServiceProvider.GetRequiredService<IHarvestRepository>();
        IPrintersRepository printersRepo = scope.ServiceProvider.GetRequiredService<IPrintersRepository>();
        IGcodeRepository gcodeRepo = scope.ServiceProvider.GetRequiredService<IGcodeRepository>();
        IBackendClientFactory backendClientFactory = scope.ServiceProvider.GetRequiredService<IBackendClientFactory>();

        _logger.LogDebug($"Processing file job: {job}", null, null);

        try
        {
            // Check if operation is still active
            GcodeHarvestOperation? operation = await harvestRepo.GetOperationByIdAsync(job.OperationId, ct);

            if (operation == null)
            {
                _logger.LogWarning($"Operation {job.OperationId} not found for job {job.FileName}", null, null);
                return;
            }

            if (operation.Status == GcodeHarvestStatus.Cancelled)
            {
                _logger.LogDebug($"Skipping job {job.FileName} - operation {job.OperationId} was cancelled", null, null);
                return;
            }

            // Check if this file is already in the discovered files table
            HarvestDiscoveredFile? existingDiscoveredFile = await harvestRepo.GetDiscoveredFileByOperationAndFileNameAsync(job.OperationId, job.FilePath, ct);

            if (existingDiscoveredFile != null)
            {
                _logger.LogInformation($"File {job.FileName} already exists in discovered files table for operation {job.OperationId}", null, null);
                return;
            }

            // Get printer info
            Printer? printer = await printersRepo.FindByIdAsync(job.PrinterId, ct);
            if (printer == null)
            {
                _logger.LogWarning($"Printer {job.PrinterId} not found for job {job.FileName}", null, null);
                await RecordFileErrorAsync(harvestRepo, job.OperationId, job.FileName, "Printer not found", ct);
                return;
            }

            // Apply operation filters
            if (!ShouldProcessFile(job, operation))
            {
                _logger.LogDebug($"Skipping file {job.FileName} due to operation filters", null, null);
                await IncrementSkippedCountAsync(harvestRepo, operation, ct);
                return;
            }

            // Create discovered file record
            HarvestDiscoveredFile discoveredFile = new()
            {
                Id = Guid.NewGuid(),
                HarvestOperationId = job.OperationId,
                FilePath = job.FilePath,
                FileName = job.FileName,
                Size = job.FileSize,
                ModifiedAt = job.ModifiedAt.HasValue ? DateTime.SpecifyKind(job.ModifiedAt.Value, DateTimeKind.Utc) : null,

                // Optimization: Use metadata from API if available (avoids file download)
                ExtractedSlicerName = job.SlicerName,
                ExtractedSlicerVersion = job.SlicerVersion,
                ExtractedPrintTime = job.EstimatedTimeSeconds.HasValue ? job.EstimatedTimeSeconds.Value / 60 : null, // Convert seconds to minutes
                ExtractedFilamentLength = job.FilamentLengthMm,
                ExtractedNozzleDiameter = null, // Not available in Moonraker metadata
                ExtractedMaterial = null, // TODO: Parse from slicer settings if available
                // Convert thumbnail relative path to full URL for Moonraker
                ThumbnailUrl = !string.IsNullOrEmpty(job.ThumbnailRelativePath)
                    ? $"{job.ServerUrl}/server/files/gcodes/{job.ThumbnailRelativePath}"
                    : null
            };

            _logger.LogInformation($"Created discovered file record for {job.FileName} with ID {discoveredFile.Id}", null, null);

            // Determine if we need to download the file
            // We ONLY download if:
            // 1. Duplicate handling is enabled and we need to calculate hash, OR
            // 2. No metadata was provided by the API (fallback to extraction)
            bool needsDownload = !string.Equals(operation.DuplicateHandling, "skip", StringComparison.OrdinalIgnoreCase) ||
                                 string.IsNullOrEmpty(job.SlicerName);

            if (needsDownload)
            {
                // Download and process file
                PrinterBackend backend = (PrinterBackend)printer.Backend;
                using MemoryStream? fileContent = await DownloadFileAsync(backend, printer, job.FilePath, backendClientFactory);

                if (fileContent != null)
                {
                    _logger.LogInformation($"Successfully downloaded file {job.FileName} ({fileContent.Length} bytes)", null, null);

                    // Calculate hash
                    fileContent.Position = 0;
                    discoveredFile.FileHash = await CalculateFileHashAsync(fileContent);

                    // Check if already in library
                    GcodeFile? existingFile = await gcodeRepo.FindByHashAsync(discoveredFile.FileHash, ct);

                    if (existingFile != null)
                    {
                        string handling = operation.DuplicateHandling ?? "skip";
                        if (string.Equals(handling, "overwrite", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation($"Overwriting duplicate file {job.FileName} (Existing ID {existingFile.Id}) per policy", null, null);
                            // Treat as added (new metadata snapshot) but reference existing file id
                            fileContent.Position = 0;
                            GcodeMetadataDto overwriteMeta = await ExtractMetadataAsync(fileContent);
                            ApplyMetadataToDiscoveredFile(discoveredFile, overwriteMeta);
                            discoveredFile.AlreadyInLibrary = false;
                            // FilesAdded will be incremented during import phase, not discovery
                        }
                        else if (string.Equals(handling, "rename", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation($"Renaming duplicate file {job.FileName} per policy", null, null);
                            // Generate a new unique name with -copy suffix (in discovered scope)
                            string baseName = Path.GetFileNameWithoutExtension(discoveredFile.FileName);
                            string ext = Path.GetExtension(discoveredFile.FileName);
                            int copyIndex = 1;
                            string candidate;
                            do
                            {
                                candidate = $"{baseName}-copy{copyIndex}{ext}";
                                copyIndex++;
                            } while (await harvestRepo.DiscoveredFileExistsByNameAsync(operation.Id, candidate, ct));
                            discoveredFile.FileName = candidate;
                            fileContent.Position = 0;
                            GcodeMetadataDto renameMeta = await ExtractMetadataAsync(fileContent);
                            ApplyMetadataToDiscoveredFile(discoveredFile, renameMeta);
                            // FilesAdded will be incremented during import phase, not discovery
                        }
                        else
                        {
                            // skip
                            _logger.LogInformation($"Skipping duplicate file {job.FileName} (Existing ID {existingFile.Id}) per policy", null, null);
                            discoveredFile.AlreadyInLibrary = true;
                            await IncrementSkippedCountAsync(harvestRepo, operation, ct);
                        }
                    }
                    else
                    {
                        // Extract metadata from file content (fallback if API didn't provide it)
                        if (string.IsNullOrEmpty(job.SlicerName))
                        {
                            fileContent.Position = 0;
                            GcodeMetadataDto metadata = await ExtractMetadataAsync(fileContent);
                            ApplyMetadataToDiscoveredFile(discoveredFile, metadata);
                            _logger.LogInformation($"Extracted metadata from file {job.FileName}: Slicer={discoveredFile.ExtractedSlicerName ?? "Unknown"}, Material={discoveredFile.ExtractedMaterial ?? "Unknown"}", null, null);
                        }
                        // FilesAdded will be incremented during import phase, not discovery
                    }

                    operation.TotalBytesProcessed += job.FileSize;
                }
                else
                {
                    _logger.LogWarning($"Failed to download file {job.FileName}", null, null);
                    await IncrementErrorCountAsync(harvestRepo, operation, ct);
                }
            }
            else
            {
                // Optimization: No download needed! We have metadata from API
                _logger.LogInformation($"Skipping download for {job.FileName} - using metadata from API (Slicer: {job.SlicerName ?? "Unknown"})", null, null);
                // FilesAdded will be incremented during import phase, not discovery
                // Note: TotalBytesProcessed not incremented since we didn't download
            }

            // Save discovered file
            _logger.LogInformation($"Saving discovered file {job.FileName} to database", null, null);
            await harvestRepo.AddDiscoveredFileAsync(discoveredFile, ct);
            await harvestRepo.SaveChangesAsync(ct);

            // Emit per-file progress event with discovered file info
            await _harvestHub.Clients.Group($"harvest-{job.OperationId}").SendAsync("harvestfilediscovered", new
            {
                operationId = job.OperationId,
                fileId = discoveredFile.Id,
                fileName = discoveredFile.FileName,
                filePath = discoveredFile.FilePath,
                fileSize = discoveredFile.Size,
                status = discoveredFile.AlreadyInLibrary ? "skipped" : "added",
                thumbnailUrl = discoveredFile.ThumbnailUrl,
                extractedSlicer = discoveredFile.ExtractedSlicerName,
                extractedMaterial = discoveredFile.ExtractedMaterial
            }, ct);

            // Emit operation progress update for real-time UI updates
            int totalProcessed = operation.FilesAdded + operation.FilesSkipped + operation.FilesErrored;
            await _harvestHub.Clients.Group($"harvest-{job.OperationId}").SendAsync("harvestoperationprogress", new
            {
                operationId = job.OperationId,
                filesFound = operation.FilesFound,
                filesProcessed = totalProcessed,
                filesAdded = operation.FilesAdded,
                filesSkipped = operation.FilesSkipped,
                filesErrored = operation.FilesErrored
            }, ct);

            _logger.LogInformation($"Successfully processed file {job.FileName} for operation {job.OperationId}", null, null);

            // Verify the file was actually saved
            HarvestDiscoveredFile? savedFile = await harvestRepo.GetDiscoveredFileByIdAsync(discoveredFile.Id, job.OperationId, ct);

            if (savedFile != null)
            {
                _logger.LogInformation($"Verified file {job.FileName} was saved with ID {savedFile.Id}", null, null);
            }
            else
            {
                _logger.LogWarning($"File {job.FileName} with ID {discoveredFile.Id} was NOT found in database after save", null, null);
            }

            // Check if operation should be marked as complete
            await CheckAndCompleteOperationAsync(harvestRepo, job.OperationId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to process file job {job.FileName} for operation {job.OperationId}", null, null);
            await RecordFileErrorAsync(harvestRepo, job.OperationId, job.FileName, ex.Message, ct);
            // Emit error event for this file
            await _harvestHub.Clients.Group($"harvest-{job.OperationId}").SendAsync("harvestfilediscovered", new
            {
                operationId = job.OperationId,
                fileId = Guid.NewGuid(),
                fileName = job.FileName,
                filePath = job.FilePath,
                fileSize = job.FileSize,
                status = "error",
                error = ex.Message
                // thumbnailUrl, extractedSlicer, extractedMaterial omitted if not available
            }, ct);

            // Check if operation should be marked as complete even after error
            await CheckAndCompleteOperationAsync(harvestRepo, job.OperationId, ct);
        }
    }

    private static bool ShouldProcessFile(HarvestFileJob job, GcodeHarvestOperation operation)
    {
        // Check size limit
        if (operation.MaxFileSizeBytes.HasValue && job.FileSize > operation.MaxFileSizeBytes.Value)
        {
            return false;
        }

        // Check modification date
        if (operation.ModifiedAfter.HasValue && job.ModifiedAt.HasValue &&
            job.ModifiedAt.Value < operation.ModifiedAfter.Value)
        {
            return false;
        }

        return true;
    }

    private async Task<MemoryStream?> DownloadFileAsync(
        PrinterBackend backend,
        Printer printer,
        string filePath,
        IBackendClientFactory backendClientFactory)
    {
        try
        {
            IBackendClient client = backendClientFactory.GetBackendClient(backend);
            
            // Only Moonraker currently supports file downloads
            if (client is ISupportsFileDownload downloadClient)
            {
                string backendUrl = backend == PrinterBackend.Moonraker
                    ? $"{printer.ServerUrl}:{printer.FrontendPort}"
                    : printer.BackendUrl;
                
                byte[]? bytes = await downloadClient.DownloadFileAsync(backendUrl, filePath);
                if (bytes != null)
                {
                    return new MemoryStream(bytes);
                }

                _logger.LogWarning($"Failed to download file {filePath} from {backend} at {backendUrl}", null, null);
                return null;
            }

            _logger.LogWarning($"Backend {backend} does not support file downloads for {filePath}", null, null);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to download file {filePath} from {backend}", null, null);
            return null;
        }
    }

    private static async Task<string> CalculateFileHashAsync(Stream stream)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] hashBytes = await sha256.ComputeHashAsync(stream);
        return ToHexLower(hashBytes);
    }

    private static string ToHexLower(byte[] hash)
        => Convert.ToHexString(hash).ToLowerInvariant();

    private static async Task<GcodeMetadataDto> ExtractMetadataAsync(Stream stream)
    {
        GcodeMetadataDto metadata = new();

        stream.Position = 0;
        using StreamReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

        int linesRead = 0;
        int maxLinesToRead = 100; // Limit header scanning

        while (linesRead < maxLinesToRead && await reader.ReadLineAsync() is { } line)
        {
            linesRead++;

            if (!line.StartsWith(';') && linesRead > 50)
            {
                break;
            }

            metadata = ExtractMetadataFromLine(line, metadata);
        }

        return metadata;
    }

    private static GcodeMetadataDto ExtractMetadataFromLine(string line, GcodeMetadataDto metadata)
    {
        if (!line.StartsWith(';'))
        {
            return metadata;
        }

        string content = line[1..].Trim();

        // PrusaSlicer patterns
        if (content.StartsWith("Generated by PrusaSlicer"))
        {
            Match versionMatch = MyRegex().Match(content);
            if (versionMatch.Success)
            {
                metadata = metadata with { SlicerName = "PrusaSlicer", SlicerVersion = versionMatch.Groups[1].Value };
            }
        }

        // Cura patterns
        if (content.StartsWith("Generated with Cura"))
        {
            Match versionMatch = MyRegex1().Match(content);
            if (versionMatch.Success)
            {
                metadata = metadata with { SlicerName = "Cura", SlicerVersion = versionMatch.Groups[1].Value };
            }
        }

        // Extract common parameters (simplified for now)
        if (content.Contains("printing time", StringComparison.OrdinalIgnoreCase) && content.Contains('h') && content.Contains('m'))
        {
            Match timeMatch = Regex.Match(content, @"(\d+)h (\d+)m");
            if (timeMatch.Success)
            {
                int hours = int.Parse(timeMatch.Groups[1].Value);
                int minutes = int.Parse(timeMatch.Groups[2].Value);
                metadata = metadata with { PrintTimeMinutes = hours * 60 + minutes };
            }
        }

        return metadata;
    }

    private static void ApplyMetadataToDiscoveredFile(HarvestDiscoveredFile discoveredFile, GcodeMetadataDto metadata)
    {
        discoveredFile.ExtractedSlicerName = metadata.SlicerName;
        discoveredFile.ExtractedSlicerVersion = metadata.SlicerVersion;
        discoveredFile.ExtractedPrintTime = metadata.PrintTimeMinutes;
        discoveredFile.ExtractedFilamentLength = metadata.FilamentLengthMm;
        discoveredFile.ExtractedNozzleDiameter = metadata.NozzleDiameter;
        discoveredFile.ExtractedMaterial = metadata.Material;
        // No ExtractedLayerHeight or ExtractedInfill fields on HarvestDiscoveredFile
    }

    private static async Task IncrementSkippedCountAsync(IHarvestRepository harvestRepo, GcodeHarvestOperation operation, CancellationToken ct)
    {
        operation.FilesSkipped++;
        await harvestRepo.SaveChangesAsync(ct);
    }

    // Note: FilesAdded is now incremented during import phase in GcodeHarvestService.ImportSelectedFilesAsync
    // This ensures the counter only reflects files actually imported to the library, not just discovered

    private static async Task IncrementErrorCountAsync(IHarvestRepository harvestRepo, GcodeHarvestOperation operation, CancellationToken ct)
    {
        operation.FilesErrored++;
        await harvestRepo.SaveChangesAsync(ct);
    }

    private static async Task RecordFileErrorAsync(IHarvestRepository harvestRepo, Guid operationId, string fileName, string errorMessage, CancellationToken ct)
    {
        _ = fileName;
        _ = errorMessage;
        GcodeHarvestOperation? operation = await harvestRepo.GetOperationByIdAsync(operationId, ct);
        if (operation != null)
        {
            operation.FilesErrored++;
            await harvestRepo.SaveChangesAsync(ct);
        }
    }

    private async Task CheckAndCompleteOperationAsync(IHarvestRepository harvestRepo, Guid operationId, CancellationToken ct)
    {
        // Get the operation
        GcodeHarvestOperation? operation = await harvestRepo.GetOperationByIdAsync(operationId, ct);

        if (operation == null || operation.Status != GcodeHarvestStatus.Running)
        {
            return; // Operation doesn't exist or is not running
        }

        // Check if we've processed all expected files
        // FilesFound is set during discovery phase and represents all files after filtering
        // FilesAdded + FilesSkipped + FilesErrored tracks how many we've processed
        int totalProcessed = operation.FilesAdded + operation.FilesSkipped + operation.FilesErrored;

        _logger.LogDebug($"Operation {operationId}: Expected={operation.FilesFound}, Processed={totalProcessed} (Added={operation.FilesAdded}, Skipped={operation.FilesSkipped}, Errored={operation.FilesErrored})", null, null);

        // Operation is complete when we've processed all files that were discovered
        // If FilesFound > 0, we should have found files and processed them all
        // If FilesFound == 0, operation already completed during discovery phase
        if (operation.FilesFound > 0 && totalProcessed >= operation.FilesFound)
        {
            // All files have been processed, mark operation as complete
            operation.Status = GcodeHarvestStatus.Completed;
            operation.CompletedAt = DateTime.UtcNow;
            await harvestRepo.SaveChangesAsync(ct);

            _logger.LogInformation($"Operation {operationId} completed: {operation.FilesAdded} added, {operation.FilesSkipped} skipped, {operation.FilesErrored} errors", null, null);

            // Emit completion event via SignalR
            await _harvestHub.Clients.Group($"harvest-{operationId}").SendAsync("HarvestOperationCompleted", new
            {
                operationId,
                status = "Completed",
                filesAdded = operation.FilesAdded,
                filesSkipped = operation.FilesSkipped,
                filesErrored = operation.FilesErrored,
                completedAt = operation.CompletedAt
            }, ct);
        }
    }

    // Helper overload intentionally removed to avoid duplicate definitions and recursive wrapper

    public override void Dispose()
    {
        _workerSemaphore.Dispose();
        base.Dispose();
    }

    [GeneratedRegex(@"PrusaSlicer (\S+)")]
    private static partial Regex MyRegex();
    [GeneratedRegex(@"Cura_SteamEngine (\S+)")]
    private static partial Regex MyRegex1();
}
