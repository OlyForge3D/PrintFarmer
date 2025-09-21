using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
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
public partial class HarvestWorkerService : BackgroundService
{
    private readonly IHarvestQueue _queue;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<HarvestWorkerService> _logger;
    private readonly SemaphoreSlim _workerSemaphore;
    private readonly IHubContext<HarvestHub> _harvestHub;
    private const int MaxConcurrentWorkers = 3; // Configurable

    public HarvestWorkerService(
        IHarvestQueue queue,
        IServiceProvider serviceProvider,
        ILogger<HarvestWorkerService> logger,
        IHubContext<HarvestHub> harvestHub)
    {
        _queue = queue;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _harvestHub = harvestHub;
        _workerSemaphore = new SemaphoreSlim(MaxConcurrentWorkers, MaxConcurrentWorkers);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HarvestWorkerService started with {MaxWorkers} concurrent workers", MaxConcurrentWorkers);

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
                        _logger.LogInformation("Starting processing of file {FileName} for operation {OperationId}",
                            job.FileName, job.OperationId);
                        await ProcessFileJobAsync(job, stoppingToken);
                        _logger.LogInformation("Completed processing of file {FileName} for operation {OperationId}",
                            job.FileName, job.OperationId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing file {FileName} for operation {OperationId}",
                            job.FileName, job.OperationId);
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
                    _logger.LogInformation("Currently tracking {TaskCount} active processing tasks", runningTasks.Count);
                }
            }

            // Wait for all remaining tasks to complete
            _logger.LogInformation("Queue enumeration complete. Waiting for {TaskCount} remaining tasks to complete",
                runningTasks.Count);
            await Task.WhenAll(runningTasks);
            _logger.LogInformation("All harvest processing tasks have completed");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("HarvestWorkerService stopping due to cancellation. {TaskCount} tasks still running",
                runningTasks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HarvestWorkerService encountered an error with {TaskCount} tasks still running",
                runningTasks.Count);
        }
    }

    private async Task ProcessFileJobAsync(HarvestFileJob job, CancellationToken ct)
    {
        using IServiceScope scope = _serviceProvider.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IMoonrakerClient moonraker = scope.ServiceProvider.GetRequiredService<IMoonrakerClient>();
        IPrusaLinkClient prusa = scope.ServiceProvider.GetRequiredService<IPrusaLinkClient>();
        ISdcpClient sdcp = scope.ServiceProvider.GetRequiredService<ISdcpClient>();

        _logger.LogDebug("Processing file job: {Job}", job);

        try
        {
            // Check if operation is still active
            GcodeHarvestOperation? operation = await db.GcodeHarvestOperations
                .FirstOrDefaultAsync(o => o.Id == job.OperationId, ct);

            if (operation == null)
            {
                _logger.LogWarning("Operation {OperationId} not found for job {FileName}",
                    job.OperationId, job.FileName);
                return;
            }

            if (operation.Status == GcodeHarvestStatus.Cancelled)
            {
                _logger.LogDebug("Skipping job {FileName} - operation {OperationId} was cancelled",
                    job.FileName, job.OperationId);
                return;
            }

            // Check if this file is already in the discovered files table
            HarvestDiscoveredFile? existingDiscoveredFile = await db.HarvestDiscoveredFiles
                .FirstOrDefaultAsync(d => d.HarvestOperationId == job.OperationId &&
                                          d.FilePath == job.FilePath, ct);

            if (existingDiscoveredFile != null)
            {
                _logger.LogInformation("File {FileName} already exists in discovered files table for operation {OperationId}",
                    job.FileName, job.OperationId);
                return;
            }

            // Get printer info
            Printer? printer = await db.Printers.FirstOrDefaultAsync(p => p.Id == job.PrinterId, ct);
            if (printer == null)
            {
                _logger.LogWarning("Printer {PrinterId} not found for job {FileName}",
                    job.PrinterId, job.FileName);
                await RecordFileErrorAsync(db, job.OperationId, job.FileName, "Printer not found");
                return;
            }

            // Apply operation filters
            if (!ShouldProcessFile(job, operation))
            {
                _logger.LogDebug("Skipping file {FileName} due to operation filters", job.FileName);
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
                ModifiedAt = job.ModifiedAt,
                // Set other fields as needed
            };

            _logger.LogInformation("Created discovered file record for {FileName} with ID {FileId}",
                job.FileName, discoveredFile.Id);

            // Download and process file
            PrinterBackend backend = (PrinterBackend)printer.Backend;
            using MemoryStream? fileContent = await DownloadFileAsync(backend, printer, job.FilePath, moonraker, prusa, sdcp);

            if (fileContent != null)
            {
                _logger.LogInformation("Successfully downloaded file {FileName} ({Size} bytes)",
                    job.FileName, fileContent.Length);

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
                            _logger.LogInformation("Overwriting duplicate file {FileName} (Existing ID {ExistingId}) per policy", job.FileName, existingFile.Id);
                            // Treat as added (new metadata snapshot) but reference existing file id
                            fileContent.Position = 0;
                            GcodeMetadataDto overwriteMeta = await ExtractMetadataAsync(fileContent);
                            ApplyMetadataToDiscoveredFile(discoveredFile, overwriteMeta);
                            discoveredFile.AlreadyInLibrary = false;
                            await IncrementAddedCountAsync(db, operation);
                            break;
                        case "rename":
                            _logger.LogInformation("Renaming duplicate file {FileName} per policy", job.FileName);
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
                            _logger.LogInformation("Skipping duplicate file {FileName} (Existing ID {ExistingId}) per policy", job.FileName, existingFile.Id);
                            discoveredFile.AlreadyInLibrary = true;
                            await IncrementSkippedCountAsync(db, operation);
                            break;
                    }
                }
                else
                {
                    // Extract metadata
                    fileContent.Position = 0;
                    GcodeMetadataDto metadata = await ExtractMetadataAsync(fileContent);
                    ApplyMetadataToDiscoveredFile(discoveredFile, metadata);
                    await IncrementAddedCountAsync(db, operation);

                    _logger.LogInformation("Extracted metadata from file {FileName}: Slicer={Slicer}, Material={Material}",
                        job.FileName, discoveredFile.ExtractedSlicerName ?? "Unknown", discoveredFile.ExtractedMaterial ?? "Unknown");
                }

                operation.TotalBytesProcessed += job.FileSize;
            }
            else
            {
                _logger.LogWarning("Failed to download file {FileName}", job.FileName);
                // No ProcessingFailed or ErrorMessage fields on HarvestDiscoveredFile
                await IncrementErrorCountAsync(db, operation);
            }

            // Save discovered file
            _logger.LogInformation("Saving discovered file {FileName} to database", job.FileName);
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
                // error property removed: HarvestDiscoveredFile does not have error field
                // thumbnailUrl intentionally omitted if not available
                extractedSlicer = discoveredFile.ExtractedSlicerName,
                extractedMaterial = discoveredFile.ExtractedMaterial
            }, ct);

            _logger.LogInformation("Successfully processed file {FileName} for operation {OperationId}",
                job.FileName, job.OperationId);

            // Verify the file was actually saved
            HarvestDiscoveredFile? savedFile = await db.HarvestDiscoveredFiles
                .FirstOrDefaultAsync(d => d.Id == discoveredFile.Id, ct);

            if (savedFile != null)
            {
                _logger.LogInformation("Verified file {FileName} was saved with ID {FileId}", job.FileName, savedFile.Id);
            }
            else
            {
                _logger.LogWarning("File {FileName} with ID {FileId} was NOT found in database after save",
                    job.FileName, discoveredFile.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process file job {FileName} for operation {OperationId}",
                job.FileName, job.OperationId);
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
            _logger.LogWarning(ex, "Failed to download file {FilePath} from {ServerUrl}", filePath, printer.ServerUrl);
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

            _logger.LogWarning("Failed to download file {FilePath} from Moonraker at {ServerUrl}", filePath, serverUrl);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download file {FilePath} from Moonraker at {ServerUrl}", filePath, serverUrl);
            return null;
        }
    }

    private async Task<MemoryStream?> DownloadPrusaLinkFileAsync(string serverUrl, string filePath)
    {
        try
        {
            // PrusaLink file download implementation would go here
            _logger.LogInformation("PrusaLink file download not yet implemented for {FilePath} at {ServerUrl}", filePath, serverUrl);
            await Task.Delay(1); // Prevent compiler warning
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download file {FilePath} from PrusaLink at {ServerUrl}", filePath, serverUrl);
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
            _logger.LogInformation("SDCP file download not yet implemented for {FilePath} at {ServerUrl}", filePath, serverUrl);
            await Task.Delay(1); // Prevent compiler warning
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download file {FilePath} from SDCP at {ServerUrl}", filePath, serverUrl);
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
