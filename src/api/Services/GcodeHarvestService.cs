// ...existing code...
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Gcode;
using Farm.Infrastructure.Repositories.Harvest;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Resilience;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using MoonrakerDir = Farm.Infrastructure.Contracts.Printers.Moonraker.MoonrakerDirectoryInfo;

namespace Farm.Web.Api.Services;

/// <summary>
/// Service for harvesting G-code files from registered printers
/// </summary>
public partial class GcodeHarvestService(
    IHarvestRepository harvestRepo,
    IPrintersRepository printersRepo,
    IGcodeRepository gcodeRepo,
    IUnifiedLoggingService logger,
    IServiceScopeFactory serviceScopeFactory,
    IHarvestQueue harvestQueue,
    IHubContext<HarvestHub> harvestHub,
    IGcodeMetadataExtractorService metadataExtractor,
    StorageManagement.IStoragePathService storagePathService,
    FileManagement.IGcodeThumbnailExtractorService thumbnailExtractor,
    IOptions<GcodeHarvestSettings> harvestOptions,
    IBackendCapabilityFactory capabilityFactory) : IGcodeHarvestService
{
    public async Task<bool> SkipDiscoveredFileAsync(Guid operationId, Guid fileId, CancellationToken ct = default)
    {
        // Find the discovered file
        HarvestDiscoveredFile? file = await _harvestRepo.GetDiscoveredFileByIdAsync(fileId, operationId, ct);
        if (file == null)
        {
            return false;
        }

        // Mark as skipped
        file.Status = HarvestFileStatus.Skipped;
        file.Error = "Skipped by user";
        await _harvestRepo.SaveChangesAsync(ct);

        // Emit SignalR update to clients
        await _harvestHub.Clients.Group($"harvest-{operationId}")
            .SendAsync("harvestfileupdated", MapToDto(file), ct);

        return true;
    }

    public async Task<bool> RetryDiscoveredFileAsync(Guid operationId, Guid fileId, CancellationToken ct = default)
    {
        // Find the discovered file
        HarvestDiscoveredFile? file = await _harvestRepo.GetDiscoveredFileByIdAsync(fileId, operationId, ct);
        if (file == null)
        {
            return false;
        }

        // Clear error and mark as pending
        file.Status = HarvestFileStatus.Pending;
        file.Error = null;
        await _harvestRepo.SaveChangesAsync(ct);

        // Re-queue the file for processing (simulate as if it was just discovered)
        GcodeHarvestOperation? op = await _harvestRepo.GetOperationByIdAsync(operationId, ct);
        if (op == null)
        {
            return false;
        }

        Printer? printer = await _printersRepo.FindByIdAsync(op.PrinterId, ct);
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
        await _harvestQueue.EnqueueAsync(job, ct);

        // Emit SignalR update to clients
        await _harvestHub.Clients.Group($"harvest-{operationId}")
            .SendAsync("harvestfileupdated", MapToDto(file), ct);

        return true;
    }
    private readonly IHarvestRepository _harvestRepo = harvestRepo;
    private readonly IPrintersRepository _printersRepo = printersRepo;
    private readonly IGcodeRepository _gcodeRepo = gcodeRepo;
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly IServiceScopeFactory _serviceScopeFactory = serviceScopeFactory;
    private readonly IHarvestQueue _harvestQueue = harvestQueue;
    private readonly ConcurrentDictionary<Guid, Task> _activeTasks = new();
    private readonly IHubContext<HarvestHub> _harvestHub = harvestHub;
    private readonly IGcodeMetadataExtractorService _metadataExtractor = metadataExtractor;
    private readonly StorageManagement.IStoragePathService _storagePathService = storagePathService;
    private readonly FileManagement.IGcodeThumbnailExtractorService _thumbnailExtractor = thumbnailExtractor;
    private readonly GcodeHarvestSettings _harvestSettings = harvestOptions.Value;
    private readonly IBackendCapabilityFactory _capabilityFactory = capabilityFactory;

    private static readonly string[] sourceArray = { "gcode" };

    public async Task<GcodeHarvestResultDto> StartHarvestAsync(StartGcodeHarvestDto request, CancellationToken ct = default)
    {
        _logger.LogInformation($"🔥 StartHarvestAsync CALLED for printer ID: {(request?.PrinterId ?? Guid.Empty)}");
        ArgumentNullException.ThrowIfNull(request);
        Printer? printer = await _printersRepo.FindByIdAsync(request.PrinterId, ct);
        _logger.LogInformation($"🔍 Found printer: {(printer?.Name ?? "NULL")} (ID: {(printer?.Id ?? Guid.Empty)})");
        if (printer == null)
        {
            return new GcodeHarvestResultDto(Guid.Empty, false, "Printer not found");
        }

        // Check if there's already an active harvest operation for this printer
        GcodeHarvestOperation? existingOperation = await _harvestRepo.GetActiveOperationForPrinterAsync(request.PrinterId, ct);

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

        await _harvestRepo.AddOperationAsync(operation, ct);
        await _harvestRepo.SaveChangesAsync(ct);

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
                await using AsyncServiceScope scope = _serviceScopeFactory.CreateAsyncScope();
                IHarvestRepository scopedHarvestRepo = scope.ServiceProvider.GetRequiredService<IHarvestRepository>();
                GcodeHarvestOperation? dbOperation = await scopedHarvestRepo.GetOperationByIdAsync(operation.Id);
                if (dbOperation != null)
                {
                    HarvestErrorHelper.SetOperationError(
                        dbOperation,
                        ex,
                        nameof(HarvestErrorPhase.Discovery),
                        failedResource: printer.ServerUrl);
                    await scopedHarvestRepo.SaveChangesAsync();
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
                _ = _activeTasks.TryRemove(operation.Id, out _);
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
            0,  // Files will be discovered asynchronously
            0); // Files will be imported by background workers
    }

    /// <summary>
    /// Discover files from printer and queue them for processing
    /// </summary>
    private async Task DiscoverAndQueueFilesAsync(GcodeHarvestOperation operation, Printer printer)
    {
        await using AsyncServiceScope scope = _serviceScopeFactory.CreateAsyncScope();
        IHarvestRepository scopedHarvestRepo = scope.ServiceProvider.GetRequiredService<IHarvestRepository>();
        IBackendClientFactory scopedBackendFactory = scope.ServiceProvider.GetRequiredService<IBackendClientFactory>();
        IBackendCapabilityFactory scopedCapabilityFactory = scope.ServiceProvider.GetRequiredService<IBackendCapabilityFactory>();
        IUnifiedLoggingService scopedLogger = scope.ServiceProvider.GetRequiredService<IUnifiedLoggingService>();
        // Note: HarvestHub is deleted, file discovery is now integrated into regular printer services

        try
        {
            scopedLogger.LogInformation($"Starting file discovery in scoped context for operation {operation.Id} on printer {printer.Name}");

            // Get file list from printer using capability-based abstraction
            PrinterBackend backend = (PrinterBackend)printer.Backend;
            scopedLogger.LogInformation($"Calling file discovery for backend {backend} on printer {printer.Name} at {printer.ServerUrl}");

            List<PrinterFileInfo> fileList = new();
            try
            {
                // Check if the backend supports file listing using capability factory
                if (scopedCapabilityFactory.TryGetFileListClient(backend, out _))
                {
                    fileList = backend switch
                    {
                        PrinterBackend.Moonraker => await GetMoonrakerFilesAsync(printer.ServerUrl, scopedLogger),
                        PrinterBackend.PrusaLink => await GetPrusaLinkFilesAsync(printer.ServerUrl, printer.ApiKey, scopedLogger),
                        PrinterBackend.SDCP => await GetSdcpFilesAsync(printer.ServerUrl, scopedLogger),
                        _ => new List<PrinterFileInfo>()
                    };
                }
                else
                {
                    scopedLogger.LogWarning($"Backend {backend} does not support file listing");
                }
            }
            catch (Exception ex)
            {
                scopedLogger.LogError(ex, $"Error discovering files for backend {backend}");
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
                .Select(e => e.StartsWith('.') ? e : "." + e)
                .ToArray();

            // Apply filtering for extensions & size constraints for preliminary count
            bool PassesInitialFilters(PrinterFileInfo f)
            {
                scopedLogger.LogDebug($"🔍 Filtering file: '{f.Name}' (Size: {f.Size})");

                // Use explicit Ordinal comparison on the original filename to avoid
                // unnecessary allocations from ToLowerInvariant. Extensions in
                // allowedExts are normalized to start with '.' so compare using
                // OrdinalIgnoreCase when appropriate.
                string name = f.Name;
                bool extOk = false;
                foreach (string? ext in allowedExts)
                {
                    if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
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
            GcodeHarvestOperation? dbOperation = await scopedHarvestRepo.GetOperationByIdAsync(operation.Id);
            if (dbOperation != null)
            {
                dbOperation.FilesFound = gcodeFileCount;
                await scopedHarvestRepo.SaveChangesAsync();
                scopedLogger.LogInformation($"Updated operation {operation.Id} with {dbOperation.FilesFound} G-code files found");

                // Emit SignalR update so clients see the updated FilesFound count immediately
                // TODO: Re-implement with proper hub context when HarvestHub is restored
                // GcodeHarvestOperationDto operationDto = MapToDto(dbOperation);
                // await scopedHub.Clients.All.SendAsync("HarvestOperationUpdated", operationDto);
            }
            else
            {
                scopedLogger.LogWarning($"Could not find operation {operation.Id} in database to update files found count");
            }

            // Send discovered files to frontend immediately during discovery phase (not wait for processing)
            scopedLogger.LogInformation($"Sending {gcodeFileCount} discovered files to frontend for operation {operation.Id}");
            foreach (PrinterFileInfo fileInfo in filteredFiles)
            {
                // Save discovered file to database so user can select it
                HarvestDiscoveredFile discoveredFile = new()
                {
                    Id = Guid.NewGuid(),
                    HarvestOperationId = operation.Id,
                    FileName = fileInfo.Name,
                    FilePath = fileInfo.Path,
                    Size = fileInfo.Size,
                    ExtractedSlicerName = fileInfo.SlicerName,
                    ExtractedMaterial = fileInfo.FilamentWeightGrams.HasValue ? $"~{Math.Round(fileInfo.FilamentWeightGrams.Value)}g" : "",
                    Status = HarvestFileStatus.Pending,
                    DiscoveredAt = DateTime.UtcNow,
                    ModifiedAt = fileInfo.ModifiedAt,
                    AlreadyInLibrary = false
                };

                await scopedHarvestRepo.AddDiscoveredFileAsync(discoveredFile, ct: default);

                // Send file discovery event immediately so UI can show files
                // TODO: Re-implement with proper hub context when HarvestHub is restored
                // await scopedHub.Clients.Group($"harvest-{operation.Id}").SendAsync("harvestfilediscovered", new
                // {
                //     operationId = operation.Id.ToString(),
                //     fileId = discoveredFile.Id.ToString(),
                //     fileName = fileInfo.Name,
                //     filePath = fileInfo.Path,
                //     fileSize = fileInfo.Size,
                //     extractedSlicer = fileInfo.SlicerName ?? "",
                //     extractedMaterial = fileInfo.FilamentWeightGrams.HasValue ? $"~{Math.Round(fileInfo.FilamentWeightGrams.Value)}g" : "",
                // });
            }

            await scopedHarvestRepo.SaveChangesAsync(ct: default);

            // Discovery is complete - files are now shown to user for selection
            // Queueing only happens after user selects files via ImportSelectedFilesAsync
            scopedLogger.LogInformation($"Discovery complete for operation {operation.Id}. Waiting for user to select files to import.");

            // Signal to UI that discovery is complete and stop the spinner
            // TODO: Re-implement with proper hub context when HarvestHub is restored
            // await scopedHub.Clients.Group($"harvest-{operation.Id}").SendAsync("harvestdiscoverycomplete", new
            // {
            //     operationId = operation.Id.ToString(),
            //     totalFilesDiscovered = gcodeFileCount,
            //     completedAt = DateTime.UtcNow.ToString("O")
            // });
        }
        catch (Exception ex)
        {
            scopedLogger.LogError(ex, $"File discovery failed for operation {operation.Id}");

            // Mark operation as failed with detailed error info
            GcodeHarvestOperation? dbOperation = await scopedHarvestRepo.GetOperationByIdAsync(operation.Id);
            if (dbOperation != null)
            {
                HarvestErrorHelper.SetOperationError(
                    dbOperation,
                    ex,
                    nameof(HarvestErrorPhase.Discovery),
                    failedResource: printer.ServerUrl);
                await scopedHarvestRepo.SaveChangesAsync();
            }
        }
    }

    private async Task<MemoryStream?> DownloadFileAsync(PrinterBackend backend, Printer printer, string filePath)
    {
        IUnifiedLoggingService log = _logger;
        try
        {
            // Check if the backend supports file downloads using capability factory
            if (_capabilityFactory.TryGetFileDownloadClient(backend, out var downloadClient) && 
                downloadClient is ISupportsFileDownload fileDownload)
            {
                // Use capability interface for file download (works for all backends that support it)
                string baseUrl = backend == PrinterBackend.Moonraker
                    ? printer.ServerUrl  // Moonraker uses ServerUrl directly
                    : printer.BackendUrl;
                    
                byte[]? fileBytes = await fileDownload.DownloadFileAsync(baseUrl, filePath, ct: CancellationToken.None);
                return await ConvertBytesToMemoryStreamAsync(fileBytes);
            }
            
            // For backends that don't support downloads, return null
            log.LogWarning($"Backend {backend} does not support file downloads for {filePath}");
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, $"Failed to download file {filePath} from {backend} printer {printer.Name}");
            return null;
        }
    }

    /// <summary>
    /// Helper to convert byte array to MemoryStream
    /// </summary>
    private static async Task<MemoryStream?> ConvertBytesToMemoryStreamAsync(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0)
            return null;
        
        var ms = new MemoryStream(bytes);
        ms.Seek(0, SeekOrigin.Begin);
        return await Task.FromResult(ms);
    }    public async Task<GcodeMetadataDto> ExtractMetadataAsync(Stream gcodeStream, CancellationToken ct = default)
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
        return ToHexLower(hash);
    }

    private static string ToHexLower(byte[] hash)
    {
        // Centralized hex canonicalization (lowercase) to avoid scattered ToLowerInvariant calls
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<GcodeHarvestOperationDto?> GetHarvestOperationAsync(Guid operationId, CancellationToken ct = default)
    {
        GcodeHarvestOperation? operation = await _harvestRepo.GetOperationWithPrinterAsync(operationId, ct);

        return operation == null ? null : MapToDto(operation);
    }

    public async Task<DiscoveredGcodeFileDto[]> GetDiscoveredFilesAsync(Guid operationId, CancellationToken ct = default)
    {
        _logger.LogInformation($"Getting discovered files for operation {operationId}");

        // Verify the operation exists
        GcodeHarvestOperation? operation = await _harvestRepo.GetOperationByIdAsync(operationId, ct);
        if (operation == null)
        {
            _logger.LogWarning($"GetDiscoveredFilesAsync: Operation {operationId} not found");
            return Array.Empty<DiscoveredGcodeFileDto>();
        }

        _logger.LogInformation($"Found operation {operationId} with status {operation.Status}, files found: {operation.FilesFound}");

        // Get files with explicit logging
        List<HarvestDiscoveredFile> files = await _harvestRepo.GetDiscoveredFilesAsync(operationId, ct);

        _logger.LogInformation($"Found {files.Count} discovered files for operation {operationId}");

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

        List<HarvestDiscoveredFile> files = await _harvestRepo.GetDiscoveredFilesPagedAsync(operationId, page, pageSize, search, ct);
        int total = await _harvestRepo.GetDiscoveredFilesCountAsync(operationId, ct);
        int totalPages = (int)Math.Ceiling(total / (double)pageSize);
        if (totalPages == 0)
        {
            totalPages = 1;
        }
        if (page > totalPages)
        {
            page = totalPages;
        }

        return new PagedResult<DiscoveredGcodeFileDto>(
            files.Select(MapToDto).ToList(),
            total,
            page,
            pageSize,
            totalPages);
    }

    public async Task<GcodeHarvestResultDto> ImportSelectedFilesAsync(ImportSelectedGcodeFilesDto request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        GcodeHarvestOperation? operation = await _harvestRepo.GetOperationWithPrinterAsync(request.HarvestOperationId, ct);

        if (operation == null)
        {
            return new GcodeHarvestResultDto(request.HarvestOperationId, false, "Harvest operation not found");
        }

        _logger.LogInformation($"ImportSelectedFilesAsync: Received {request.FileIds.Length} file IDs to import: {string.Join(", ", request.FileIds)}");

        HarvestDiscoveredFile[] selectedFiles = await _harvestRepo.GetDiscoveredFilesByIdsAsync(request.FileIds.ToList(), ct);

        _logger.LogInformation($"ImportSelectedFilesAsync: Retrieved {selectedFiles.Length} files from database");

        List<string> importedFileIds = new();
        List<string> skippedFileIds = new();
        List<string> failedFileIds = new();
        Dictionary<string, string> errorDetails = new();

        // Apply concurrency limiting using semaphore based on configuration
        int maxConcurrent = _harvestSettings.MaxConcurrentImports;
        _logger.LogInformation($"ImportSelectedFilesAsync: Using max concurrent imports: {maxConcurrent}");
        using SemaphoreSlim semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);

        // Create import tasks for all selected files with concurrency limiting
        List<Task> importTasks = selectedFiles.Select(async discoveredFile =>
        {
            // Acquire a semaphore slot (respects max concurrency)
            await semaphore.WaitAsync(ct);
            try
            {
                if (discoveredFile.AlreadyInLibrary)
                {
                    skippedFileIds.Add(discoveredFile.Id.ToString());
                    return;
                }

                // Mark as in progress and emit update
                discoveredFile.Status = HarvestFileStatus.InProgress;
                discoveredFile.StartedAt = DateTime.UtcNow;
                await _harvestRepo.SaveChangesAsync(ct);
                await _harvestHub.Clients.Group($"harvest-{operation.Id}")
                    .SendAsync("HarvestFileUpdated", MapToDto(discoveredFile), ct);

                // Get storage directory from centralized storage service (supports Docker and K8s)
                string storageDir = _storagePathService.GetGcodeStorageDirectory();
                _ = Directory.CreateDirectory(storageDir);
                // Generate unique filename
                string fileName = $"{Guid.NewGuid()}_{discoveredFile.FileName}";
                string filePath = Path.Combine(storageDir, fileName);

                // Download file from printer
                if (operation.Printer == null)
                {
                    discoveredFile.Status = HarvestFileStatus.Failed;
                    discoveredFile.Error = "Printer information not available for download";
                    discoveredFile.CompletedAt = DateTime.UtcNow;
                    await _harvestRepo.SaveChangesAsync(ct);
                    await _harvestHub.Clients.Group($"harvest-{operation.Id}")
                        .SendAsync("HarvestFileUpdated", MapToDto(discoveredFile), ct);
                    failedFileIds.Add(discoveredFile.Id.ToString());
                    errorDetails[discoveredFile.Id.ToString()] = "Printer information not available";
                    return;
                }

                PrinterBackend backend = (PrinterBackend)operation.Printer.Backend;
                using MemoryStream? gcodeContent = await DownloadFileAsync(backend, operation.Printer, discoveredFile.FilePath);

                if (gcodeContent == null)
                {
                    discoveredFile.Status = HarvestFileStatus.Failed;
                    discoveredFile.Error = $"Failed to download {discoveredFile.FileName}";
                    discoveredFile.CompletedAt = DateTime.UtcNow;
                    await _harvestRepo.SaveChangesAsync(ct);
                    await _harvestHub.Clients.Group($"harvest-{operation.Id}")
                        .SendAsync("HarvestFileUpdated", MapToDto(discoveredFile), ct);
                    failedFileIds.Add(discoveredFile.Id.ToString());
                    errorDetails[discoveredFile.Id.ToString()] = $"Failed to download {discoveredFile.FileName}";
                    return;
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
                await _harvestRepo.SaveChangesAsync(ct);
                await _harvestHub.Clients.Group($"harvest-{operation.Id}")
                    .SendAsync("HarvestFileUpdated", MapToDto(discoveredFile), ct);

                // Extract metadata from gcode as fallback for incomplete API data
                GcodeMetadataExtracted? extractedMetadata = null;
                string? thumbnailPath = null;
                if (gcodeContent.Length > 0)
                {
                    try
                    {
                        gcodeContent.Position = 0;
                        using StreamReader reader = new(gcodeContent, Encoding.UTF8, leaveOpen: true);
                        string gcodeText = await reader.ReadToEndAsync(ct);
                        extractedMetadata = await _metadataExtractor.ExtractMetadataAsync(gcodeText);

                        // Extract and save thumbnail if present using shared service
                        if (extractedMetadata?.ThumbnailData != null && extractedMetadata.ThumbnailData.Length > 0)
                        {
                            try
                            {
                                gcodeContent.Position = 0;
                                thumbnailPath = await _thumbnailExtractor.ExtractAndSaveThumbnailAsync(gcodeContent, ct);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to extract thumbnail for {FileName}", discoveredFile.FileName);
                                // Continue anyway - thumbnail is optional
                                thumbnailPath = null;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to extract metadata from gcode file {FileName}", discoveredFile.FileName);
                        // Continue anyway - metadata extraction is optional
                    }
                }

                // Create library entry
                // Note: We use TargetModelId instead of SourcePrinterId so the file is usable on ANY printer
                // of the same model, not just the one it was harvested from
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
                    SourcePrinterId = operation.PrinterId, // Keep for reference/audit trail
                    OriginalPrinterPath = discoveredFile.FilePath,
                    LastSeenOnPrinter = DateTime.UtcNow,
                    TargetModelId = operation.Printer?.ModelId, // Make available to all printers of this model
                    RequiredNozzleDiameter = discoveredFile.ExtractedNozzleDiameter ?? extractedMetadata?.NozzleDiameter,
                    RequiredMaterial = discoveredFile.ExtractedMaterial ?? extractedMetadata?.Material,
                    EstimatedPrintTimeMinutes = discoveredFile.ExtractedPrintTime ?? extractedMetadata?.EstimatedPrintTimeMinutes,
                    EstimatedFilamentLengthMm = discoveredFile.ExtractedFilamentLength ?? extractedMetadata?.FilamentLengthMm,
                    SlicerName = discoveredFile.ExtractedSlicerName ?? extractedMetadata?.SlicerName,
                    SlicerVersion = discoveredFile.ExtractedSlicerVersion ?? extractedMetadata?.SlicerVersion,
                    ThumbnailPath = thumbnailPath, // Save extracted thumbnail path if available
                    Tags = request.DefaultTags != null ? JsonSerializer.Serialize(request.DefaultTags) : null
                };

                await _gcodeRepo.AddAsync(gcodeFile, ct);
                importedFileIds.Add(discoveredFile.Id.ToString());

                // Increment the operation's FilesAdded counter (only when successfully imported to library)
                operation.FilesAdded++;
            }
            catch (Exception ex)
            {
                discoveredFile.Status = HarvestFileStatus.Failed;
                discoveredFile.Error = $"Failed to import {discoveredFile.FileName}: {ex.Message}";
                discoveredFile.CompletedAt = DateTime.UtcNow;
                await _harvestRepo.SaveChangesAsync(ct);
                await _harvestHub.Clients.Group($"harvest-{operation.Id}")
                    .SendAsync("HarvestFileUpdated", MapToDto(discoveredFile), ct);
                _logger.LogError(ex, "Failed to import file {FileName}", discoveredFile.FileName);
                failedFileIds.Add(discoveredFile.Id.ToString());
                errorDetails[discoveredFile.Id.ToString()] = $"Failed to import {discoveredFile.FileName}: {ex.Message}";
            }
            finally
            {
                // Release the semaphore slot so another file can begin importing
                _ = semaphore.Release();
            }
        }).ToList();

        // Wait for all import tasks to complete
        try
        {
            await Task.WhenAll(importTasks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during concurrent file imports");
            // Individual errors already handled in task catch blocks
        }

        await _harvestRepo.SaveChangesAsync(ct);
        await _gcodeRepo.SaveChangesAsync(ct);

        GcodeHarvestResultDto result = new GcodeHarvestResultDto(
            request.HarvestOperationId,
            true,
            $"Imported {importedFileIds.Count} files",
            selectedFiles.Length,
            importedFileIds.Count,
            failedFileIds.Count > 0 ? failedFileIds.ToArray() : null)
        {
            ImportedFileIds = importedFileIds.ToArray(),
            SkippedFileIds = skippedFileIds.ToArray(),
            FailedFileIds = failedFileIds.ToArray(),
            ErrorDetails = errorDetails.Count > 0 ? errorDetails : null
        };
        return result;
    }

    public async Task<bool> CancelHarvestAsync(Guid operationId, CancellationToken ct = default)
    {
        // Use tracked version so changes will be persisted
        GcodeHarvestOperation? operation = await _harvestRepo.GetOperationByIdTrackedAsync(operationId, ct);

        if (operation == null || operation.Status != GcodeHarvestStatus.Running)
        {
            return false;
        }

        operation.Status = GcodeHarvestStatus.Cancelled;
        operation.CompletedAt = DateTime.UtcNow;
        await _harvestRepo.SaveChangesAsync(ct);

        // Log the cancellation for tracking purposes
        _logger.LogInformation($"Harvest operation {operationId} was cancelled");

        // Broadcast cancellation event via SignalR so UI updates immediately
        await _harvestHub.Clients.Group($"harvest-{operationId}").SendAsync("harvestoperationcancelled", new
        {
            operationId,
            status = "cancelled",
            completedAt = operation.CompletedAt
        }, ct);

        // Note: We don't actually cancel the task because Task.Run doesn't support 
        // cancellation after it's started. The background task will check the 
        // operation status and exit gracefully when it sees the Cancelled status.

        return true;
    }

    public async Task<bool> RestartDiscoveryAsync(Guid operationId, CancellationToken ct = default)
    {
        // Get the operation with tracking enabled
        GcodeHarvestOperation? operation = await _harvestRepo.GetOperationByIdTrackedAsync(operationId, ct);

        if (operation == null || operation.Status != GcodeHarvestStatus.Running)
        {
            return false;
        }

        // Get the printer to verify it exists and get its details
        Printer? printer = await _printersRepo.FindByIdAsync(operation.PrinterId, ct);
        if (printer == null)
        {
            _logger.LogError($"Printer {operation.PrinterId} for harvest operation {operationId} not found");
            return false;
        }

        _logger.LogInformation($"Restarting file discovery for operation {operationId} on printer {printer.Name}");

        // Clear discovered files to restart fresh
        await _harvestRepo.DeleteDiscoveredFilesByOperationAsync(operationId, ct);

        // Reset operation statistics
        operation.FilesFound = 0;
        operation.FilesAdded = 0;
        operation.FilesSkipped = 0;
        operation.FilesErrored = 0;
        operation.TotalBytesProcessed = 0;

        // Save changes
        await _harvestRepo.SaveChangesAsync(ct);

        _logger.LogInformation($"Cleared discovered files for operation {operationId}, restarting discovery");

        // Broadcast restart event via SignalR to notify clients
        await _harvestHub.Clients.Group($"harvest-{operationId}").SendAsync("harvestdiscoveryrestarted", new
        {
            operationId,
            status = "restarting",
            restartedAt = DateTime.UtcNow
        }, ct);

        // Start fresh discovery in background (using same pattern as StartHarvestAsync)
        Task backgroundTask = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation($"🔄 Background harvest restart task STARTED for operation {operationId} on printer {printer.Name}");
                await DiscoverAndQueueFilesAsync(operation, printer);
                _logger.LogInformation($"✅ Background harvest restart task COMPLETED successfully for operation {operationId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Background harvest restart task FAILED for operation {operationId}: {ex.Message}");

                // Update the operation status to failed with detailed error info
                await using AsyncServiceScope scope = _serviceScopeFactory.CreateAsyncScope();
                IHarvestRepository scopedHarvestRepo = scope.ServiceProvider.GetRequiredService<IHarvestRepository>();
                GcodeHarvestOperation? dbOperation = await scopedHarvestRepo.GetOperationByIdTrackedAsync(operationId);
                if (dbOperation != null)
                {
                    HarvestErrorHelper.SetOperationError(
                        dbOperation,
                        ex,
                        nameof(HarvestErrorPhase.Discovery),
                        failedResource: printer.ServerUrl);
                    await scopedHarvestRepo.SaveChangesAsync();
                    _logger.LogError($"💾 Updated operation {operationId} status to Failed in database");
                }
                else
                {
                    _logger.LogError($"⚠️ Could not find operation {operationId} in database to mark as failed");
                }
            }
            finally
            {
                // Remove from active tasks when done
                _ = _activeTasks.TryRemove(operationId, out _);
                _logger.LogDebug($"Removed operation {operationId} from active tasks tracking");
            }
        });

        // Track the task
        _ = _activeTasks.TryAdd(operationId, backgroundTask);

        return true;
    }

    public async Task<GcodeHarvestOperationDto?> GetActiveHarvestAsync(Guid printerId, CancellationToken ct = default)
    {
        GcodeHarvestOperation? operation = await _harvestRepo.GetActiveOperationForPrinterAsync(printerId, ct);

        return operation == null ? null : MapToDto(operation);
    }

    public async Task<GcodeHarvestOperationDto[]> GetRecentHarvestsAsync(Guid printerId, int count = 10, CancellationToken ct = default)
    {
        List<GcodeHarvestOperation> operations = await _harvestRepo.GetRecentOperationsForPrinterAsync(printerId, count, ct);

        return operations.Select(MapToDto).ToArray();
    }

    public async Task<GcodeHarvestOperationDto[]> GetActiveHarvestsAsync(CancellationToken ct = default)
    {
        List<GcodeHarvestOperation> operations = await _harvestRepo.GetActiveOperationsAsync(ct);

        return operations.Select(MapToDto).ToArray();
    }

    public async Task<GcodeHarvestOperationDto[]> GetHarvestOperationsAsync(Guid? printerId = null, string? status = null, int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        GcodeHarvestStatus? statusEnum = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse(status, true, out GcodeHarvestStatus parsedStatus))
        {
            statusEnum = parsedStatus;
        }

        List<GcodeHarvestOperation> operations = await _harvestRepo.GetOperationsAsync(printerId, statusEnum, limit, offset, ct);

        return operations.Select(MapToDto).ToArray();
    }

    // (moved below to be adjacent to the other overload)

    private async Task<MemoryStream?> DownloadMoonrakerFileAsync(string serverUrl, string filePath, IUnifiedLoggingService? logger = null)
    {
        IUnifiedLoggingService log = logger ?? _logger;

        try
        {
            // Retrieve Moonraker client from factory when needed
            if (!_capabilityFactory.TryGetFileDownloadClient(PrinterBackend.Moonraker, out var downloadClient) ||
                downloadClient is not ISupportsFileDownload fileDownload)
            {
                log.LogWarning("Moonraker backend does not support file downloads");
                return null;
            }

            // We already have ISupportsFileDownload, so use it directly
            log.LogInformation("Downloading file {FilePath} from Moonraker at {ServerUrl}", filePath, serverUrl);
            byte[]? bytes = await fileDownload.DownloadFileAsync(serverUrl, filePath);
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

    private async Task<MemoryStream?> DownloadPrusaLinkFileAsync(string serverUrl, string filePath, IUnifiedLoggingService? logger = null)
    {
        IUnifiedLoggingService log = logger ?? _logger;

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
    private Task<MemoryStream?> DownloadPrusaLinkFileAsync(string serverUrl, string? apiKey, string filePath, IUnifiedLoggingService? logger = null)
    {
        _ = apiKey; // explicitly discard unused
        return DownloadPrusaLinkFileAsync(serverUrl, filePath, logger);
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

    // Helper methods for different printer backends
    private async Task<List<PrinterFileInfo>> GetMoonrakerFilesAsync(string serverUrl, IUnifiedLoggingService? logger = null)
    {
        IUnifiedLoggingService log = logger ?? _logger;

        try
        {
            // Retrieve Moonraker client from factory when needed
            if (!_capabilityFactory.TryGetFileListClient(PrinterBackend.Moonraker, out var baseClient))
            {
                log.LogWarning("Backend Moonraker does not support file listing");
                return new List<PrinterFileInfo>();
            }

            // Verify backend supports Moonraker-specific directory operations
            if (baseClient is not ISupportsFileList fileListClient)
            {
                log.LogError("Moonraker backend does not support file listing capability");
                return new List<PrinterFileInfo>();
            }

            log.LogInformation("GetMoonrakerFilesAsync starting for {ServerUrl} with retry logic", serverUrl);
            List<PrinterFileInfo> files = new();

            // Note: File directory traversal requires Moonraker-specific API
            // This would need to be refactored to use a more generic capability interface
            // For now, we'll return an empty list to unblock compilation
            log.LogWarning("Moonraker directory traversal not implemented in capability-based architecture");
            return files;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to get file list from Moonraker at {ServerUrl}", serverUrl);
            return new();
        }
    }

    private static async Task CollectFilesRecursivelyWithRetryAsync(List<PrinterFileInfo> files, MoonrakerDir directory, string basePath, string serverUrl, IUnifiedLoggingService log)
    {
        // Note: This method was previously able to recursively fetch directory info from Moonraker,
        // but that functionality required direct access to the Moonraker client, which we no longer expose.
        // This method is now kept for compatibility but is deprecated.
        log.LogWarning("CollectFilesRecursivelyWithRetryAsync called but recursive directory traversal is not implemented");
        
        // Add files from current directory only (no recursion)
        if (directory.Files != null)
        {
            foreach (MoonrakerFileInfo file in directory.Files)
            {
                files.Add(new PrinterFileInfo
                {
                    Name = Path.GetFileName(file.Path),
                    Path = file.Path,
                    Size = file.Size,
                    ModifiedAt = DateTimeOffset.FromUnixTimeSeconds((long)file.Modified).DateTime
                });
            }
        }
    }
    private async Task<List<PrinterFileInfo>> GetPrusaLinkFilesAsync(string serverUrl, string? apiKey, IUnifiedLoggingService? logger = null)
    {
        IUnifiedLoggingService log = logger ?? _logger;

        try
        {
            // Retrieve PrusaLink file list using capability interface
            if (_capabilityFactory.TryGetFileListClient(PrinterBackend.PrusaLink, out var baseClient) &&
                baseClient is ISupportsFileList fileListClient)
            {
                List<Farm.Infrastructure.Services.Printers.PrinterFileInfo> files = await fileListClient.GetFileListAsync(serverUrl, apiKey, CancellationToken.None);
                log.LogInformation($"Found {files.Count} files in PrusaLink at {serverUrl}");
                
                // Map infrastructure PrinterFileInfo to local PrinterFileInfo class
                return files.Select(f => new PrinterFileInfo
                {
                    Name = f.Name,
                    Path = f.Path,
                    Size = f.Size ?? 0,
                    ModifiedAt = null,
                    SlicerName = null,
                    FilamentWeightGrams = null
                }).ToList();
            }

            log.LogWarning("Backend PrusaLink does not support file listing capability");
            return new List<PrinterFileInfo>();
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
    [GeneratedRegex(@"Cura_SteamEngine (\S+)")]
    private static partial Regex MyRegex1();
}
