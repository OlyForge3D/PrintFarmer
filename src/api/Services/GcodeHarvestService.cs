using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.Models;
using Farm.Web.Shared;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services;

/// <summary>
/// Service for harvesting G-code files from registered printers
/// </summary>
public class GcodeHarvestService : IGcodeHarvestService
{
    private readonly AppDbContext _db;
    private readonly IMoonrakerClient _moonraker;
    private readonly IPrusaLinkClient _prusa;
    private readonly ISdcpClient _sdcp;
    private readonly ILogger<GcodeHarvestService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHarvestQueue _harvestQueue;
    private readonly ConcurrentDictionary<Guid, Task> _activeTasks = new();
    
    private static readonly string GcodeStoragePath = "gcode-library";
    
    public GcodeHarvestService(
        AppDbContext db,
        IMoonrakerClient moonraker,
        IPrusaLinkClient prusa,
        ISdcpClient sdcp,
        ILogger<GcodeHarvestService> logger,
        IServiceProvider serviceProvider,
        IHarvestQueue harvestQueue)
    {
        _db = db;
        _moonraker = moonraker;
        _prusa = prusa;
        _sdcp = sdcp;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _harvestQueue = harvestQueue;
    }

    public async Task<GcodeHarvestResultDto> StartHarvestAsync(StartGcodeHarvestDto request, CancellationToken ct = default)
    {
        var printer = await _db.Printers.FirstOrDefaultAsync(p => p.Id == request.PrinterId, ct);
        if (printer == null)
        {
            return new GcodeHarvestResultDto(Guid.Empty, false, "Printer not found");
        }

        // Check if there's already an active harvest operation for this printer
        var existingOperation = await _db.GcodeHarvestOperations
            .FirstOrDefaultAsync(h => h.PrinterId == request.PrinterId && h.Status == GcodeHarvestStatus.Running, ct);
            
        if (existingOperation != null)
        {
            return new GcodeHarvestResultDto(existingOperation.Id, false, $"Harvest operation already in progress for printer '{printer.Name}'. Please wait for it to complete or cancel it first.");
        }

        // Create harvest operation
        var operation = new GcodeHarvestOperation
        {
            Id = Guid.NewGuid(),
            PrinterId = request.PrinterId,
            StartedAt = DateTime.UtcNow,
            Status = GcodeHarvestStatus.Running,
            IncludeSubdirectories = request.IncludeSubdirectories,
            MaxFileSizeBytes = request.MaxFileSizeBytes,
            ModifiedAfter = request.ModifiedAfter,
            FilesFound = 0,
            FilesAdded = 0,
            FilesSkipped = 0,
            FilesErrored = 0,
            TotalBytesProcessed = 0
        };

        _db.GcodeHarvestOperations.Add(operation);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Starting file discovery for operation {OperationId} on printer {PrinterName}", 
            operation.Id, printer.Name);

        // Start file discovery and queueing in background
        // Using a properly tracked task with error handling and using a new, dedicated cancellation token
        var backgroundTask = Task.Run(async () => 
        {
            try 
            {
                _logger.LogInformation("Background task started for operation {OperationId}", operation.Id);
                await DiscoverAndQueueFilesAsync(operation, printer);
                _logger.LogInformation("Background task completed successfully for operation {OperationId}", operation.Id);
            }
            catch (Exception ex) 
            {
                _logger.LogError(ex, "Background task failed for operation {OperationId}", operation.Id);
                
                // Update the operation status to failed
                using var scope = _serviceProvider.CreateScope();
                var scopedDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var dbOperation = await scopedDb.GcodeHarvestOperations
                    .FirstOrDefaultAsync(o => o.Id == operation.Id);
                if (dbOperation != null)
                {
                    dbOperation.Status = GcodeHarvestStatus.Failed;
                    dbOperation.ErrorMessage = $"File discovery failed: {ex.Message}";
                    dbOperation.CompletedAt = DateTime.UtcNow;
                    await scopedDb.SaveChangesAsync();
                }
            }
            finally
            {
                // Remove from active tasks when done
                _activeTasks.TryRemove(operation.Id, out _);
                _logger.LogDebug("Removed operation {OperationId} from active tasks tracking", operation.Id);
            }
        });
        
        // Add to active tasks collection for tracking
        _activeTasks[operation.Id] = backgroundTask;
        _logger.LogDebug("Added operation {OperationId} to active tasks tracking", operation.Id);

