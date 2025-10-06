using Farm.Infrastructure.Telemetry;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.Models;
using Farm.Web.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Services;

/// <summary>
/// Background service that processes harvest file jobs from the queue
/// </summary>
public partial class HarvestWorkerService(
    IHarvestQueue queue,
    IServiceProvider serviceProvider,
    IUnifiedLoggingService logger,
    IHubContext<HarvestHub> harvestHub) : BackgroundService
{
    private readonly IHarvestQueue _queue = queue;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly SemaphoreSlim _workerSemaphore = new SemaphoreSlim(MaxConcurrentWorkers, MaxConcurrentWorkers);
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
                        _workerSemaphore.Release();
                    }
                }, stoppingToken);

                // Add to tracking list and clean up completed tasks
                runningTasks.Add(processTask);

                // Clean up completed tasks periodically
                runningTasks.RemoveAll(t => t.IsCompleted);

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
        using IServiceScope scope = _serviceProvider.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IMoonrakerClient moonraker = scope.ServiceProvider.GetRequiredService<IMoonrakerClient>();
        IPrusaLinkClient prusa = scope.ServiceProvider.GetRequiredService<IPrusaLinkClient>();
        ISdcpClient sdcp = scope.ServiceProvider.GetRequiredService<ISdcpClient>();

        _logger.LogDebug($"Processing file job: {job}", null, null);

        try
        {
            // Check if operation is still active
            GcodeHarvestOperation? operation = await db.GcodeHarvestOperations
                .FirstOrDefaultAsync(o => o.Id == job.OperationId, ct);

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
            HarvestDiscoveredFile? existingDiscoveredFile = await db.HarvestDiscoveredFiles
                .FirstOrDefaultAsync(d => d.HarvestOperationId == job.OperationId &&
                                          d.FilePath == job.FilePath, ct);

            if (existingDiscoveredFile != null)
            {
                _logger.LogInformation($"File {job.FileName} already exists in discovered files table for operation {job.OperationId}", null, null);
                return;
            }

            // Get printer info
            Printer? printer = await db.Printers.FirstOrDefaultAsync(p => p.Id == job.PrinterId, ct);
            if (printer == null)
            {
                _logger.LogWarning($"Printer {job.PrinterId} not found for job {job.FileName}", null, null);
                await RecordFileErrorAsync(db, job.OperationId, job.FileName, "Printer not found");
                return;
            }

            // Apply operation filters
            if (!ShouldProcessFile(job, operation))
            {
                _logger.LogDebug($"Skipping file {job.FileName} due to operation filters", null, null);
                await IncrementSkippedCountAsync(db, operation);
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
            bool needsDownload = operation.DuplicateHandling?.ToLowerInvariant() != "skip" ||
                                 string.IsNullOrEmpty(job.SlicerName);

            if (needsDownload)
            {
                // Download and process file
                PrinterBackend backend = (PrinterBackend)printer.Backend;
                using MemoryStream? fileContent = await DownloadFileAsync(backend, printer, job.FilePath, moonraker, prusa, sdcp);

                if (fileContent != null)
                {
                    _logger.LogInformation($"Successfully downloaded file {job.FileName} ({fileContent.Length} bytes)", null, null);

                    // Calculate hash
                    fileContent.Position = 0;
                    discoveredFile.FileHash = await CalculateFileHashAsync(fileContent);

                    // Check if already in library
                    GcodeFile? existingFile = await db.GcodeFiles
                        .FirstOrDefaultAsync(f => f.FileHash == discoveredFile.FileHash, ct);

                    if (existingFile != null)
                    {
                        string handling = operation.DuplicateHandling?.ToLowerInvariant() ?? "skip";
                        switch (handling)
                        {
                            case "overwrite":
                                _logger.LogInformation($"Overwriting duplicate file {job.FileName} (Existing ID {existingFile.Id}) per policy", null, null);
                                // Treat as added (new metadata snapshot) but reference existing file id
                                fileContent.Position = 0;
                                GcodeMetadataDto overwriteMeta = await ExtractMetadataAsync(fileContent);
                                ApplyMetadataToDiscoveredFile(discoveredFile, overwriteMeta);
                                discoveredFile.AlreadyInLibrary = false;
                                await IncrementAddedCountAsync(db, operation);
                                break;
                            case "rename":
                                _logger.LogInformation($"Renaming duplicate file {job.FileName} per policy", null, null);
                                // Generate a new unique name with -copy suffix (in discovered scope)
                                string baseName = System.IO.Path.GetFileNameWithoutExtension(discoveredFile.FileName);
                                string ext = System.IO.Path.GetExtension(discoveredFile.FileName);
                                int copyIndex = 1;
                                string candidate;
                                do
                                {
                                    candidate = $"{baseName}-copy{copyIndex}{ext}";
                                    copyIndex++;
                                } while (await db.HarvestDiscoveredFiles.AnyAsync(d => d.HarvestOperationId == operation.Id && d.FileName == candidate, ct));
                                discoveredFile.FileName = candidate;
                                fileContent.Position = 0;
                                GcodeMetadataDto renameMeta = await ExtractMetadataAsync(fileContent);
                                ApplyMetadataToDiscoveredFile(discoveredFile, renameMeta);
                                await IncrementAddedCountAsync(db, operation);
                                break;
                            default: // skip
                                _logger.LogInformation($"Skipping duplicate file {job.FileName} (Existing ID {existingFile.Id}) per policy", null, null);
                                discoveredFile.AlreadyInLibrary = true;
                                await IncrementSkippedCountAsync(db, operation);
                                break;
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
                        await IncrementAddedCountAsync(db, operation);
                    }

                    operation.TotalBytesProcessed += job.FileSize;
                }
                else
                {
                    _logger.LogWarning($"Failed to download file {job.FileName}", null, null);
                    await IncrementErrorCountAsync(db, operation);
                }
            }
            else
            {
                // Optimization: No download needed! We have metadata from API
                _logger.LogInformation($"Skipping download for {job.FileName} - using metadata from API (Slicer: {job.SlicerName ?? "Unknown"})", null, null);
                await IncrementAddedCountAsync(db, operation);
                // Note: TotalBytesProcessed not incremented since we didn't download
            }

            // Save discovered file
            _logger.LogInformation($"Saving discovered file {job.FileName} to database", null, null);
            db.HarvestDiscoveredFiles.Add(discoveredFile);
            await db.SaveChangesAsync(ct);

            // Emit per-file progress event with discovered file info
            await _harvestHub.Clients.Group($"harvest-{job.OperationId}").SendAsync("HarvestFileDiscovered", new
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

            _logger.LogInformation($"Successfully processed file {job.FileName} for operation {job.OperationId}", null, null);

            // Verify the file was actually saved
            HarvestDiscoveredFile? savedFile = await db.HarvestDiscoveredFiles
                .FirstOrDefaultAsync(d => d.Id == discoveredFile.Id, ct);

            if (savedFile != null)
            {
                _logger.LogInformation($"Verified file {job.FileName} was saved with ID {savedFile.Id}", null, null);
            }
            else
            {
                _logger.LogWarning($"File {job.FileName} with ID {discoveredFile.Id} was NOT found in database after save", null, null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to process file job {job.FileName} for operation {job.OperationId}", null, null);
            await RecordFileErrorAsync(db, job.OperationId, job.FileName, ex.Message);
            // Emit error event for this file
            await _harvestHub.Clients.Group($"harvest-{job.OperationId}").SendAsync("HarvestFileDiscovered", new
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
        IMoonrakerClient moonraker,
        IPrusaLinkClient prusa,
        ISdcpClient sdcp)
    {
        try
        {
            return backend switch
            {
                PrinterBackend.Moonraker => await DownloadMoonrakerFileAsync(printer.ServerUrl, filePath, moonraker),
                PrinterBackend.PrusaLink => await DownloadPrusaLinkFileAsync(printer.ServerUrl, printer.ApiKey, filePath, prusa),
                PrinterBackend.SDCP => await DownloadSdcpFileAsync(printer.ServerUrl, filePath, sdcp),
                _ => null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to download file {filePath} from {printer.ServerUrl}", null, null);
            return null;
        }
    }

    private async Task<MemoryStream?> DownloadMoonrakerFileAsync(string serverUrl, string filePath, IMoonrakerClient moonraker)
    {
        try
        {
            byte[]? bytes = await moonraker.DownloadFileAsync(serverUrl, filePath);
            if (bytes != null)
            {
                return new MemoryStream(bytes);
            }

            _logger.LogWarning($"Failed to download file {filePath} from Moonraker at {serverUrl}", null, null);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to download file {filePath} from Moonraker at {serverUrl}", null, null);
            return null;
        }
    }

    private async Task<MemoryStream?> DownloadPrusaLinkFileAsync(string serverUrl, string filePath)
    {
        try
        {
            // PrusaLink file download implementation would go here
            _logger.LogInformation($"PrusaLink file download not yet implemented for {filePath} at {serverUrl}", null, null);
            await Task.Delay(1); // Prevent compiler warning
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to download file {filePath} from PrusaLink at {serverUrl}", null, null);
            return null;
        }
    }

    // Wrapper to satisfy call sites expecting apiKey and client parameters (currently unused)
#pragma warning disable S1172 // Remove this unused method parameter
    private Task<MemoryStream?> DownloadPrusaLinkFileAsync(string serverUrl, string? apiKey, string filePath, IPrusaLinkClient prusa)
        => DownloadPrusaLinkFileAsync(serverUrl, filePath);
#pragma warning restore S1172

    // Note: No wrapper overload needed; use the primary method above.

    private async Task<MemoryStream?> DownloadSdcpFileAsync(string serverUrl, string filePath)
    {
        try
        {
            // SDCP file download implementation would go here
            _logger.LogInformation($"SDCP file download not yet implemented for {filePath} at {serverUrl}", null, null);
            await Task.Delay(1); // Prevent compiler warning
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to download file {filePath} from SDCP at {serverUrl}", null, null);
            return null;
        }
    }

    // Wrapper to satisfy call sites expecting a client parameter (currently unused)
#pragma warning disable S1172 // Remove this unused method parameter
    private Task<MemoryStream?> DownloadSdcpFileAsync(string serverUrl, string filePath, ISdcpClient sdcp)
        => DownloadSdcpFileAsync(serverUrl, filePath);
#pragma warning restore S1172

    // Note: No wrapper overload needed; use the primary method above.

    private static async Task<string> CalculateFileHashAsync(Stream stream)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] hashBytes = await sha256.ComputeHashAsync(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

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

        string content = line.Substring(1).Trim();

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
            Match versionMatch = System.Text.RegularExpressions.Regex.Match(content, @"Cura_SteamEngine (\S+)");
            if (versionMatch.Success)
            {
                metadata = metadata with { SlicerName = "Cura", SlicerVersion = versionMatch.Groups[1].Value };
            }
        }

        // Extract common parameters (simplified for now)
        if (content.Contains("printing time") && content.Contains('h') && content.Contains('m'))
        {
            Match timeMatch = System.Text.RegularExpressions.Regex.Match(content, @"(\d+)h (\d+)m");
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

    private static async Task IncrementSkippedCountAsync(AppDbContext db, GcodeHarvestOperation operation)
    {
        operation.FilesSkipped++;
        await db.SaveChangesAsync();
    }

    private static async Task IncrementAddedCountAsync(AppDbContext db, GcodeHarvestOperation operation)
    {
        operation.FilesAdded++;
        await db.SaveChangesAsync();
    }

    private static async Task IncrementErrorCountAsync(AppDbContext db, GcodeHarvestOperation operation)
    {
        operation.FilesErrored++;
        await db.SaveChangesAsync();
    }

    private static async Task RecordFileErrorAsync(AppDbContext db, Guid operationId, string fileName, string errorMessage)
    {
        _ = fileName;
        _ = errorMessage;
        GcodeHarvestOperation? operation = await db.GcodeHarvestOperations.FirstOrDefaultAsync(o => o.Id == operationId);
        if (operation != null)
        {
            operation.FilesErrored++;
            await db.SaveChangesAsync();
        }
    }

    // Helper overload intentionally removed to avoid duplicate definitions and recursive wrapper

    public override void Dispose()
    {
        _workerSemaphore.Dispose();
        base.Dispose();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"PrusaSlicer (\S+)")]
    private static partial System.Text.RegularExpressions.Regex MyRegex();
}
