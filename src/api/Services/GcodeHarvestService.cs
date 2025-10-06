// ...existing code...
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.Models;
using Farm.Web.Shared;
using Microsoft.AspNetCore.SignalR;
using Farm.Web.Api.Hubs;
using Farm.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services;

/// <summary>
/// Service for harvesting G-code files from registered printers
/// </summary>
public partial class GcodeHarvestService(
    AppDbContext db,
    IMoonrakerClient moonraker,
    IPrusaLinkClient prusa,
    ISdcpClient sdcp,
    IUnifiedLoggingService logger,
    IServiceScopeFactory serviceScopeFactory,
    IHarvestQueue harvestQueue,
    IHubContext<HarvestHub> harvestHub) : IGcodeHarvestService
{
    public async Task<bool> SkipDiscoveredFileAsync(Guid operationId, Guid fileId, CancellationToken ct = default)
    {
        // Find the discovered file
        var file = await _db.HarvestDiscoveredFiles.FirstOrDefaultAsync(f => f.Id == fileId && f.HarvestOperationId == operationId, ct);
        if (file == null)
        {
            return false;
        }

        // Mark as skipped
        file.Status = HarvestFileStatus.Skipped;
        file.Error = "Skipped by user";
        await _db.SaveChangesAsync(ct);

        // Emit SignalR update to clients
        await _harvestHub.Clients.Group($"harvest-{operationId}")
            .SendAsync("HarvestFileUpdated", MapToDto(file), ct);

        return true;
    }

    public async Task<bool> RetryDiscoveredFileAsync(Guid operationId, Guid fileId, CancellationToken ct = default)
    {
        // Find the discovered file
        var file = await _db.HarvestDiscoveredFiles.FirstOrDefaultAsync(f => f.Id == fileId && f.HarvestOperationId == operationId, ct);
        if (file == null)
        {
            return false;
        }

        // Clear error and mark as pending
        file.Status = HarvestFileStatus.Pending;
        file.Error = null;
        await _db.SaveChangesAsync(ct);

        // Re-queue the file for processing (simulate as if it was just discovered)
        var op = await _db.GcodeHarvestOperations.FirstOrDefaultAsync(o => o.Id == operationId, ct);
        if (op == null)
        {
            return false;
        }

        var printer = await _db.Printers.FirstOrDefaultAsync(p => p.Id == op.PrinterId, ct);
        if (printer == null)
        {
            return false;
        }

        HarvestFileJob job = new()
        {
            OperationId = operationId,
            PrinterId = printer.Id,
            ServerUrl = printer.ServerUrl,
            FilePath = file.FilePath,
            FileName = file.FileName,
            FileSize = file.Size,
            ModifiedAt = file.ModifiedAt ?? DateTime.UtcNow
        };
        await _harvestQueue.EnqueueAsync(job);

        // Emit SignalR update to clients
        await _harvestHub.Clients.Group($"harvest-{operationId}")
            .SendAsync("HarvestFileUpdated", MapToDto(file), ct);

        return true;
    }
    private readonly AppDbContext _db = db;
    private readonly IMoonrakerClient _moonraker = moonraker;
    private readonly IPrusaLinkClient _prusa = prusa;
    private readonly ISdcpClient _sdcp = sdcp;
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly IServiceScopeFactory _serviceScopeFactory = serviceScopeFactory;
    private readonly IHarvestQueue _harvestQueue = harvestQueue;
    private readonly ConcurrentDictionary<Guid, Task> _activeTasks = new();
    private readonly IHubContext<HarvestHub> _harvestHub = harvestHub;

    private const string GcodeStoragePath = "gcode-library";
    private static readonly string[] sourceArray = { "gcode" };

    public async Task<GcodeHarvestResultDto> StartHarvestAsync(StartGcodeHarvestDto request, CancellationToken ct = default)
    {
        _logger.LogInformation($"🔥 StartHarvestAsync CALLED for printer ID: {(request?.PrinterId ?? Guid.Empty)}");
        ArgumentNullException.ThrowIfNull(request);
        Printer? printer = await _db.Printers.FirstOrDefaultAsync(p => p.Id == request.PrinterId, ct);
        _logger.LogInformation($"🔍 Found printer: {(printer?.Name ?? "NULL")} (ID: {(printer?.Id ?? Guid.Empty)})");
        if (printer == null)
        {
            return new GcodeHarvestResultDto(Guid.Empty, false, "Printer not found");
        }

        // Check if there's already an active harvest operation for this printer
        GcodeHarvestOperation? existingOperation = await _db.GcodeHarvestOperations
            .FirstOrDefaultAsync(h => h.PrinterId == request.PrinterId && h.Status == GcodeHarvestStatus.Running, ct);

        if (existingOperation != null)
        {
            return new GcodeHarvestResultDto(existingOperation.Id, false, $"Harvest operation already in progress for printer '{printer.Name}'. Please wait for it to complete or cancel it first.");
        }

        // Create harvest operation
        GcodeHarvestOperation operation = new()
        {
            Id = Guid.NewGuid(),
            PrinterId = request.PrinterId,
            StartedAt = DateTime.UtcNow,
            Status = GcodeHarvestStatus.Running,
            IncludeSubdirectories = request.IncludeSubdirectories,
            MaxFileSizeBytes = request.MaxFileSizeBytes,
            ModifiedAfter = request.ModifiedAfter,
            FileExtensions = request.FileExtensions,
            MinFileSizeBytes = request.MinFileSizeBytes,
            DuplicateHandling = request.DuplicateHandling,
            FilesFound = 0,
            FilesAdded = 0,
            FilesSkipped = 0,
            FilesErrored = 0,
            TotalBytesProcessed = 0
        };

        _db.GcodeHarvestOperations.Add(operation);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation($"Starting file discovery for operation {operation.Id} on printer {printer.Name}");

        // Start file discovery and queueing in background
        // Using a properly tracked task with error handling and using CancellationToken.None
        // to prevent cancellation when HTTP request completes
        Task backgroundTask = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation($"🚀 Background harvest task STARTED for operation {operation.Id} on printer {printer.Name}");
                await DiscoverAndQueueFilesAsync(operation, printer);
                _logger.LogInformation($"✅ Background harvest task COMPLETED successfully for operation {operation.Id}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Background harvest task FAILED for operation {operation.Id}: {ex.Message}");

                // Update the operation status to failed with detailed error info
                using IServiceScope scope = _serviceScopeFactory.CreateScope();
                AppDbContext scopedDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                GcodeHarvestOperation? dbOperation = await scopedDb.GcodeHarvestOperations
                    .FirstOrDefaultAsync(o => o.Id == operation.Id);
                if (dbOperation != null)
                {
                    HarvestErrorHelper.SetOperationError(
                        dbOperation,
                        ex,
                        nameof(HarvestErrorPhase.Discovery),
                        failedResource: printer.ServerUrl);
                    await scopedDb.SaveChangesAsync();
                    _logger.LogError($"💾 Updated operation {operation.Id} status to Failed in database");
                }
                else
                {
                    _logger.LogError($"⚠️ Could not find operation {operation.Id} in database to mark as failed");
                }
            }
            finally
            {
                // Remove from active tasks when done
                _activeTasks.TryRemove(operation.Id, out _);
                _logger.LogDebug($"Removed operation {operation.Id} from active tasks tracking");
            }
        }, CancellationToken.None);

        // Add to active tasks collection for tracking
        _activeTasks[operation.Id] = backgroundTask;
        _logger.LogDebug($"Added operation {operation.Id} to active tasks tracking");

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
        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        AppDbContext scopedDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IMoonrakerClient scopedMoonraker = scope.ServiceProvider.GetRequiredService<IMoonrakerClient>();
        IPrusaLinkClient scopedPrusa = scope.ServiceProvider.GetRequiredService<IPrusaLinkClient>();
        ISdcpClient scopedSdcp = scope.ServiceProvider.GetRequiredService<ISdcpClient>();
        var scopedLogger = scope.ServiceProvider.GetRequiredService<IUnifiedLoggingService>();

        try
        {
            scopedLogger.LogInformation($"Starting file discovery in scoped context for operation {operation.Id} on printer {printer.Name}");

            // Get file list from printer based on backend type
            PrinterBackend backend = (PrinterBackend)printer.Backend;
            scopedLogger.LogInformation($"Calling file discovery for backend {backend} on printer {printer.Name} at {printer.ServerUrl}");

            List<PrinterFileInfo> fileList;

            // Depending on printer backend, call the appropriate method to get files
            switch (backend)
            {
                case PrinterBackend.Moonraker:
                    scopedLogger.LogInformation($"Getting files from Moonraker backend at {printer.ServerUrl}");
                    fileList = await GetMoonrakerFilesAsync(printer.ServerUrl, scopedMoonraker, scopedLogger);
                    break;
                case PrinterBackend.PrusaLink:
                    scopedLogger.LogInformation($"Getting files from PrusaLink backend at {printer.ServerUrl}");
                    fileList = await GetPrusaLinkFilesAsync(printer.ServerUrl, printer.ApiKey, scopedPrusa, scopedLogger);
                    break;
                case PrinterBackend.SDCP:
                    scopedLogger.LogInformation($"Getting files from SDCP backend at {printer.ServerUrl}");
                    fileList = await GetSdcpFilesAsync(printer.ServerUrl, scopedSdcp, scopedLogger);
                    break;
                default:
                    scopedLogger.LogWarning($"Unsupported printer backend {backend}");
                    fileList = new List<PrinterFileInfo>();
                    break;
            }

            scopedLogger.LogInformation($"Discovered {fileList.Count} files for operation {operation.Id}");

            // Log sample filenames for debugging
            if (fileList.Count > 0)
            {
                string[] sampleFiles = fileList.Take(10).Select(f => $"{f.Name} (size: {f.Size})").ToArray();
                scopedLogger.LogInformation($"📄 Sample files: {string.Join(", ", sampleFiles)}");
            }

            // Determine allowed extensions (default to gcode if none specified)
            string[] allowedExts = (operation.FileExtensions != null && operation.FileExtensions.Length > 0
                ? operation.FileExtensions
                : sourceArray)
                .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
                .ToArray();

            // Apply filtering for extensions & size constraints for preliminary count
            bool PassesInitialFilters(PrinterFileInfo f)
            {
                scopedLogger.LogDebug($"🔍 Filtering file: '{f.Name}' (Size: {f.Size})");

                string nameLower = f.Name.ToLowerInvariant();
                bool extOk = false;
                foreach (string? ext in allowedExts)
                {
                    if (nameLower.EndsWith(ext))
                    {
                        extOk = true;
                        scopedLogger.LogDebug($"✅ File '{f.Name}' matches extension '{ext}'");
                        break;
                    }
                }
                if (!extOk)
                {
                    scopedLogger.LogDebug($"❌ File '{f.Name}' rejected: extension doesn't match any of [{string.Join(", ", allowedExts)}]");
                    return false;
                }
                if (operation.MinFileSizeBytes.HasValue && f.Size < operation.MinFileSizeBytes.Value)
                {
                    scopedLogger.LogDebug($"❌ File '{f.Name}' rejected: size {f.Size} < minimum {operation.MinFileSizeBytes.Value}");
                    return false;
                }
                if (operation.MaxFileSizeBytes.HasValue && f.Size > operation.MaxFileSizeBytes.Value)
                {
                    scopedLogger.LogDebug($"❌ File '{f.Name}' rejected: size {f.Size} > maximum {operation.MaxFileSizeBytes.Value}");
                    return false;
                }
                if (operation.ModifiedAfter.HasValue && f.ModifiedAt.HasValue && f.ModifiedAt < operation.ModifiedAfter)
                {
                    scopedLogger.LogDebug($"❌ File '{f.Name}' rejected: modified {f.ModifiedAt} < required {operation.ModifiedAfter}");
                    return false;
                }
                scopedLogger.LogDebug($"✅ File '{f.Name}' passed all filters");
                return true;
            }

            List<PrinterFileInfo> filteredFiles = fileList.Where(PassesInitialFilters).ToList();
            int gcodeFileCount = filteredFiles.Count;
            scopedLogger.LogInformation($"Filtered to {gcodeFileCount} candidate files (from {fileList.Count}). Extensions: {string.Join(",", allowedExts)}");

            // Update files found count immediately
            GcodeHarvestOperation? dbOperation = await scopedDb.GcodeHarvestOperations
                .FirstOrDefaultAsync(o => o.Id == operation.Id);
            if (dbOperation != null)
            {
                dbOperation.FilesFound = gcodeFileCount;
                await scopedDb.SaveChangesAsync();
                scopedLogger.LogInformation($"Updated operation {operation.Id} with {dbOperation.FilesFound} G-code files found");
            }
            else
            {
                scopedLogger.LogWarning($"Could not find operation {operation.Id} in database to update files found count");
            }

            // Queue each G-code file for processing
            int queuedCount = 0;
            foreach (PrinterFileInfo? fileInfo in filteredFiles)
            {
                HarvestFileJob job = new()
                {
                    OperationId = operation.Id,
                    PrinterId = printer.Id,
                    ServerUrl = printer.ServerUrl,
                    FilePath = fileInfo.Path,
                    FileName = fileInfo.Name,
                    FileSize = fileInfo.Size,
                    ModifiedAt = fileInfo.ModifiedAt,
                    
                    // Pass metadata from API (avoids downloading files during processing)
                    SlicerName = fileInfo.SlicerName,
                    SlicerVersion = fileInfo.SlicerVersion,
                    EstimatedTimeSeconds = fileInfo.EstimatedTimeSeconds,
                    FilamentLengthMm = fileInfo.FilamentLengthMm,
                    FilamentWeightGrams = fileInfo.FilamentWeightGrams,
                    LayerHeight = fileInfo.LayerHeight,
                    FirstLayerHeight = fileInfo.FirstLayerHeight,
                    ObjectHeight = fileInfo.ObjectHeight,
                    FirstLayerBedTemp = fileInfo.FirstLayerBedTemp,
                    FirstLayerExtrTemp = fileInfo.FirstLayerExtrTemp,
                    ThumbnailRelativePath = fileInfo.ThumbnailRelativePath
                };

                scopedLogger.LogDebug($"Queueing file {fileInfo.Name} with path {fileInfo.Path}");
                await _harvestQueue.EnqueueAsync(job);
                queuedCount++;

                if (queuedCount % 10 == 0)
                {
                    scopedLogger.LogInformation($"Queued {queuedCount} files so far for operation {operation.Id}");
                }
            }

            scopedLogger.LogInformation($"Queued {queuedCount} files for processing in operation {operation.Id}");

            // Check how many discovered files already exist
            int existingFiles = await scopedDb.HarvestDiscoveredFiles
                .Where(d => d.HarvestOperationId == operation.Id)
                .CountAsync();

            scopedLogger.LogInformation($"Found {existingFiles} existing discovered files for operation {operation.Id}");

            // If no files were queued, mark operation as completed
            if (queuedCount == 0 && dbOperation != null)
            {
                dbOperation.Status = GcodeHarvestStatus.Completed;
                dbOperation.CompletedAt = DateTime.UtcNow;
                await scopedDb.SaveChangesAsync();
                scopedLogger.LogInformation($"Operation {operation.Id} completed with no files to process");
            }
        }
        catch (Exception ex)
        {
            scopedLogger.LogError(ex, $"File discovery failed for operation {operation.Id}");

            // Mark operation as failed with detailed error info
            GcodeHarvestOperation? dbOperation = await scopedDb.GcodeHarvestOperations
                .FirstOrDefaultAsync(o => o.Id == operation.Id);
            if (dbOperation != null)
            {
                HarvestErrorHelper.SetOperationError(
                    dbOperation,
                    ex,
                    nameof(HarvestErrorPhase.Discovery),
                    failedResource: printer.ServerUrl);
                await scopedDb.SaveChangesAsync();
            }
        }
    }

    private async Task<MemoryStream?> DownloadFileAsync(PrinterBackend backend, Printer printer, string filePath, IMoonrakerClient? moonraker, IPrusaLinkClient? prusa, ISdcpClient? sdcp)
    {
        IUnifiedLoggingService log = _logger;
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
        return await DownloadFileAsync(backend, printer, filePath, _moonraker, _prusa, _sdcp);
    }

    public async Task<GcodeMetadataDto> ExtractMetadataAsync(Stream gcodeStream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gcodeStream);
        GcodeMetadataDto metadata = new();

        using StreamReader reader = new(gcodeStream, leaveOpen: true);

        // Read first few hundred lines to get slicer comments
        int linesRead = 0;
        int maxLines = 500;

        while (linesRead < maxLines && !reader.EndOfStream)
        {
            string? line = await reader.ReadLineAsync(ct);
            if (line == null)
            {
                break;
            }

            linesRead++;

            // Skip non-comment lines after header
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
            Match versionMatch = Regex.Match(content, @"Cura_SteamEngine (\S+)");
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
        Match match = Regex.Match(content, pattern, RegexOptions.IgnoreCase);
        return match.Success ? apply(match) : metadata;
    }

    public async Task<string> CalculateFileHashAsync(Stream fileStream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileStream);
        using SHA256 sha256 = SHA256.Create();
        byte[] hash = await sha256.ComputeHashAsync(fileStream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<GcodeHarvestOperationDto?> GetHarvestOperationAsync(Guid operationId, CancellationToken ct = default)
    {
        GcodeHarvestOperation? operation = await _db.GcodeHarvestOperations
            .Include(h => h.Printer)
            .FirstOrDefaultAsync(h => h.Id == operationId, ct);

        return operation == null ? null : MapToDto(operation);
    }

    public async Task<DiscoveredGcodeFileDto[]> GetDiscoveredFilesAsync(Guid operationId, CancellationToken ct = default)
    {
        _logger.LogInformation($"Getting discovered files for operation {operationId}");

        // Verify the operation exists
        GcodeHarvestOperation? operation = await _db.GcodeHarvestOperations.FirstOrDefaultAsync(o => o.Id == operationId, ct);
        if (operation == null)
        {
            _logger.LogWarning($"GetDiscoveredFilesAsync: Operation {operationId} not found");
            return Array.Empty<DiscoveredGcodeFileDto>();
        }

        _logger.LogInformation($"Found operation {operationId} with status {operation.Status}, files found: {operation.FilesFound}");

        // Get files with explicit logging
        HarvestDiscoveredFile[] files = await _db.HarvestDiscoveredFiles
                .Where(d => d.HarvestOperationId == operationId)
                .OrderBy(d => d.FileName)
                .ToArrayAsync(ct);

        _logger.LogInformation($"Found {files.Length} discovered files for operation {operationId}");

        return files.Select(MapToDto).ToArray();
    }

    public async Task<PagedResult<DiscoveredGcodeFileDto>> GetDiscoveredFilesPagedAsync(Guid operationId, int page = 1, int pageSize = 50, string? search = null, CancellationToken ct = default)
    {
        if (page < 1)
        {
            page = 1;
        }
        if (pageSize < 1)
        {
            pageSize = 1;
        }
        if (pageSize > 500)
        {
            pageSize = 500; // guardrail
        }

        IQueryable<HarvestDiscoveredFile> baseQuery = _db.HarvestDiscoveredFiles.AsQueryable().Where(d => d.HarvestOperationId == operationId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            string term = search.Trim();
            baseQuery = baseQuery.Where(d => d.FileName.Contains(term));
        }

        int total = await baseQuery.CountAsync(ct);
        int totalPages = (int)Math.Ceiling(total / (double)pageSize);
        if (totalPages == 0)
        {
            totalPages = 1;
        }
        if (page > totalPages)
        {
            page = totalPages;
        }

        HarvestDiscoveredFile[] items = await baseQuery
                .OrderBy(d => d.FileName)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToArrayAsync(ct);

        return new PagedResult<DiscoveredGcodeFileDto>(
            items.Select(MapToDto).ToList(),
            total,
            page,
            pageSize,
            totalPages);
    }

    public async Task<GcodeHarvestResultDto> ImportSelectedFilesAsync(ImportSelectedGcodeFilesDto request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        GcodeHarvestOperation? operation = await _db.GcodeHarvestOperations
            .Include(h => h.Printer)
            .FirstOrDefaultAsync(h => h.Id == request.HarvestOperationId, ct);

        if (operation == null)
        {
            return new GcodeHarvestResultDto(request.HarvestOperationId, false, "Harvest operation not found");
        }

        HarvestDiscoveredFile[] selectedFiles = await _db.HarvestDiscoveredFiles
                .Where(d => request.SelectedFileIds.Contains(d.Id))
                .ToArrayAsync(ct);

        List<string> errors = new();
        int importedCount = 0;

        foreach (HarvestDiscoveredFile? discoveredFile in selectedFiles)
        {
            try
            {
                if (discoveredFile.AlreadyInLibrary)
                {
                    continue; // Skip files already in library
                }

                // Mark as in progress and emit update
                discoveredFile.Status = HarvestFileStatus.InProgress;
                discoveredFile.StartedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                await _harvestHub.Clients.Group($"harvest-{operation.Id}")
                    .SendAsync("HarvestFileUpdated", MapToDto(discoveredFile), ct);

                // Create storage directory if needed
                using IServiceScope serviceScope = _serviceScopeFactory.CreateScope();
                IWebHostEnvironment environment = serviceScope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
                string storageDir = Path.Combine(environment.ContentRootPath, "wwwroot", GcodeStoragePath);
                Directory.CreateDirectory(storageDir);

                // Generate unique filename
                string fileName = $"{Guid.NewGuid()}_{discoveredFile.FileName}";
                string filePath = Path.Combine(storageDir, fileName);

                // Download file from printer
                PrinterBackend backend = (PrinterBackend)operation.Printer.Backend;
                using MemoryStream? gcodeContent = await DownloadFileAsync(backend, operation.Printer, discoveredFile.FilePath);

                if (gcodeContent == null)
                {
                    discoveredFile.Status = HarvestFileStatus.Failed;
                    discoveredFile.Error = $"Failed to download {discoveredFile.FileName}";
                    discoveredFile.CompletedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct);
                    await _harvestHub.Clients.Group($"harvest-{operation.Id}")
                        .SendAsync("HarvestFileUpdated", MapToDto(discoveredFile), ct);
                    errors.Add($"Failed to download {discoveredFile.FileName}");
                    continue;
                }

                // Save to local storage
                await using (FileStream fileStream = File.Create(filePath))
                {
                    gcodeContent.Position = 0;
                    const int bufferSize = 64 * 1024; // 64KB
                    byte[] buffer = new byte[bufferSize];
                    long totalBytes = gcodeContent.Length;
                    long bytesCopied = 0;
                    int read;
                    while ((read = await gcodeContent.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                        bytesCopied += read;
                        // Emit progress update every 512KB or on completion
                        if (bytesCopied == totalBytes || bytesCopied % (512 * 1024) < bufferSize)
                        {
                            await _harvestHub.Clients.Group($"harvest-{operation.Id}")
                                .SendAsync(
                                    "HarvestFileProgress",
                                    new
                                    {
                                        operationId = operation.Id,
                                        fileName = discoveredFile.FileName,
                                        bytesCopied,
                                        totalBytes,
                                        percent = totalBytes > 0 ? (bytesCopied * 100.0 / totalBytes) : 0
                                    },
                                    ct
                                );
                        }
                    }
                }

                // Mark as complete and emit update
                discoveredFile.Status = HarvestFileStatus.Complete;
                discoveredFile.CompletedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                await _harvestHub.Clients.Group($"harvest-{operation.Id}")
                    .SendAsync("HarvestFileUpdated", MapToDto(discoveredFile), ct);

                // Create library entry
                GcodeFile gcodeFile = new()
                {
                    Id = Guid.NewGuid(),
                    OriginalFileName = discoveredFile.FileName,
                    DisplayName = Path.GetFileNameWithoutExtension(discoveredFile.FileName),
                    FilePath = filePath,
                    FileSizeBytes = discoveredFile.Size,
                    FileHash = discoveredFile.FileHash ?? "",
                    UploadedAt = DateTime.UtcNow,
                    Source = GcodeSource.Harvested,
                    SourcePrinterId = operation.PrinterId,
                    OriginalPrinterPath = discoveredFile.FilePath,
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
                discoveredFile.Status = HarvestFileStatus.Failed;
                discoveredFile.Error = $"Failed to import {discoveredFile.FileName}: {ex.Message}";
                discoveredFile.CompletedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                await _harvestHub.Clients.Group($"harvest-{operation.Id}")
                    .SendAsync("HarvestFileUpdated", MapToDto(discoveredFile), ct);
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
        GcodeHarvestOperation? operation = await _db.GcodeHarvestOperations
            .FirstOrDefaultAsync(h => h.Id == operationId, ct);

        if (operation == null || operation.Status != GcodeHarvestStatus.Running)
        {
            return false;
        }

        operation.Status = GcodeHarvestStatus.Cancelled;
        operation.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Log the cancellation for tracking purposes
        _logger.LogInformation($"Harvest operation {operationId} was cancelled");

        // Note: We don't actually cancel the task because Task.Run doesn't support 
        // cancellation after it's started. The background task will check the 
        // operation status and exit gracefully when it sees the Cancelled status.

        return true;
    }

    public async Task<GcodeHarvestOperationDto?> GetActiveHarvestAsync(Guid printerId, CancellationToken ct = default)
    {
        GcodeHarvestOperation? operation = await _db.GcodeHarvestOperations
            .Include(h => h.Printer)
            .FirstOrDefaultAsync(h => h.PrinterId == printerId && h.Status == GcodeHarvestStatus.Running, ct);

        return operation == null ? null : MapToDto(operation);
    }

    public async Task<GcodeHarvestOperationDto[]> GetRecentHarvestsAsync(Guid printerId, int count = 10, CancellationToken ct = default)
    {
        GcodeHarvestOperation[] operations = await _db.GcodeHarvestOperations
            .Include(h => h.Printer)
            .Where(h => h.PrinterId == printerId)
            .OrderByDescending(h => h.StartedAt)
            .Take(count)
            .ToArrayAsync(ct);

        return operations.Select(MapToDto).ToArray();
    }

    public async Task<GcodeHarvestOperationDto[]> GetActiveHarvestsAsync(CancellationToken ct = default)
    {
        GcodeHarvestOperation[] operations = await _db.GcodeHarvestOperations
            .Include(h => h.Printer)
            .Where(h => h.Status == GcodeHarvestStatus.Running)
            .OrderByDescending(h => h.StartedAt)
            .ToArrayAsync(ct);

        return operations.Select(MapToDto).ToArray();
    }

    public async Task<GcodeHarvestOperationDto[]> GetHarvestOperationsAsync(Guid? printerId = null, string? status = null, int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        IQueryable<GcodeHarvestOperation> query = _db.GcodeHarvestOperations
            .Include(h => h.Printer)
            .AsQueryable();

        // Apply filters
        if (printerId.HasValue)
        {
            query = query.Where(h => h.PrinterId == printerId.Value);
        }

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<GcodeHarvestStatus>(status, true, out GcodeHarvestStatus statusEnum))
        {
            query = query.Where(h => h.Status == statusEnum);
        }

        // Apply pagination and ordering
        GcodeHarvestOperation[] operations = await query
            .OrderByDescending(h => h.StartedAt)
            .Skip(offset)
            .Take(limit)
            .ToArrayAsync(ct);

        return operations.Select(MapToDto).ToArray();
    }

    // (moved below to be adjacent to the other overload)

    private async Task<MemoryStream?> DownloadMoonrakerFileAsync(string serverUrl, string filePath, IMoonrakerClient? moonraker = null, IUnifiedLoggingService? logger = null)
    {
        IUnifiedLoggingService log = logger ?? _logger;
        IMoonrakerClient client = moonraker ?? _moonraker;

        try
        {
            log.LogInformation("Downloading file {FilePath} from Moonraker at {ServerUrl}", filePath, serverUrl);
            byte[]? bytes = await client.DownloadFileAsync(serverUrl, filePath);
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

    private async Task<MemoryStream?> DownloadPrusaLinkFileAsync(string serverUrl, string filePath, IPrusaLinkClient? prusa = null, IUnifiedLoggingService? logger = null)
    {
        IUnifiedLoggingService log = logger ?? _logger;
        _ = prusa; // explicitly discard unused optional client parameter

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

    // Overload to satisfy call sites that include an apiKey param (currently unused by implementation)
    private Task<MemoryStream?> DownloadPrusaLinkFileAsync(string serverUrl, string? apiKey, string filePath, IPrusaLinkClient? prusa = null, IUnifiedLoggingService? logger = null)
    {
        _ = apiKey; // explicitly discard unused
        return DownloadPrusaLinkFileAsync(serverUrl, filePath, prusa, logger);
    }

    private async Task<MemoryStream?> DownloadSdcpFileAsync(string serverUrl, string filePath, IUnifiedLoggingService? logger = null)
    {
        IUnifiedLoggingService log = logger ?? _logger;
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

    // Overload to satisfy call sites expecting a client and pass-through logger
    private Task<MemoryStream?> DownloadSdcpFileAsync(string serverUrl, string filePath, ISdcpClient? sdcp, IUnifiedLoggingService? logger = null)
    {
        _ = sdcp; // explicitly discard unused
        return DownloadSdcpFileAsync(serverUrl, filePath, logger);
    }

    // Helper methods for different printer backends
    private async Task<List<PrinterFileInfo>> GetMoonrakerFilesAsync(string serverUrl, IMoonrakerClient? moonraker = null, IUnifiedLoggingService? logger = null)
    {
        IMoonrakerClient client = moonraker ?? _moonraker;
        IUnifiedLoggingService log = logger ?? _logger;

        try
        {
            log.LogInformation("GetMoonrakerFilesAsync starting for {ServerUrl} with retry logic", serverUrl);
            List<PrinterFileInfo> files = new();

            // Get the gcodes directory listing with retry
            log.LogInformation("Calling GetDirectoryAsync for gcodes directory with retry");
            DirectoryInfo? directoryInfo = await RetryPolicyHelper.ExecuteWithRetryAsync(
                () => client.GetDirectoryAsync(serverUrl, "gcodes", extended: true),
                logger: null,
                operationName: $"GetDirectoryAsync for gcodes directory at {serverUrl}");

            log.LogInformation("GetDirectoryAsync completed, directoryInfo is {IsNull}", directoryInfo == null ? "null" : "not null");

            if (directoryInfo != null)
            {
                int fileCount = directoryInfo.Files?.Length ?? 0;
                int dirCount = directoryInfo.Dirs?.Length ?? 0;
                log.LogInformation($"🔍 Directory has {fileCount} files and {dirCount} subdirectories");

                log.LogInformation($"🚀 Starting CollectFilesRecursivelyWithRetryAsync with empty list (current count: {files.Count})");
                await CollectFilesRecursivelyWithRetryAsync(files, directoryInfo, "gcodes", serverUrl, client, log);
                log.LogInformation($"✅ Completed CollectFilesRecursivelyWithRetryAsync, list now has {files.Count} files");
            }

            log.LogInformation($"🏁 Found {files.Count} files in Moonraker at {serverUrl}");
            return files; // List<PrinterFileInfo>
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to get file list from Moonraker at {ServerUrl}", serverUrl);
            return new();
        }
    }

    private static async Task CollectFilesRecursivelyWithRetryAsync(List<PrinterFileInfo> files, DirectoryInfo directory, string basePath, string serverUrl, IMoonrakerClient client, IUnifiedLoggingService log)
    {
        log.LogInformation("🔍 CollectFilesRecursivelyWithRetryAsync called for {BasePath}, starting with {CurrentFileCount} files", basePath, files.Count);

        // Add files from current directory
        if (directory.Files != null)
        {
            log.LogInformation($"📁 Processing {directory.Files.Length} files in directory {basePath}");
            foreach (MoonrakerFileInfo file in directory.Files)
            {
                PrinterFileInfo printerFileInfo = new()
                {
                    Name = System.IO.Path.GetFileName(file.Path),
                    Path = file.Path,
                    Size = file.Size,
                    ModifiedAt = DateTimeOffset.FromUnixTimeSeconds((long)file.Modified).DateTime
                };
                
                // Optimization: Fetch metadata from Moonraker API instead of downloading the file
                // This avoids transferring potentially large files over the network just to read metadata
                try
                {
                    GCodeMetadata? metadata = await RetryPolicyHelper.ExecuteWithRetryAsync(
                        () => client.GetFileMetadataAsync(serverUrl, file.Path),
                        logger: null,
                        operationName: $"GetFileMetadataAsync for {file.Path}");
                    
                    if (metadata != null)
                    {
                        printerFileInfo.SlicerName = metadata.Slicer;
                        printerFileInfo.SlicerVersion = metadata.SlicerVersion;
                        printerFileInfo.EstimatedTimeSeconds = metadata.EstimatedTime;
                        printerFileInfo.FilamentLengthMm = metadata.FilamentTotal;
                        printerFileInfo.FilamentWeightGrams = metadata.FilamentWeightTotal;
                        printerFileInfo.LayerHeight = metadata.LayerHeight;
                        printerFileInfo.FirstLayerHeight = metadata.FirstLayerHeight;
                        printerFileInfo.ObjectHeight = metadata.ObjectHeight;
                        printerFileInfo.FirstLayerBedTemp = metadata.FirstLayerBedTemp;
                        printerFileInfo.FirstLayerExtrTemp = metadata.FirstLayerExtrTemp;
                        
                        // Extract largest thumbnail path if available
                        if (metadata.Thumbnails != null && metadata.Thumbnails.Length > 0)
                        {
                            ThumbnailInfo largest = metadata.Thumbnails
                                .OrderByDescending(t => t.Width * t.Height)
                                .First();
                            printerFileInfo.ThumbnailRelativePath = largest.RelativePath;
                        }
                        
                        log.LogDebug($"✅ Fetched metadata for {printerFileInfo.Name}: Slicer={metadata.Slicer ?? "Unknown"}, Time={metadata.EstimatedTime ?? 0}s", null, null);
                    }
                    else
                    {
                        log.LogDebug("⚠️ No metadata available for {FileName}", printerFileInfo.Name);
                    }
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "⚠️ Failed to fetch metadata for {FileName}, will extract during processing if needed", printerFileInfo.Name);
                    // Continue without metadata - file will still be discovered
                }
                
                files.Add(printerFileInfo);
                log.LogDebug("➕ Added file: {FileName} (Size: {Size} bytes)", printerFileInfo.Name, printerFileInfo.Size);
            }
            log.LogInformation($"✅ Added {directory.Files.Length} files from {basePath}, total now: {files.Count}");
        }
        else
        {
            log.LogInformation("📂 No files in directory {BasePath}", basePath);
        }

        // Recursively process subdirectories
        if (directory.Dirs != null && directory.Dirs.Length > 0)
        {
            log.LogInformation($"📁 Processing {directory.Dirs.Length} subdirectories in {basePath}");
            foreach (DirectoryInfo subDir in directory.Dirs)
            {
                try
                {
                    // Use the dirname field from Moonraker response for directory names
                    string dirName = !string.IsNullOrEmpty(subDir.Dirname) ? subDir.Dirname : System.IO.Path.GetFileName(subDir.Path);
                    string subDirPath = $"{basePath}/{dirName}";
                    log.LogInformation("🔍 Processing subdirectory {DirName} -> {SubDirPath}", dirName, subDirPath);

                    // Get subdirectory info with retry
                    DirectoryInfo? subDirInfo = await RetryPolicyHelper.ExecuteWithRetryAsync(
                        () => client.GetDirectoryAsync(serverUrl, subDirPath, extended: true),
                        logger: null,
                        operationName: $"GetDirectoryAsync for subdirectory {subDirPath}");

                    if (subDirInfo != null)
                    {
                        await CollectFilesRecursivelyWithRetryAsync(files, subDirInfo, subDirPath, serverUrl, client, log);
                    }
                    else
                    {
                        log.LogWarning("⚠️ Subdirectory {SubDirPath} returned null", subDirPath);
                    }
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "❌ Error processing subdirectory {SubDirPath}", subDir.Path);
                    // Continue with next subdirectory
                }
            }
        }
        else
        {
            log.LogInformation("📂 No subdirectories in {BasePath}", basePath);
        }

        log.LogInformation("🏁 CollectFilesRecursivelyWithRetryAsync completed for {BasePath}, total files: {TotalCount}", basePath, files.Count);
    }

    // Simple overload kept adjacent for analyzer friendliness
    private async Task CollectFilesRecursivelyAsync(List<PrinterFileInfo> files, DirectoryInfo directory, string basePath, string serverUrl)
    {
        // Add files from current directory
        if (directory.Files != null)
        {
            foreach (MoonrakerFileInfo file in directory.Files)
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
            foreach (DirectoryInfo subDir in directory.Dirs)
            {
                try
                {
                    string subDirPath = $"{basePath}/{subDir.Path}";
                    DirectoryInfo? subDirectoryInfo = await _moonraker.GetDirectoryAsync(serverUrl, subDirPath, extended: true);
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
    }

    private static async Task CollectFilesRecursivelyAsync(List<PrinterFileInfo> files, DirectoryInfo directory, string basePath, string serverUrl, IMoonrakerClient moonraker, IUnifiedLoggingService logger)
    {
        // Add files from current directory
        if (directory.Files != null)
        {
            foreach (MoonrakerFileInfo file in directory.Files)
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
            foreach (DirectoryInfo subDir in directory.Dirs)
            {
                try
                {
                    string subDirPath = $"{basePath}/{subDir.Path}";
                    DirectoryInfo? subDirectoryInfo = await moonraker.GetDirectoryAsync(serverUrl, subDirPath, extended: true);
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

    private async Task<List<PrinterFileInfo>> GetPrusaLinkFilesAsync(string serverUrl, string? apiKey, IPrusaLinkClient? prusa = null, IUnifiedLoggingService? logger = null)
    {
        IPrusaLinkClient client = prusa ?? _prusa;
        IUnifiedLoggingService log = logger ?? _logger;

        try
        {
            string[] fileNames = await client.GetFileListAsync(serverUrl, apiKey);
            List<PrinterFileInfo> files = fileNames.Select(fileName => new PrinterFileInfo
            {
                Name = fileName,
                Path = fileName,
                Size = 0, // PrusaLink basic API doesn't provide size info
                ModifiedAt = null // PrusaLink basic API doesn't provide modification date
            }).ToList();

            log.LogInformation($"Found {files.Count} files in PrusaLink at {serverUrl}");
            return files;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to get file list from PrusaLink at {ServerUrl}", serverUrl);
            return new List<PrinterFileInfo>();
        }
    }

    private async Task<List<PrinterFileInfo>> GetSdcpFilesAsync(string serverUrl, IUnifiedLoggingService? logger = null)
    {
        IUnifiedLoggingService log = logger ?? _logger;
        try
        {
            // SDCP implementation would go here
            log.LogInformation($"SDCP file listing not yet implemented for {serverUrl}");
            await Task.Delay(100); // Adding await to fix the warning
            return new List<PrinterFileInfo>();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to get file list from SDCP at {ServerUrl}", serverUrl);
            return new List<PrinterFileInfo>();
        }
    }

    // Overload to satisfy call sites expecting a client and pass-through logger
    private Task<List<PrinterFileInfo>> GetSdcpFilesAsync(string serverUrl, ISdcpClient? sdcp, IUnifiedLoggingService? logger = null)
    {
        _ = sdcp; // explicitly discard unused
        return GetSdcpFilesAsync(serverUrl, logger);
    }

    private static GcodeHarvestOperationDto MapToDto(GcodeHarvestOperation operation)
    {
        // Calculate files processed (same logic as HarvestCompletionService)
        int filesProcessed = operation.FilesAdded + operation.FilesSkipped + operation.FilesErrored;

        return new GcodeHarvestOperationDto(
            operation.Id,
            operation.PrinterId,
            operation.Printer?.Name ?? "Unknown",
            operation.StartedAt,
            operation.CompletedAt,
            MapStatus(operation.Status),
            operation.ErrorMessage,
            operation.ErrorType,
            operation.ErrorPhase,
            operation.ErrorDetails,
            operation.FailedResource,
            operation.IsRetryable,
            operation.ErrorOccurredAt,
            operation.FilesFound,
            filesProcessed, // Include calculated FilesProcessed
            operation.FilesAdded,
            operation.FilesSkipped,
            operation.FilesErrored,
            operation.TotalBytesProcessed,
            operation.IncludeSubdirectories,
            operation.MaxFileSizeBytes,
            operation.ModifiedAfter);
    }

    private static GcodeHarvestStatusDto MapStatus(GcodeHarvestStatus status)
    {
        return status switch
        {
            GcodeHarvestStatus.Running => GcodeHarvestStatusDto.Running,
            GcodeHarvestStatus.Completed => GcodeHarvestStatusDto.Completed,
            GcodeHarvestStatus.Failed => GcodeHarvestStatusDto.Failed,
            GcodeHarvestStatus.Cancelled => GcodeHarvestStatusDto.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown harvest status")
        };
    }

    private static DiscoveredGcodeFileDto MapToDto(HarvestDiscoveredFile file)
    {
        return new DiscoveredGcodeFileDto(
            file.Id,
            file.HarvestOperationId,
            file.FilePath, // PrinterPath
            file.FileName,
            file.Size, // FileSizeBytes
            file.ModifiedAt, // ModifiedAt
            file.FileHash, // FileHash
            false, // IsSelected (not persisted, always false from backend)
            file.AlreadyInLibrary, // AlreadyInLibrary
            null, // ExistingLibraryFileId (not available)
            file.Status == HarvestFileStatus.Failed, // ProcessingFailed
            file.Error, // ErrorMessage
            file.ThumbnailUrl, // ThumbnailUrl
            file.ExtractedSlicerName,
            file.ExtractedSlicerVersion,
            file.ExtractedPrintTime,
            file.ExtractedFilamentLength,
            file.ExtractedNozzleDiameter,
            file.ExtractedMaterial,
            null, // ExtractedLayerHeight (not available)
            null  // ExtractedInfill (not available)
        );
    }

    // Helper class for file information
    private sealed class PrinterFileInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public long Size { get; set; }
        public DateTime? ModifiedAt { get; set; }
        
        // Metadata from API (populated during discovery for backends that support it)
        // This avoids downloading files just to extract metadata
        public string? SlicerName { get; set; }
        public string? SlicerVersion { get; set; }
        public int? EstimatedTimeSeconds { get; set; }
        public double? FilamentLengthMm { get; set; }
        public double? FilamentWeightGrams { get; set; }
        public double? LayerHeight { get; set; }
        public double? FirstLayerHeight { get; set; }
        public double? ObjectHeight { get; set; }
        public double? FirstLayerBedTemp { get; set; }
        public double? FirstLayerExtrTemp { get; set; }
        public string? ThumbnailRelativePath { get; set; } // Path to largest thumbnail
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
        Task[] tasks = _activeTasks.Values.ToArray();
        if (tasks.Length == 0)
        {
            return;
        }

        _logger.LogInformation($"Waiting for {tasks.Length} active harvest tasks to complete");

        try
        {
            using CancellationTokenSource timeoutCts = new(timeout);
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);

            await Task.WhenAll(tasks).WaitAsync(linkedCts.Token);
            _logger.LogInformation($"All harvest tasks completed successfully");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning($"Timeout or cancellation occurred while waiting for harvest tasks");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error waiting for harvest tasks to complete");
        }
    }

    [GeneratedRegex(@"PrusaSlicer (\S+)")]
    private static partial Regex MyRegex();
}