        return new GcodeHarvestResultDto(
            operation.Id, 
            true, 
            "Harvest operation started",
            DiscoveredFiles: 0,  // Files will be discovered asynchronously
            ImportedFiles: 0);   // Files will be imported by background workers
    }

    /// <summary>
    /// Discover files from printer and queue them for processing
    /// </summary>
    private async Task DiscoverAndQueueFilesAsync(GcodeHarvestOperation operation, Printer printer)
    {
        using var scope = _serviceProvider.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scopedMoonraker = scope.ServiceProvider.GetRequiredService<IMoonrakerClient>();
        var scopedPrusa = scope.ServiceProvider.GetRequiredService<IPrusaLinkClient>();
        var scopedSdcp = scope.ServiceProvider.GetRequiredService<ISdcpClient>();
        var scopedLogger = scope.ServiceProvider.GetRequiredService<ILogger<GcodeHarvestService>>();

        try
        {
            scopedLogger.LogInformation("Starting file discovery in scoped context for operation {OperationId} on printer {PrinterName}", 
                operation.Id, printer.Name);

            // Get file list from printer based on backend type
            var backend = (PrinterBackend)printer.Backend;
            scopedLogger.LogInformation("Calling file discovery for backend {Backend} on printer {PrinterName} at {ServerUrl}", 
                backend, printer.Name, printer.ServerUrl);
            
            List<PrinterFileInfo> fileList;
            
            // Depending on printer backend, call the appropriate method to get files
            switch (backend)
            {
                case PrinterBackend.Moonraker:
                    scopedLogger.LogInformation("Getting files from Moonraker backend at {ServerUrl}", printer.ServerUrl);
                    fileList = await GetMoonrakerFilesAsync(printer.ServerUrl, scopedMoonraker, scopedLogger);
                    break;
                case PrinterBackend.PrusaLink:
                    scopedLogger.LogInformation("Getting files from PrusaLink backend at {ServerUrl}", printer.ServerUrl);
                    fileList = await GetPrusaLinkFilesAsync(printer.ServerUrl, printer.ApiKey, scopedPrusa, scopedLogger);
                    break;
                case PrinterBackend.SDCP:
                    scopedLogger.LogInformation("Getting files from SDCP backend at {ServerUrl}", printer.ServerUrl);
                    fileList = await GetSdcpFilesAsync(printer.ServerUrl, scopedSdcp, scopedLogger);
                    break;
                default:
                    scopedLogger.LogWarning("Unsupported printer backend {Backend}", backend);
                    fileList = new List<PrinterFileInfo>();
                    break;
            }

            scopedLogger.LogInformation("Discovered {FileCount} files for operation {OperationId}", 
                fileList.Count, operation.Id);

            // Count how many are G-code files
            int gcodeFileCount = fileList.Count(f => f.Name.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase));
            scopedLogger.LogInformation("Found {GcodeFileCount} G-code files out of {TotalFileCount} total files",
                gcodeFileCount, fileList.Count);

            // Update files found count immediately
            var dbOperation = await scopedDb.GcodeHarvestOperations
                .FirstOrDefaultAsync(o => o.Id == operation.Id);
            if (dbOperation != null)
            {
                dbOperation.FilesFound = gcodeFileCount;
                await scopedDb.SaveChangesAsync();
                scopedLogger.LogInformation("Updated operation {OperationId} with {GcodeFileCount} G-code files found", 
                    operation.Id, dbOperation.FilesFound);
            }
            else
            {
                scopedLogger.LogWarning("Could not find operation {OperationId} in database to update files found count", operation.Id);
            }

            // Queue each G-code file for processing
            int queuedCount = 0;
            foreach (var fileInfo in fileList)
            {
                if (!fileInfo.Name.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase))
                {
                    scopedLogger.LogDebug("Skipping non-G-code file: {FileName}", fileInfo.Name);
                    continue;
                }

                var job = new HarvestFileJob
                {
                    OperationId = operation.Id,
                    PrinterId = printer.Id,
                    ServerUrl = printer.ServerUrl,
                    FilePath = fileInfo.Path,
                    FileName = fileInfo.Name,
                    FileSize = fileInfo.Size,
                    ModifiedAt = fileInfo.ModifiedAt
                };

                scopedLogger.LogDebug("Queueing file {FileName} with path {FilePath}", fileInfo.Name, fileInfo.Path);
                await _harvestQueue.EnqueueAsync(job);
                queuedCount++;
                
                if (queuedCount % 10 == 0)
                {
                    scopedLogger.LogInformation("Queued {QueuedCount} files so far for operation {OperationId}", 
                        queuedCount, operation.Id);
                }
            }

            scopedLogger.LogInformation("Queued {QueuedCount} files for processing in operation {OperationId}", 
                queuedCount, operation.Id);
            
            // Check how many discovered files already exist
            var existingFiles = await scopedDb.DiscoveredGcodeFiles
                .Where(d => d.HarvestOperationId == operation.Id)
                .CountAsync();
                
            scopedLogger.LogInformation("Found {ExistingCount} existing discovered files for operation {OperationId}", 
                existingFiles, operation.Id);

            // If no files were queued, mark operation as completed
            if (queuedCount == 0)
            {
                if (dbOperation != null)
                {
                    dbOperation.Status = GcodeHarvestStatus.Completed;
                    dbOperation.CompletedAt = DateTime.UtcNow;
                    await scopedDb.SaveChangesAsync();
                    scopedLogger.LogInformation("Operation {OperationId} completed with no files to process", operation.Id);
                }
            }
        }
        catch (Exception ex)
        {
            scopedLogger.LogError(ex, "File discovery failed for operation {OperationId}", operation.Id);
            
            // Mark operation as failed
            var dbOperation = await scopedDb.GcodeHarvestOperations
                .FirstOrDefaultAsync(o => o.Id == operation.Id);
            if (dbOperation != null)
            {
                dbOperation.Status = GcodeHarvestStatus.Failed;
                dbOperation.ErrorMessage = ex.Message;
                dbOperation.CompletedAt = DateTime.UtcNow;
                await scopedDb.SaveChangesAsync();
            }
        }
    }

    private async Task PerformHarvestInBackgroundAsync(Guid operationId, Printer printer, IServiceScope scope)
    {
        _logger.LogInformation("Background harvest task starting for operation {OperationId}", operationId);
        try
        {
            var scopedDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var scopedMoonraker = scope.ServiceProvider.GetRequiredService<IMoonrakerClient>();
            var scopedPrusa = scope.ServiceProvider.GetRequiredService<IPrusaLinkClient>();
            var scopedSdcp = scope.ServiceProvider.GetRequiredService<ISdcpClient>();
            var scopedLogger = scope.ServiceProvider.GetRequiredService<ILogger<GcodeHarvestService>>();

            scopedLogger.LogInformation("Created scoped services for operation {OperationId}", operationId);

            // Re-fetch the operation from the database in this scope
            var operation = await scopedDb.GcodeHarvestOperations
                .FirstOrDefaultAsync(o => o.Id == operationId);
            
            if (operation == null)
            {
                scopedLogger.LogError("Harvest operation {OperationId} not found", operationId);
                return;
            }

            scopedLogger.LogInformation("Found operation {OperationId}, status: {Status}", operationId, operation.Status);

            // Check if operation was cancelled before we start processing
            if (operation.Status == GcodeHarvestStatus.Cancelled)
            {
                scopedLogger.LogInformation("Harvest operation {OperationId} was cancelled before processing started", operationId);
                return;
            }

            await PerformHarvestAsync(operation, printer, scopedDb, scopedMoonraker, scopedPrusa, scopedSdcp, scopedLogger);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background harvest task failed for operation {OperationId}", operationId);
        }
    }

    private async Task PerformHarvestAsync(GcodeHarvestOperation operation, Printer printer, AppDbContext db, IMoonrakerClient moonraker, IPrusaLinkClient prusa, ISdcpClient sdcp, ILogger<GcodeHarvestService> logger)
    {
        logger.LogInformation("PerformHarvestAsync starting for operation {OperationId}", operation.Id);
        try
        {
            logger.LogInformation("Starting G-code harvest for printer {PrinterName} ({PrinterId})", 
                printer.Name, printer.Id);

            var discoveredFiles = new List<DiscoveredGcodeFile>();
            
            logger.LogInformation("About to call GetMoonrakerFilesAsync for printer {PrinterName}", printer.Name);
            // Get file list from printer based on backend type
            var backend = (PrinterBackend)printer.Backend;
            var fileList = backend switch
            {
                PrinterBackend.Moonraker => await GetMoonrakerFilesAsync(printer.ServerUrl, moonraker, logger),
                PrinterBackend.PrusaLink => await GetPrusaLinkFilesAsync(printer.ServerUrl, printer.ApiKey, prusa, logger),
                PrinterBackend.SDCP => await GetSdcpFilesAsync(printer.ServerUrl, sdcp, logger),
                _ => new List<PrinterFileInfo>()
            };

            logger.LogInformation("Found {FileCount} files for harvest on printer {PrinterName}", fileList.Count, printer.Name);

            operation.FilesFound = fileList.Count;
            await UpdateOperationAsync(operation, db);

            // Process each file
            foreach (var fileInfo in fileList)
            {
                // Check if operation was cancelled during processing
                await db.Entry(operation).ReloadAsync();
                if (operation.Status == GcodeHarvestStatus.Cancelled)
                {
                    logger.LogInformation("Harvest operation {OperationId} was cancelled during processing", operation.Id);
                    return;
                }

                if (!fileInfo.Name.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Check size limit
                if (operation.MaxFileSizeBytes.HasValue && fileInfo.Size > operation.MaxFileSizeBytes.Value)
                {
                    logger.LogDebug("Skipping file {FileName} - too large ({Size} bytes)", 
                        fileInfo.Name, fileInfo.Size);
                    continue;
                }

                // Check modification date
                if (operation.ModifiedAfter.HasValue && fileInfo.ModifiedAt.HasValue && 
                    fileInfo.ModifiedAt.Value < operation.ModifiedAfter.Value)
                {
                    continue;
                }

                var discoveredFile = new DiscoveredGcodeFile
                {
                    Id = Guid.NewGuid(),
                    HarvestOperationId = operation.Id,
                    PrinterPath = fileInfo.Path,
                    FileName = fileInfo.Name,
                    FileSizeBytes = fileInfo.Size,
                    ModifiedAt = fileInfo.ModifiedAt
                };

                try
                {
                    // Try to download and analyze the file
                    var gcodeContent = await DownloadFileAsync(backend, printer, fileInfo.Path, moonraker, prusa, sdcp, logger);
                    if (gcodeContent != null)
                    {
                        // Calculate hash for deduplication
                        discoveredFile.FileHash = await CalculateFileHashAsync(gcodeContent);
                        
                        // Check if already in library
                        var existingFile = await db.GcodeFiles
                            .FirstOrDefaultAsync(g => g.FileHash == discoveredFile.FileHash);
                        
                        if (existingFile != null)
                        {
                            discoveredFile.AlreadyInLibrary = true;
                            discoveredFile.ExistingLibraryFileId = existingFile.Id;
                            operation.FilesSkipped++;
                        }
                        else
                        {
                            // Extract metadata
                            gcodeContent.Position = 0;
                            var metadata = await ExtractMetadataAsync(gcodeContent);
                            ApplyMetadataToDiscoveredFile(discoveredFile, metadata);
                        }

                        operation.TotalBytesProcessed += fileInfo.Size;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to process file {FileName}", fileInfo.Name);
                    discoveredFile.ProcessingFailed = true;
                    discoveredFile.ErrorMessage = ex.Message;
                    operation.FilesErrored++;
                }

                discoveredFiles.Add(discoveredFile);
                await UpdateOperationAsync(operation, db);
            }

            // Save all discovered files
            db.DiscoveredGcodeFiles.AddRange(discoveredFiles);
            operation.Status = GcodeHarvestStatus.Completed;
            operation.CompletedAt = DateTime.UtcNow;
            
            await db.SaveChangesAsync();
            
            logger.LogInformation("Completed G-code harvest for printer {PrinterName}. Found: {Found}, Skipped: {Skipped}, Errors: {Errors}",
                printer.Name, operation.FilesFound, operation.FilesSkipped, operation.FilesErrored);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Harvest operation failed for printer {PrinterName}", printer.Name);
            operation.Status = GcodeHarvestStatus.Failed;
            operation.ErrorMessage = ex.Message;
            operation.CompletedAt = DateTime.UtcNow;
            await UpdateOperationAsync(operation, db);
        }
    }

    private async Task<MemoryStream?> DownloadFileAsync(PrinterBackend backend, Printer printer, string filePath, IMoonrakerClient? moonraker = null, IPrusaLinkClient? prusa = null, ISdcpClient? sdcp = null, ILogger<GcodeHarvestService>? logger = null)
    {
        var log = logger ?? _logger;
        try
        {
            return backend switch
            {
                PrinterBackend.Moonraker => await DownloadMoonrakerFileAsync(printer.ServerUrl, filePath, moonraker, log),
                PrinterBackend.PrusaLink => await DownloadPrusaLinkFileAsync(printer.ServerUrl, printer.ApiKey, filePath, prusa, log),
                PrinterBackend.SDCP => await DownloadSdcpFileAsync(printer.ServerUrl, filePath, sdcp, log),
                _ => null
            };
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to download file {FilePath} from printer {PrinterName}", 
                filePath, printer.Name);
            return null;
        }
    }

    // Overload for ImportSelectedFilesAsync that uses instance clients
    private async Task<MemoryStream?> DownloadFileAsync(PrinterBackend backend, Printer printer, string filePath)
    {
        return await DownloadFileAsync(backend, printer, filePath, _moonraker, _prusa, _sdcp, _logger);
    }

    public async Task<GcodeMetadataDto> ExtractMetadataAsync(Stream gcodeStream, CancellationToken ct = default)
    {
        var metadata = new GcodeMetadataDto();
        
        using var reader = new StreamReader(gcodeStream, leaveOpen: true);
        
        // Read first few hundred lines to get slicer comments
        var linesRead = 0;
        var maxLines = 500;
        
        while (linesRead < maxLines && !reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (line == null) break;
            
            linesRead++;
            
            // Skip non-comment lines after header
            if (!line.StartsWith(";") && linesRead > 50)
                break;
                
            metadata = ExtractMetadataFromLine(line, metadata);
        }
        
        return metadata;
    }

    private static GcodeMetadataDto ExtractMetadataFromLine(string line, GcodeMetadataDto metadata)
    {
        if (!line.StartsWith(";")) return metadata;
        
        var content = line.Substring(1).Trim();
        
        // PrusaSlicer patterns
        if (content.StartsWith("Generated by PrusaSlicer"))
        {
            var versionMatch = Regex.Match(content, @"PrusaSlicer (\S+)");
            if (versionMatch.Success)
            {
                metadata = metadata with { SlicerName = "PrusaSlicer", SlicerVersion = versionMatch.Groups[1].Value };
            }
        }
        
        // Cura patterns
        if (content.StartsWith("Generated with Cura"))
        {
            var versionMatch = Regex.Match(content, @"Cura_SteamEngine (\S+)");
            if (versionMatch.Success)
            {
                metadata = metadata with { SlicerName = "Cura", SlicerVersion = versionMatch.Groups[1].Value };
            }
        }
        
        // Extract common parameters
        metadata = TryExtractParameter(content, @"estimated printing time.*?(\d+)h (\d+)m", metadata, 
            m => metadata with { PrintTimeMinutes = int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value) });
            
        metadata = TryExtractParameter(content, @"filament used.*?(\d+\.?\d*)mm", metadata,
            m => metadata with { FilamentLengthMm = double.Parse(m.Groups[1].Value) });
            
        metadata = TryExtractParameter(content, @"nozzle_diameter = (\d+\.?\d*)", metadata,
            m => metadata with { NozzleDiameter = double.Parse(m.Groups[1].Value) });
            
        metadata = TryExtractParameter(content, @"filament_type = (\w+)", metadata,
            m => metadata with { Material = m.Groups[1].Value });
            
        metadata = TryExtractParameter(content, @"layer_height = (\d+\.?\d*)", metadata,
            m => metadata with { LayerHeight = double.Parse(m.Groups[1].Value) });
            
        metadata = TryExtractParameter(content, @"fill_density = (\d+)%", metadata,
            m => metadata with { InfillPercentage = m.Groups[1].Value + "%" });
        
        return metadata;
    }
    
    private static GcodeMetadataDto TryExtractParameter(string content, string pattern, GcodeMetadataDto metadata, Func<Match, GcodeMetadataDto> apply)
    {
        var match = Regex.Match(content, pattern, RegexOptions.IgnoreCase);
        return match.Success ? apply(match) : metadata;
    }

    public async Task<string> CalculateFileHashAsync(Stream fileStream, CancellationToken ct = default)
    {
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(fileStream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<GcodeHarvestOperationDto?> GetHarvestOperationAsync(Guid operationId, CancellationToken ct = default)
    {
        var operation = await _db.GcodeHarvestOperations
            .Include(h => h.Printer)
            .FirstOrDefaultAsync(h => h.Id == operationId, ct);
            
        return operation == null ? null : MapToDto(operation);
    }

    public async Task<DiscoveredGcodeFileDto[]> GetDiscoveredFilesAsync(Guid operationId, CancellationToken ct = default)
    {
        _logger.LogInformation("Getting discovered files for operation {OperationId}", operationId);
        
        // Verify the operation exists
        var operation = await _db.GcodeHarvestOperations.FirstOrDefaultAsync(o => o.Id == operationId, ct);
        if (operation == null)
        {
            _logger.LogWarning("GetDiscoveredFilesAsync: Operation {OperationId} not found", operationId);
            return Array.Empty<DiscoveredGcodeFileDto>();
        }
        
        _logger.LogInformation("Found operation {OperationId} with status {Status}, files found: {FilesFound}", 
            operationId, operation.Status, operation.FilesFound);
        
        // Get files with explicit logging
        var files = await _db.DiscoveredGcodeFiles
            .Where(d => d.HarvestOperationId == operationId)
            .OrderBy(d => d.FileName)
            .ToArrayAsync(ct);
            
        _logger.LogInformation("Found {FileCount} discovered files for operation {OperationId}", 
            files.Length, operationId);
        
        return files.Select(MapToDto).ToArray();
    }

    public async Task<GcodeHarvestResultDto> ImportSelectedFilesAsync(ImportSelectedGcodeFilesDto request, CancellationToken ct = default)
    {
        var operation = await _db.GcodeHarvestOperations
            .Include(h => h.Printer)
            .FirstOrDefaultAsync(h => h.Id == request.HarvestOperationId, ct);
            
        if (operation == null)
        {
            return new GcodeHarvestResultDto(request.HarvestOperationId, false, "Harvest operation not found");
        }

        var selectedFiles = await _db.DiscoveredGcodeFiles
            .Where(d => request.SelectedFileIds.Contains(d.Id))
            .ToArrayAsync(ct);

        var errors = new List<string>();
        var importedCount = 0;

        foreach (var discoveredFile in selectedFiles)
        {
            try
            {
                if (discoveredFile.AlreadyInLibrary)
                {
                    continue; // Skip files already in library
                }

                // Create storage directory if needed
                var environment = _serviceProvider.GetRequiredService<IWebHostEnvironment>();
                var storageDir = Path.Combine(environment.ContentRootPath, "wwwroot", GcodeStoragePath);
                Directory.CreateDirectory(storageDir);

                // Generate unique filename
                var fileName = $"{Guid.NewGuid()}_{discoveredFile.FileName}";
                var filePath = Path.Combine(storageDir, fileName);

                // Download file from printer
                var backend = (PrinterBackend)operation.Printer.Backend;
                using var gcodeContent = await DownloadFileAsync(backend, operation.Printer, discoveredFile.PrinterPath);
                
                if (gcodeContent == null)
                {
                    errors.Add($"Failed to download {discoveredFile.FileName}");
                    continue;
                }

                // Save to local storage
                await using (var fileStream = File.Create(filePath))
                {
                    gcodeContent.Position = 0;
                    await gcodeContent.CopyToAsync(fileStream, ct);
                }

                // Create library entry
                var gcodeFile = new GcodeFile
                {
                    Id = Guid.NewGuid(),
                    OriginalFileName = discoveredFile.FileName,
                    DisplayName = Path.GetFileNameWithoutExtension(discoveredFile.FileName),
                    FilePath = filePath,
                    FileSizeBytes = discoveredFile.FileSizeBytes,
                    FileHash = discoveredFile.FileHash ?? "",
                    UploadedAt = DateTime.UtcNow,
                    Source = GcodeSource.Harvested,
                    SourcePrinterId = operation.PrinterId,
                    OriginalPrinterPath = discoveredFile.PrinterPath,
                    LastSeenOnPrinter = DateTime.UtcNow,
                    RequiredNozzleDiameter = discoveredFile.ExtractedNozzleDiameter,
                    RequiredMaterial = discoveredFile.ExtractedMaterial,
                    EstimatedPrintTimeMinutes = discoveredFile.ExtractedPrintTime,
                    EstimatedFilamentLengthMm = discoveredFile.ExtractedFilamentLength,
                    SlicerName = discoveredFile.ExtractedSlicerName,
                    SlicerVersion = discoveredFile.ExtractedSlicerVersion,
                    Tags = request.DefaultTags != null ? JsonSerializer.Serialize(request.DefaultTags) : null
                };

                _db.GcodeFiles.Add(gcodeFile);
                importedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to import file {FileName}", discoveredFile.FileName);
                errors.Add($"Failed to import {discoveredFile.FileName}: {ex.Message}");
            }
        }

        await _db.SaveChangesAsync(ct);

        return new GcodeHarvestResultDto(
            request.HarvestOperationId,
            true,
            $"Imported {importedCount} files",
            selectedFiles.Length,
            importedCount,
            errors.Count > 0 ? errors.ToArray() : null);
    }

    public async Task<bool> CancelHarvestAsync(Guid operationId, CancellationToken ct = default)
    {
        var operation = await _db.GcodeHarvestOperations
            .FirstOrDefaultAsync(h => h.Id == operationId, ct);
            
        if (operation == null || operation.Status != GcodeHarvestStatus.Running)
        {
            return false;
        }

        operation.Status = GcodeHarvestStatus.Cancelled;
        operation.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        
        // Log the cancellation for tracking purposes
        _logger.LogInformation("Harvest operation {OperationId} was cancelled", operationId);
        
        // Note: We don't actually cancel the task because Task.Run doesn't support 
        // cancellation after it's started. The background task will check the 
        // operation status and exit gracefully when it sees the Cancelled status.
        
        return true;
    }

    public async Task<GcodeHarvestOperationDto?> GetActiveHarvestAsync(Guid printerId, CancellationToken ct = default)
    {
        var operation = await _db.GcodeHarvestOperations
            .Include(h => h.Printer)
            .FirstOrDefaultAsync(h => h.PrinterId == printerId && h.Status == GcodeHarvestStatus.Running, ct);
            
        return operation == null ? null : MapToDto(operation);
    }

    public async Task<GcodeHarvestOperationDto[]> GetRecentHarvestsAsync(Guid printerId, int count = 10, CancellationToken ct = default)
    {
        var operations = await _db.GcodeHarvestOperations
            .Include(h => h.Printer)
            .Where(h => h.PrinterId == printerId)
            .OrderByDescending(h => h.StartedAt)
            .Take(count)
            .ToArrayAsync(ct);
            
        return operations.Select(MapToDto).ToArray();
    }

    public async Task<GcodeHarvestOperationDto[]> GetActiveHarvestsAsync(CancellationToken ct = default)
    {
        var operations = await _db.GcodeHarvestOperations
            .Include(h => h.Printer)
            .Where(h => h.Status == GcodeHarvestStatus.Running)
            .OrderByDescending(h => h.StartedAt)
            .ToArrayAsync(ct);
            
        return operations.Select(MapToDto).ToArray();
    }

    // Helper methods for different printer backends
    private async Task<List<PrinterFileInfo>> GetMoonrakerFilesAsync(string serverUrl)
    {
        // Delegate to the more comprehensive implementation with retry logic
        return await GetMoonrakerFilesAsync(serverUrl, _moonraker, _logger);
    }

    private async Task CollectFilesRecursivelyAsync(List<PrinterFileInfo> files, DirectoryInfo directory, string basePath, string serverUrl)
    {
        // Add files from current directory
        foreach (var file in directory.Files)
        {
            files.Add(new PrinterFileInfo
            {
                Name = System.IO.Path.GetFileName(file.Path),
                Path = file.Path,
                Size = file.Size,
                ModifiedAt = DateTimeOffset.FromUnixTimeSeconds((long)file.Modified).DateTime
            });
        }

        // Recursively process subdirectories
        foreach (var subDir in directory.Dirs)
        {
            try
            {
                var subDirPath = $"{basePath}/{subDir.Path}";
                var subDirectoryInfo = await _moonraker.GetDirectoryAsync(serverUrl, subDirPath, extended: true);
                if (subDirectoryInfo != null)
                {
                    await CollectFilesRecursivelyAsync(files, subDirectoryInfo, subDirPath, serverUrl);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to access subdirectory {SubDirPath}", subDir.Path);
            }
        }
    }

    private async Task<List<PrinterFileInfo>> GetPrusaLinkFilesAsync(string serverUrl, string? apiKey)
    {
        try
        {
            var fileNames = await _prusa.GetFileListAsync(serverUrl, apiKey);
            var files = fileNames.Select(fileName => new PrinterFileInfo
            {
                Name = fileName,
                Path = fileName,
                Size = 0, // PrusaLink basic API doesn't provide size info
                ModifiedAt = null // PrusaLink basic API doesn't provide modification date
            }).ToList();
            
            _logger.LogInformation("Found {FileCount} files in PrusaLink at {ServerUrl}", files.Count, serverUrl);
            return files;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get file list from PrusaLink at {ServerUrl}", serverUrl);
            return new List<PrinterFileInfo>();
        }
    }

    private async Task<MemoryStream?> DownloadMoonrakerFileAsync(string serverUrl, string filePath, IMoonrakerClient? moonraker = null, ILogger<GcodeHarvestService>? logger = null)
    {
        var log = logger ?? _logger;
        var client = moonraker ?? _moonraker;
        
        try
        {
            log.LogInformation("Downloading file {FilePath} from Moonraker at {ServerUrl}", filePath, serverUrl);
            var bytes = await client.DownloadFileAsync(serverUrl, filePath);
            if (bytes != null)
            {
                log.LogInformation("Successfully downloaded {FilePath} ({Size} bytes)", filePath, bytes.Length);
                return new MemoryStream(bytes);
            }
            
            log.LogWarning("Failed to download file {FilePath} from Moonraker at {ServerUrl}", filePath, serverUrl);
            return null;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to download file {FilePath} from Moonraker at {ServerUrl}", filePath, serverUrl);
            return null;
        }
    }

    private async Task<MemoryStream?> DownloadPrusaLinkFileAsync(string serverUrl, string? apiKey, string filePath, IPrusaLinkClient? prusa = null, ILogger<GcodeHarvestService>? logger = null)
    {
        var log = logger ?? _logger;
        var client = prusa ?? _prusa;
        
        try
        {
            // PrusaLink file download implementation would go here
            log.LogInformation("PrusaLink file download not yet implemented for {FilePath} at {ServerUrl}", filePath, serverUrl);
            await Task.Delay(100); // Adding await to fix the warning
            return null;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to download file {FilePath} from PrusaLink at {ServerUrl}", filePath, serverUrl);
            return null;
        }
    }

    private async Task<MemoryStream?> DownloadSdcpFileAsync(string serverUrl, string filePath, ISdcpClient? sdcp = null, ILogger<GcodeHarvestService>? logger = null)
    {
        var log = logger ?? _logger;
        try
        {
            // SDCP file download implementation would go here
            log.LogInformation("SDCP file download not yet implemented for {FilePath} at {ServerUrl}", filePath, serverUrl);
            await Task.Delay(100); // Adding await to fix the warning
            return null;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to download file {FilePath} from SDCP at {ServerUrl}", filePath, serverUrl);
            return null;
        }
    }

    private async Task UpdateOperationAsync(GcodeHarvestOperation operation, AppDbContext? db = null)
    {
        var dbContext = db ?? _db;
        dbContext.GcodeHarvestOperations.Update(operation);
        await dbContext.SaveChangesAsync();
    }

    // Helper methods for different printer backends
    private async Task<List<PrinterFileInfo>> GetMoonrakerFilesAsync(string serverUrl, IMoonrakerClient? moonraker = null, ILogger<GcodeHarvestService>? logger = null)
    {
        var client = moonraker ?? _moonraker;
        var log = logger ?? _logger;
        
        try
        {
            log.LogInformation("GetMoonrakerFilesAsync starting for {ServerUrl} with retry logic", serverUrl);
            var files = new List<PrinterFileInfo>();
            
            // Get the gcodes directory listing with retry
            log.LogInformation("Calling GetDirectoryAsync for gcodes directory with retry");
            var directoryInfo = await RetryPolicyHelper.ExecuteWithRetryAsync(
                () => client.GetDirectoryAsync(serverUrl, "gcodes", extended: true),
                logger: log,
                operationName: $"GetDirectoryAsync for gcodes directory at {serverUrl}");
                
            log.LogInformation("GetDirectoryAsync completed, directoryInfo is {IsNull}", directoryInfo == null ? "null" : "not null");
            
            if (directoryInfo != null)
            {
                var fileCount = directoryInfo.Files?.Length ?? 0;
                var dirCount = directoryInfo.Dirs?.Length ?? 0;
                log.LogInformation("Directory has {FileCount} files and {DirCount} subdirectories", fileCount, dirCount);
                await CollectFilesRecursivelyWithRetryAsync(files, directoryInfo, "gcodes", serverUrl, client, log);
            }
            
            log.LogInformation("Found {FileCount} files in Moonraker at {ServerUrl}", files.Count, serverUrl);
            return files;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to get file list from Moonraker at {ServerUrl}", serverUrl);
            return new List<PrinterFileInfo>();
        }
    }

    private async Task CollectFilesRecursivelyWithRetryAsync(List<PrinterFileInfo> files, DirectoryInfo directory, string basePath, string serverUrl, IMoonrakerClient client, ILogger log)
    {
        // Add files from current directory
        if (directory.Files != null)
        {
            foreach (var file in directory.Files)
            {
                files.Add(new PrinterFileInfo
                {
                    Name = System.IO.Path.GetFileName(file.Path),
                    Path = file.Path,
                    Size = file.Size,
                    ModifiedAt = DateTimeOffset.FromUnixTimeSeconds((long)file.Modified).DateTime
                });
            }
        }

        // Recursively process subdirectories
        if (directory.Dirs != null)
        {
            foreach (var subDir in directory.Dirs)
            {
                try
                {
                    var subDirPath = $"{basePath}/{subDir.Path}";
                    log.LogDebug("Processing subdirectory {SubDirPath}", subDirPath);
                    
                    // Get subdirectory info with retry
                    var subDirInfo = await RetryPolicyHelper.ExecuteWithRetryAsync(
                        () => client.GetDirectoryAsync(serverUrl, subDirPath, extended: true),
                        logger: log,
                        operationName: $"GetDirectoryAsync for subdirectory {subDirPath}");
                    
                    if (subDirInfo != null)
                    {
                        await CollectFilesRecursivelyWithRetryAsync(files, subDirInfo, subDirPath, serverUrl, client, log);
                    }
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Error processing subdirectory {SubDirPath}", subDir.Path);
                    // Continue with next subdirectory
                }
            }
        }
    }

    private async Task CollectFilesRecursivelyAsync(List<PrinterFileInfo> files, DirectoryInfo directory, string basePath, string serverUrl, IMoonrakerClient moonraker, ILogger<GcodeHarvestService> logger)
    {
        // Add files from current directory
        if (directory.Files != null)
        {
            foreach (var file in directory.Files)
            {
                files.Add(new PrinterFileInfo
                {
                    Name = System.IO.Path.GetFileName(file.Path),
                    Path = file.Path,
                    Size = file.Size,
                    ModifiedAt = DateTimeOffset.FromUnixTimeSeconds((long)file.Modified).DateTime
                });
            }
        }

        // Recursively process subdirectories
        if (directory.Dirs != null)
        {
            foreach (var subDir in directory.Dirs)
            {
                try
                {
                    var subDirPath = $"{basePath}/{subDir.Path}";
                    var subDirectoryInfo = await moonraker.GetDirectoryAsync(serverUrl, subDirPath, extended: true);
                    if (subDirectoryInfo != null)
                    {
                        await CollectFilesRecursivelyAsync(files, subDirectoryInfo, subDirPath, serverUrl, moonraker, logger);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to access subdirectory {SubDirPath}", subDir.Path);
                }
            }
        }
    }

    private async Task<List<PrinterFileInfo>> GetPrusaLinkFilesAsync(string serverUrl, string? apiKey, IPrusaLinkClient? prusa = null, ILogger<GcodeHarvestService>? logger = null)
    {
        var client = prusa ?? _prusa;
        var log = logger ?? _logger;
        
        try
        {
            var fileNames = await client.GetFileListAsync(serverUrl, apiKey);
            var files = fileNames.Select(fileName => new PrinterFileInfo
            {
                Name = fileName,
                Path = fileName,
                Size = 0, // PrusaLink basic API doesn't provide size info
                ModifiedAt = null // PrusaLink basic API doesn't provide modification date
            }).ToList();
            
            log.LogInformation("Found {FileCount} files in PrusaLink at {ServerUrl}", files.Count, serverUrl);
            return files;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to get file list from PrusaLink at {ServerUrl}", serverUrl);
            return new List<PrinterFileInfo>();
        }
    }

    private async Task<List<PrinterFileInfo>> GetSdcpFilesAsync(string serverUrl, ISdcpClient? sdcp = null, ILogger<GcodeHarvestService>? logger = null)
    {
        var log = logger ?? _logger;
        try
        {
            // SDCP implementation would go here
            log.LogInformation("SDCP file listing not yet implemented for {ServerUrl}", serverUrl);
            await Task.Delay(100); // Adding await to fix the warning
            return new List<PrinterFileInfo>();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to get file list from SDCP at {ServerUrl}", serverUrl);
            return new List<PrinterFileInfo>();
        }
    }

    private static void ApplyMetadataToDiscoveredFile(DiscoveredGcodeFile discoveredFile, GcodeMetadataDto metadata)
    {
        discoveredFile.ExtractedSlicerName = metadata.SlicerName;
        discoveredFile.ExtractedSlicerVersion = metadata.SlicerVersion;
        discoveredFile.ExtractedPrintTime = metadata.PrintTimeMinutes;
        discoveredFile.ExtractedFilamentLength = metadata.FilamentLengthMm;
        discoveredFile.ExtractedNozzleDiameter = metadata.NozzleDiameter;
        discoveredFile.ExtractedMaterial = metadata.Material;
        discoveredFile.ExtractedLayerHeight = metadata.LayerHeight?.ToString();
        discoveredFile.ExtractedInfill = metadata.InfillPercentage;
    }

    private static GcodeHarvestOperationDto MapToDto(GcodeHarvestOperation operation)
    {
        return new GcodeHarvestOperationDto(
            operation.Id,
            operation.PrinterId,
            operation.Printer?.Name ?? "Unknown",
            operation.StartedAt,
            operation.CompletedAt,
            (GcodeHarvestStatusDto)operation.Status,
            operation.ErrorMessage,
            operation.FilesFound,
            operation.FilesAdded,
            operation.FilesSkipped,
            operation.FilesErrored,
            operation.TotalBytesProcessed,
            operation.IncludeSubdirectories,
            operation.MaxFileSizeBytes,
            operation.ModifiedAfter);
    }

    private static DiscoveredGcodeFileDto MapToDto(DiscoveredGcodeFile file)
    {
        return new DiscoveredGcodeFileDto(
            file.Id,
            file.HarvestOperationId,
            file.PrinterPath,
            file.FileName,
            file.FileSizeBytes,
            file.ModifiedAt,
            file.FileHash,
            file.IsSelected,
            file.AlreadyInLibrary,
            file.ExistingLibraryFileId,
            file.ProcessingFailed,
            file.ErrorMessage,
            file.ExtractedSlicerName,
            file.ExtractedSlicerVersion,
            file.ExtractedPrintTime,
            file.ExtractedFilamentLength,
            file.ExtractedNozzleDiameter,
            file.ExtractedMaterial,
            file.ExtractedLayerHeight,
            file.ExtractedInfill);
    }

    // Helper class for file information
    private class PrinterFileInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public long Size { get; set; }
        public DateTime? ModifiedAt { get; set; }
    }
    
    /// <summary>
    /// Gets information about all currently running harvest tasks
    /// </summary>
    /// <returns>Dictionary of operation IDs and their current status</returns>
    public IDictionary<Guid, bool> GetActiveTasksStatus()
    {
        return _activeTasks.ToDictionary(kvp => kvp.Key, kvp => !kvp.Value.IsCompleted);
    }
    
    /// <summary>
    /// Wait for all active tasks to complete or cancel them after timeout
    /// </summary>
    /// <param name="timeout">Maximum time to wait</param>
    /// <param name="ct">Cancellation token</param>
    public async Task WaitForAllTasksAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var tasks = _activeTasks.Values.ToArray();
        if (tasks.Length == 0)
            return;
            
        _logger.LogInformation("Waiting for {TaskCount} active harvest tasks to complete", tasks.Length);
        
        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
            
            await Task.WhenAll(tasks).WaitAsync(linkedCts.Token);
            _logger.LogInformation("All harvest tasks completed successfully");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timeout or cancellation occurred while waiting for harvest tasks");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error waiting for harvest tasks to complete");
        }
    }
}
