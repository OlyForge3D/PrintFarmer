// ...existing code...
#pragma warning disable VSTHRD101, S1172, CA1849 // Fire-and-forget background tasks, unused parameter in delegate, sync writes intentional

using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Gcode;
using Farm.Infrastructure.Repositories.Harvest;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Resilience;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MoonrakerDir = Farm.Infrastructure.Contracts.Printers.Moonraker.MoonrakerDirectoryInfo;

namespace Farm.Infrastructure.Services.Gcode;

/// <summary>
/// Service for harvesting G-code files from registered printers
/// </summary>
public class GcodeHarvestService(
    IUnitOfWork unitOfWork,
    IUnifiedLoggingService logger,
    IServiceScopeFactory serviceScopeFactory,
    IGcodeMetadataExtractorService metadataExtractor,
    IStoragePathService storagePathService,
    IGcodeThumbnailExtractorService thumbnailExtractor,
    IBackendCapabilityFactory capabilityFactory,
    IHarvestEventBroadcaster harvestEventBroadcaster) : IGcodeHarvestService
{

    private readonly IUnitOfWork _unitOfWork = unitOfWork;
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly IServiceScopeFactory _serviceScopeFactory = serviceScopeFactory;
    private readonly ConcurrentDictionary<Guid, Task> _activeTasks = new();
    private readonly IGcodeMetadataExtractorService _metadataExtractor = metadataExtractor;
    private readonly IStoragePathService _storagePathService = storagePathService;
    private readonly IGcodeThumbnailExtractorService _thumbnailExtractor = thumbnailExtractor;
    private readonly IBackendCapabilityFactory _capabilityFactory = capabilityFactory;
    private readonly IHarvestEventBroadcaster _harvestEventBroadcaster = harvestEventBroadcaster;

    private static readonly string[] sourceArray = { "gcode" };

    private static readonly Regex OrcaSlicerVersionRegex = new(@"OrcaSlicer (\S+)", RegexOptions.Compiled);
    private static readonly Regex PrusaSlicerVersionRegex = new(@"PrusaSlicer (\S+)", RegexOptions.Compiled);
    private static readonly Regex CuraVersionRegex = new(@"Cura_SteamEngine (\S+)", RegexOptions.Compiled);

    public async Task<bool> SkipDiscoveredFileAsync(Guid operationId, Guid fileId, CancellationToken ct = default)
    {
        // Find the discovered file
        HarvestDiscoveredFile? file = await _unitOfWork.HarvestOperations.GetDiscoveredFileByIdAsync(fileId, operationId, ct);
        if (file == null)
        {
            return false;
        }

        // Mark as skipped
        file.Status = HarvestFileStatus.Skipped;
        file.Error = "Skipped by user";
        await _unitOfWork.SaveChangesAsync(ct);

        // Send file update to SignalR clients
        await _harvestEventBroadcaster.BroadcastToGroupAsync(operationId, "harvestfileupdated", MapToDto(file), ct);

        return true;
    }

    public async Task<bool> RetryDiscoveredFileAsync(Guid operationId, Guid fileId, CancellationToken ct = default)
    {
        // Find the discovered file
        HarvestDiscoveredFile? file = await _unitOfWork.HarvestOperations.GetDiscoveredFileByIdAsync(fileId, operationId, ct);
        if (file == null)
        {
            return false;
        }

        // Clear error and mark as pending
        file.Status = HarvestFileStatus.Pending;
        file.Error = null;
        await _unitOfWork.SaveChangesAsync(ct);

        // Send file update to SignalR clients
        await _harvestEventBroadcaster.BroadcastToGroupAsync(operationId, "harvestfileupdated", MapToDto(file), ct);

        return true;
    }

    public async Task<GcodeHarvestResultDto> StartHarvestAsync(StartGcodeHarvestDto request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        _logger.LogInformation($"🔥 StartHarvestAsync CALLED for printer ID: {request.PrinterId}");
        Printer? printer = await _unitOfWork.Printers.FindByIdAsync(request.PrinterId, ct);
        
        _logger.LogInformation($"🔍 Found printer: {(printer?.Name ?? "NULL")} (ID: {(printer?.Id ?? Guid.Empty)})");
        if (printer == null)
        {
            return new GcodeHarvestResultDto(Guid.Empty, false, "Printer not found");
        }

        // Check if printer backend supports file listing capability for harvesting
        PrinterBackend backend = (PrinterBackend)printer.Backend;
        _logger.LogWarning($"[DIAGNOSTIC] Checking file list capability for backend: {backend}");
        if (!_capabilityFactory.TryGetFileListClient(backend, out _))
        {
            _logger.LogWarning($"[DIAGNOSTIC] FAILED: Backend {backend} does NOT support file listing capability");
            return new GcodeHarvestResultDto(Guid.Empty, false, $"Printer backend '{backend}' does not support file listing capability required for G-code harvesting");
        }

        _logger.LogWarning($"[DIAGNOSTIC] SUCCESS: Backend {backend} DOES support file listing capability, proceeding with harvest");

        // Check if there's already an active harvest operation for this printer
        GcodeHarvestOperation? existingOperation = await _unitOfWork.HarvestOperations.GetActiveOperationForPrinterAsync(request.PrinterId, ct);

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

        await _unitOfWork.HarvestOperations.AddOperationAsync(operation, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation($"Starting file discovery for operation {operation.Id} on printer {printer.Name}");

        // Extract essential printer data BEFORE background task to avoid EF Core context issues
        Guid printerId = printer.Id;
        string printerName = printer.Name;
        string printerBackendUrl = printer.BackendUrl;  // Use calculated BackendUrl with port
        string printerApiKey = printer.ApiKey ?? "";
        PrinterBackend printerBackend = (PrinterBackend)printer.Backend;

        _logger.LogError($"[DIAGNOSTIC-HARVEST] Extracted printer data: id={printerId}, name={printerName}, backendUrl={printerBackendUrl}, backend={printerBackend}");

        // Start file discovery and queueing in background
        // Use explicit async task method to properly execute on thread pool
        async Task ExecuteHarvestAsync()
        {
            await Console.Error.WriteLineAsync($"[HARVEST] Background harvest start for op {operation.Id}");
            try
            {
                _logger.LogError($"🚀 Background harvest task STARTED for operation {operation.Id} on printer {printerName}");
                _logger.LogError($"[DIAGNOSTIC-HARVEST] Background task: Calling DiscoverAndQueueFilesAsync");
                await DiscoverAndQueueFilesAsync(operation, printerId, printerName, printerBackendUrl, printerApiKey, printerBackend);
                await Console.Error.WriteLineAsync($"[HARVEST] Background harvest completed for op {operation.Id}");
                _logger.LogError($"✅ Background harvest task COMPLETED successfully for operation {operation.Id}");
            }
            catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync($"[HARVEST] Background harvest FAILED for op {operation.Id}: {ex.Message}");
                    _logger.LogError(ex, $"❌ Background harvest task FAILED for operation {operation.Id}: {ex.Message}");

                    // Update the operation status to failed with detailed error info
                    await using AsyncServiceScope scope = _serviceScopeFactory.CreateAsyncScope();
                    IUnitOfWork scopedUnitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                    IHarvestRepository scopedHarvestRepo = scopedUnitOfWork.HarvestOperations;
                
                // CRITICAL: Must use GetOperationByIdTrackedAsync (with tracking) not GetOperationByIdAsync (AsNoTracking)
                // We need to modify the operation, so it MUST be tracked by EF Core
                GcodeHarvestOperation? dbOperation = await scopedHarvestRepo.GetOperationByIdTrackedAsync(operation.Id);
                
                if (dbOperation != null)
                {
                    HarvestErrorHelper.SetOperationError(
                        dbOperation,
                        ex,
                        nameof(HarvestErrorPhase.Discovery),
                        failedResource: printerBackendUrl);
                    
                    await scopedHarvestRepo.SaveChangesAsync();
                    _logger.LogError($"💾 Updated operation {operation.Id} status to Failed in database");
                }
                else
                {
                    _logger.LogError($"⚠️ Could not find operation {operation.Id} in database to mark as failed");
                }
            }
        }

        _ = Task.Run(ExecuteHarvestAsync);

        _logger.LogDebug($"Queued harvest operation {operation.Id} to thread pool");

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
    private async Task DiscoverAndQueueFilesAsync(GcodeHarvestOperation operation, Guid _printerId, string printerName, string printerBackendUrl, string printerApiKey, PrinterBackend printerBackend)
    {
        await using AsyncServiceScope scope = _serviceScopeFactory.CreateAsyncScope();
            IUnitOfWork scopedUnitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            IHarvestRepository scopedHarvestRepo = scopedUnitOfWork.HarvestOperations;
            IBackendClientFactory scopedBackendFactory = scope.ServiceProvider.GetRequiredService<IBackendClientFactory>();
            IBackendCapabilityFactory scopedCapabilityFactory = scope.ServiceProvider.GetRequiredService<IBackendCapabilityFactory>();
        // Use _logger instead of scoped logger for background tasks to ensure logs are flushed

        try
        {
            _logger.LogInformation($"Starting file discovery in scoped context for operation {operation.Id} on printer {printerName}");
            _logger.LogError($"[DIAGNOSTIC-HARVEST] DiscoverAndQueueFilesAsync START: operation={operation.Id}, printer={printerName}, backendUrl={printerBackendUrl}, backend={printerBackend}");

            // Get file list from printer using capability-based abstraction
            PrinterBackend backend = printerBackend;

            _logger.LogInformation($"Calling file discovery for backend {backend} on printer {printerName} at {printerBackendUrl}");
            _logger.LogError($"[DIAGNOSTIC-HARVEST] Backend determined: {backend}");

            List<PrinterFileInfo> fileList = new();
            try
            {
                // Check if the backend supports file listing using capability factory
                _logger.LogError($"[DIAGNOSTIC-HARVEST] Checking if backend {backend} supports file listing...");
                
                if (scopedCapabilityFactory.TryGetFileListClient(backend, out var fileListClient) &&
                    fileListClient is ISupportsFileList fileListCapability)
                {
                    _logger.LogError($"[DIAGNOSTIC-HARVEST] Backend {backend} supports file listing - proceeding with discovery");
                    _logger.LogError($"[DIAGNOSTIC-HARVEST] About to call GetFileListAsync on {fileListClient.GetType().FullName} for {backend}");
                    try
                    {
                        // Use the capability interface directly - no switch statement needed!
                        var infrastructureFiles = await fileListCapability.GetFileListAsync(printerBackendUrl, printerApiKey, CancellationToken.None);
                        _logger.LogError($"[DIAGNOSTIC-HARVEST] GetFileListAsync returned {infrastructureFiles?.Count ?? 0} files");

                        // Map from Infrastructure.PrinterFileInfo to local PrinterFileInfo
                        fileList = (infrastructureFiles ?? new()).Select(f => new PrinterFileInfo
                        {
                            Name = f.Name,
                            Path = f.Path,
                            Size = f.Size ?? 0,
                            ModifiedAt = null,
                            SlicerName = null,
                            SlicerVersion = null,
                            FilamentWeightGrams = null
                        }).ToList();
                    }
                    catch (Exception switchEx)
                    {
                        _logger.LogError(switchEx, $"[DIAGNOSTIC-HARVEST] Exception calling GetFileListAsync: {switchEx.Message}");
                        throw;
                    }
                    _logger.LogError($"[DIAGNOSTIC-HARVEST] File discovery completed. Returned {fileList?.Count ?? 0} files");
                    if ((fileList?.Count ?? 0) == 0)
                    {
                        _logger.LogError($"[DIAGNOSTIC-HARVEST] ⚠️ Backend returned 0 files. printerBackendUrl={printerBackendUrl}, backend={backend}");
                    }
                }
                else
                {
                    _logger.LogWarning($"Backend {backend} does not support file listing");
                    _logger.LogError($"[DIAGNOSTIC-HARVEST] Backend {backend} does NOT support file listing - returning empty list");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error discovering files for backend {backend}");
                _logger.LogError($"[DIAGNOSTIC-HARVEST] Exception in file discovery: {ex.Message}");
            }

            _logger.LogInformation($"Discovered {fileList?.Count ?? 0} files for operation {operation.Id}");
            _logger.LogError($"[DIAGNOSTIC-HARVEST] Total discovered files: {fileList?.Count ?? 0}");

            // Log sample filenames for debugging
            if ((fileList?.Count ?? 0) > 0)
            {
                string[] sampleFiles = fileList!.Take(10).Select(f => $"{f.Name} (size: {f.Size})").ToArray();
                _logger.LogInformation($"📄 Sample files: {string.Join(", ", sampleFiles)}");
                _logger.LogError($"[DIAGNOSTIC-HARVEST] Sample discovered files: {string.Join("; ", sampleFiles)}");
            }
            else
            {
                _logger.LogError($"[DIAGNOSTIC-HARVEST] ⚠️ NO FILES DISCOVERED!");
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
                _logger.LogDebug($"🔍 Filtering file: '{f.Name}' (Size: {f.Size})");

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
                        _logger.LogDebug($"✅ File '{f.Name}' matches extension '{ext}'");
                        break;
                    }
                }
                if (!extOk)
                {
                    _logger.LogDebug($"❌ File '{f.Name}' rejected: extension doesn't match any of [{string.Join(", ", allowedExts)}]");
                    return false;
                }
                if (operation.MinFileSizeBytes.HasValue && f.Size < operation.MinFileSizeBytes.Value)
                {
                    _logger.LogDebug($"❌ File '{f.Name}' rejected: size {f.Size} < minimum {operation.MinFileSizeBytes.Value}");
                    return false;
                }
                if (operation.MaxFileSizeBytes.HasValue && f.Size > operation.MaxFileSizeBytes.Value)
                {
                    _logger.LogDebug($"❌ File '{f.Name}' rejected: size {f.Size} > maximum {operation.MaxFileSizeBytes.Value}");
                    return false;
                }
                if (operation.ModifiedAfter.HasValue && f.ModifiedAt.HasValue && f.ModifiedAt < operation.ModifiedAfter)
                {
                    _logger.LogDebug($"❌ File '{f.Name}' rejected: modified {f.ModifiedAt} < required {operation.ModifiedAfter}");
                    return false;
                }
                _logger.LogDebug($"✅ File '{f.Name}' passed all filters");
                return true;
            }

            List<PrinterFileInfo> filteredFiles = (fileList ?? new()).Where(PassesInitialFilters).ToList();
            int gcodeFileCount = filteredFiles.Count;
            _logger.LogInformation($"Filtered to {gcodeFileCount} candidate files (from {fileList?.Count ?? 0}). Extensions: {string.Join(",", allowedExts)}");
            _logger.LogError($"[DIAGNOSTIC-HARVEST] Files after filtering: {gcodeFileCount} (from {fileList?.Count ?? 0})");

            // Update files found count immediately
            // CRITICAL: Must use GetOperationByIdTrackedAsync (with tracking) not GetOperationByIdAsync (AsNoTracking)
            // AsNoTracking objects are detached and SaveChangesAsync won't detect modifications
            _logger.LogError($"[DIAGNOSTIC-HARVEST] ABOUT TO CALL GetOperationByIdTrackedAsync");
            GcodeHarvestOperation? dbOperation = await scopedHarvestRepo.GetOperationByIdTrackedAsync(operation.Id);
            _logger.LogError($"[DIAGNOSTIC-HARVEST] RETURNED FROM GetOperationByIdTrackedAsync: dbOperation is {(dbOperation == null ? "NULL" : "NOT NULL")}");
            
            if (dbOperation != null)
            {
                _logger.LogError($"[DIAGNOSTIC-HARVEST] BEFORE UPDATE: FilesFound={dbOperation.FilesFound}");
                dbOperation.FilesFound = gcodeFileCount;
                _logger.LogError($"[DIAGNOSTIC-HARVEST] AFTER ASSIGNMENT: FilesFound={dbOperation.FilesFound}");
                try
                {
                    await scopedHarvestRepo.SaveChangesAsync();
                    _logger.LogError($"[DIAGNOSTIC-HARVEST] SaveChangesAsync completed successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"[DIAGNOSTIC-HARVEST] SaveChangesAsync FAILED with exception: {ex.Message}");
                    throw;
                }
                _logger.LogError($"[DIAGNOSTIC-HARVEST] AFTER SAVE: FilesFound={dbOperation.FilesFound}");

                // Verify the save actually persisted by querying again
                GcodeHarvestOperation? verifyOperation = await scopedHarvestRepo.GetOperationByIdTrackedAsync(operation.Id);
                _logger.LogError($"[DIAGNOSTIC-HARVEST] VERIFICATION QUERY: Operation={verifyOperation?.Id}, FilesFound={verifyOperation?.FilesFound}");

                _logger.LogInformation($"Updated operation {operation.Id} with {dbOperation.FilesFound} G-code files found");
                _logger.LogError($"[DIAGNOSTIC-HARVEST] Updated DB operation: FilesFound={gcodeFileCount}");

                // Send SignalR update so clients see the updated FilesFound count immediately
                await _harvestEventBroadcaster.BroadcastToGroupAsync(operation.Id, "harvestoperationprogress", new
                {
                    operationId = operation.Id,
                    filesFound = dbOperation.FilesFound,
                    filesProcessed = 0,
                    filesAdded = 0,
                    filesSkipped = 0,
                    filesErrored = 0
                }, CancellationToken.None);
                _logger.LogInformation($"Sent OperationProgress event for operation {operation.Id} with {dbOperation.FilesFound} files found");
                _logger.LogError($"[DIAGNOSTIC-HARVEST] Sent SignalR progress update: filesFound={gcodeFileCount}");
            }
            else
            {
                _logger.LogWarning($"Could not find operation {operation.Id} in database to update files found count");
                _logger.LogError($"[DIAGNOSTIC-HARVEST] ERROR: Could not find operation {operation.Id} to update FilesFound");
            }

            // Send discovered files to frontend immediately during discovery phase (not wait for processing)
            _logger.LogInformation($"Sending {gcodeFileCount} discovered files to frontend for operation {operation.Id}");
            foreach (PrinterFileInfo fileInfo in filteredFiles)
            {
                // Separate directory path from filename
                // fileInfo.Path could be "file.gcode" or "subfolder/file.gcode"
                string fullPath = fileInfo.Path;
                string fileName = Path.GetFileName(fullPath); // Gets just the filename
                string directoryPath = Path.GetDirectoryName(fullPath) ?? string.Empty; // Gets directory portion, or empty if root

                // Save discovered file to database so user can select it
                HarvestDiscoveredFile discoveredFile = new()
                {
                    Id = Guid.NewGuid(),
                    HarvestOperationId = operation.Id,
                    FileName = fileName, // Just the filename
                    FilePath = directoryPath, // Just the directory path (empty string for root)
                    Size = fileInfo.Size,
                    ExtractedSlicerName = fileInfo.SlicerName,
                    ExtractedMaterial = fileInfo.FilamentWeightGrams.HasValue ? $"~{Math.Round(fileInfo.FilamentWeightGrams.Value)}g" : "",
                    Status = HarvestFileStatus.Pending,
                    DiscoveredAt = DateTime.UtcNow,
                    ModifiedAt = fileInfo.ModifiedAt,
                    AlreadyInLibrary = false
                };

                await scopedHarvestRepo.AddDiscoveredFileAsync(discoveredFile, ct: default);
                await scopedHarvestRepo.SaveChangesAsync(ct: default);

                // Send file discovered event directly to SignalR clients via broadcaster
                DiscoveredGcodeFileDto fileDto = MapToDto(discoveredFile);
                await _harvestEventBroadcaster.BroadcastToGroupAsync(operation.Id, "harvestfilediscovered", fileDto, CancellationToken.None);
                _logger.LogInformation($"Sent FileDiscovered event for file '{fileName}' to SignalR clients in operation {operation.Id}");
            }

            // Discovery is complete - files are now shown to user for selection
            // Queueing only happens after user selects files via ImportSelectedFilesAsync
            _logger.LogInformation($"Discovery complete for operation {operation.Id}. Waiting for user to select files to import.");

            // Signal to UI that discovery is complete and stop the spinner
            await _harvestEventBroadcaster.BroadcastToGroupAsync(operation.Id, "harvestdiscoverycomplete", new
            {
                operationId = operation.Id.ToString(),
                totalFilesDiscovered = gcodeFileCount,
                completedAt = DateTime.UtcNow.ToString("O")
            }, CancellationToken.None);
            _logger.LogInformation($"Sent DiscoveryComplete event for operation {operation.Id} with {gcodeFileCount} files");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"File discovery failed for operation {operation.Id}");

            // Mark operation as failed with detailed error info
            // CRITICAL: Must use GetOperationByIdTrackedAsync (with tracking) not GetOperationByIdAsync (AsNoTracking)
            // We need to modify the operation, so it MUST be tracked by EF Core
            GcodeHarvestOperation? dbOperation = await scopedHarvestRepo.GetOperationByIdTrackedAsync(operation.Id);
            if (dbOperation != null)
            {
                HarvestErrorHelper.SetOperationError(
                    dbOperation,
                    ex,
                    nameof(HarvestErrorPhase.Discovery),
                    failedResource: printerBackendUrl);
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
                // Use the calculated BackendUrl property which includes the proper port
                string baseUrl = printer.BackendUrl;

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
        {
            return null;
        }

        var ms = new MemoryStream(bytes);
        ms.Seek(0, SeekOrigin.Begin);
        return await Task.FromResult(ms);
    }

    /// <summary>
    /// Download thumbnail from a URL and save it locally.
    /// Returns the local file path of the saved thumbnail, or null if download fails.
    /// </summary>
    private async Task<string?> DownloadThumbnailFromUrlAsync(string thumbnailUrl, CancellationToken ct = default)
    {
        try
        {
            using HttpClient httpClient = new();
            httpClient.Timeout = TimeSpan.FromSeconds(30);
            
            byte[] thumbnailBytes = await httpClient.GetByteArrayAsync(thumbnailUrl, ct);
            
            if (thumbnailBytes == null || thumbnailBytes.Length == 0)
            {
                _logger.LogWarning("Downloaded thumbnail from {Url} but received empty data", thumbnailUrl);
                return null;
            }

            // Save to same directory as gcode files with _thumb.png suffix
            string thumbnailDir = _storagePathService.GetThumbnailDirectory();
            Directory.CreateDirectory(thumbnailDir);

            string thumbnailFileName = $"{Guid.NewGuid()}_thumb.png";
            string thumbnailPath = Path.Combine(thumbnailDir, thumbnailFileName);

            await File.WriteAllBytesAsync(thumbnailPath, thumbnailBytes, ct);
            _logger.LogInformation("Downloaded and saved thumbnail from {Url} to {Path}", thumbnailUrl, thumbnailPath);

            return thumbnailPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download thumbnail from {Url}", thumbnailUrl);
            return null;
        }
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

        string content = line[1..].Trim();

        // PrusaSlicer patterns
        if (content.StartsWith("Generated by PrusaSlicer", StringComparison.OrdinalIgnoreCase))
        {
            Match versionMatch = PrusaSlicerVersionRegex.Match(content);
            if (versionMatch.Success)
            {
                metadata = metadata with { SlicerName = "PrusaSlicer", SlicerVersion = versionMatch.Groups[1].Value };
            }
        }

        // OrcaSlicer patterns
        if (content.StartsWith("Generated by OrcaSlicer", StringComparison.OrdinalIgnoreCase))
        {
            Match versionMatch = OrcaSlicerVersionRegex.Match(content);
            if (versionMatch.Success)
            {
                metadata = metadata with { SlicerName = "OrcaSlicer", SlicerVersion = versionMatch.Groups[1].Value };
            }
        }

        // Cura patterns
        if (content.StartsWith("Generated with Cura", StringComparison.OrdinalIgnoreCase))
        {
            Match versionMatch = CuraVersionRegex.Match(content);
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
        GcodeHarvestOperation? operation = await _unitOfWork.HarvestOperations.GetOperationWithPrinterAsync(operationId, ct);

        return operation == null ? null : MapToDto(operation);
    }

    public async Task<DiscoveredGcodeFileDto[]> GetDiscoveredFilesAsync(Guid operationId, CancellationToken ct = default)
    {
        _logger.LogInformation($"Getting discovered files for operation {operationId}");

        // Verify the operation exists
        GcodeHarvestOperation? operation = await _unitOfWork.HarvestOperations.GetOperationByIdAsync(operationId, ct);
        if (operation == null)
        {
            _logger.LogWarning($"GetDiscoveredFilesAsync: Operation {operationId} not found");
            return Array.Empty<DiscoveredGcodeFileDto>();
        }

        _logger.LogInformation($"Found operation {operationId} with status {operation.Status}, files found: {operation.FilesFound}");

        // Get files with explicit logging
        List<HarvestDiscoveredFile> files = await _unitOfWork.HarvestOperations.GetDiscoveredFilesAsync(operationId, ct);

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

        List<HarvestDiscoveredFile> files = await _unitOfWork.HarvestOperations.GetDiscoveredFilesPagedAsync(operationId, page, pageSize, search, ct);
        int total = await _unitOfWork.HarvestOperations.GetDiscoveredFilesCountAsync(operationId, ct);
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
        GcodeHarvestOperation? operation = await _unitOfWork.HarvestOperations.GetOperationWithPrinterAsync(request.HarvestOperationId, ct);

        if (operation == null)
        {
            return new GcodeHarvestResultDto(request.HarvestOperationId, false, "Harvest operation not found");
        }

        _logger.LogInformationWithSource($"Received {request.FileIds.Length} file IDs to import: {string.Join(", ", request.FileIds)}");

        // Load only IDs initially - don't load entities in main context
        // Each file will be loaded fresh within its own scoped context
        List<Guid> fileIdsToImport = request.FileIds.ToList();

        _logger.LogInformationWithSource($"Processing {fileIdsToImport.Count} selected files sequentially");

        List<string> importedFileIds = new();
        List<string> skippedFileIds = new();
        List<string> failedFileIds = new();
        Dictionary<string, string> errorDetails = new();

        // Process files sequentially to avoid concurrency issues with DbContext
        // and to ensure harvest operation state is consistent
        // Each file is loaded FRESH within its own scoped context to avoid cross-context FK violations
        foreach (Guid fileId in fileIdsToImport)
        {
            _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Starting import iteration for fileId={fileId}");
            try
            {
                // Create a new scope for this import to get fresh DbContext
                // Each file is processed one at a time sequentially in its own context
                await using AsyncServiceScope scopedServices = _serviceScopeFactory.CreateAsyncScope();
                IUnitOfWork scopedUnitOfWork = scopedServices.ServiceProvider.GetRequiredService<IUnitOfWork>();
                IHarvestRepository scopedHarvestRepo = scopedUnitOfWork.HarvestOperations;
                IGcodeRepository scopedGcodeRepo = scopedUnitOfWork.GcodeFiles;

                // CRITICAL: Load discovered file FRESH within this scoped context
                // This ensures the entity belongs to this context's DbContext
                // and all FK constraints will be satisfied when we create mappings
                HarvestDiscoveredFile? discoveredFile = await scopedHarvestRepo.GetDiscoveredFileByIdAsync(fileId, operation.Id, ct);
                if (discoveredFile == null)
                {
                    _logger.LogWarning($"Discovered file {fileId} not found in operation {operation.Id}");
                    failedFileIds.Add(fileId.ToString());
                    errorDetails[fileId.ToString()] = "File not found in harvest operation";
                    continue;
                }

                _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Loaded discovered file: {discoveredFile.FileName}, Status={discoveredFile.Status}");

                if (discoveredFile.AlreadyInLibrary)
                {
                    _logger.LogInformationWithSource($"File {discoveredFile.FileName} already in library, skipping");
                    skippedFileIds.Add(discoveredFile.Id.ToString());
                    continue;
                }

                // Mark as in progress and send update (now using scoped entity)
                discoveredFile.Status = HarvestFileStatus.InProgress;
                discoveredFile.StartedAt = DateTime.UtcNow;
                await scopedHarvestRepo.SaveChangesAsync(ct);

                _logger.LogInformationWithSource($"[IMPORT-LIFECYCLE] Updated status to InProgress for {discoveredFile.FileName}, saved to DB");
                await _harvestEventBroadcaster.BroadcastToGroupAsync(operation.Id, "harvestfileupdated", MapToDto(discoveredFile), ct);
                _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Sent harvestfileupdated event for {discoveredFile.FileName}");

                // Get storage directory from centralized storage service (supports Docker and K8s)
                string storageDir = _storagePathService.GetGcodeStorageDirectory();
                _ = Directory.CreateDirectory(storageDir);
                // Generate unique filename using pure GUID (consistent with 3D model file storage)
                string extension = Path.GetExtension(discoveredFile.FileName);
                string fileName = $"{Guid.NewGuid()}{extension}";
                string filePath = Path.Combine(storageDir, fileName);

                // Download file from printer
                if (operation.Printer == null)
                {
                    discoveredFile.Status = HarvestFileStatus.Failed;
                    discoveredFile.Error = "Printer information not available for download";
                    discoveredFile.CompletedAt = DateTime.UtcNow;
                    await scopedHarvestRepo.SaveChangesAsync(ct);
                    await _harvestEventBroadcaster.BroadcastToGroupAsync(operation.Id, "harvestfileupdated", MapToDto(discoveredFile), ct);
                    failedFileIds.Add(discoveredFile.Id.ToString());
                    errorDetails[discoveredFile.Id.ToString()] = "Printer information not available";
                    continue;
                }

                // Combine directory path and filename to get full path for download
                // FilePath contains directory (empty for root), FileName contains just the filename
                string fullPathForDownload = string.IsNullOrWhiteSpace(discoveredFile.FilePath)
                    ? discoveredFile.FileName  // Root directory: just use filename
                    : $"{discoveredFile.FilePath}/{discoveredFile.FileName}"; // Subdirectory: combine with /

                PrinterBackend backend = (PrinterBackend)operation.Printer.Backend;
                _logger.LogInformation($"[IMPORT-LIFECYCLE] About to download file: FileName={discoveredFile.FileName}, FilePath={discoveredFile.FilePath}, FullPath={fullPathForDownload}, Backend={backend}");
                using MemoryStream? gcodeContent = await DownloadFileAsync(backend, operation.Printer, fullPathForDownload);

                if (gcodeContent == null)
                {
                    _logger.LogWarning($"[IMPORT-LIFECYCLE] Download returned null for {discoveredFile.FileName}, FilePath was: {discoveredFile.FilePath}");
                    discoveredFile.Status = HarvestFileStatus.Failed;
                    discoveredFile.Error = $"Failed to download {discoveredFile.FileName}";
                    discoveredFile.CompletedAt = DateTime.UtcNow;
                    await scopedHarvestRepo.SaveChangesAsync(ct);
                    await _harvestEventBroadcaster.BroadcastToGroupAsync(operation.Id, "harvestfileupdated", MapToDto(discoveredFile), ct);
                    failedFileIds.Add(discoveredFile.Id.ToString());
                    errorDetails[discoveredFile.Id.ToString()] = $"Failed to download {discoveredFile.FileName}";
                    continue;
                }

                _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Successfully downloaded {discoveredFile.FileName}, size={gcodeContent.Length} bytes");

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
                        // Send progress update every 512KB or on completion
                        if (bytesCopied == totalBytes || bytesCopied % (512 * 1024) < bufferSize)
                        {
                            double percent = totalBytes > 0 ? (bytesCopied * 100.0 / totalBytes) : 0;
                            await _harvestEventBroadcaster.BroadcastToGroupAsync(operation.Id, "harvestfileprogress", new
                            {
                                operationId = operation.Id,
                                fileName = discoveredFile.FileName,
                                bytesCopied,
                                totalBytes,
                                percent
                            }, ct);
                        }
                    }
                }
                _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Saved file to disk: {filePath}");

                // CRITICAL: Calculate file hash for deduplication
                // This MUST be done after download/save so we hash actual file content
                // Not doing this causes all files to have empty hash → duplicate constraint violations
                string fileHash = "";
                try
                {
                    gcodeContent.Position = 0;
                    fileHash = await CalculateFileHashAsync(gcodeContent, ct);
                    discoveredFile.FileHash = fileHash;
                    _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Calculated file hash for {discoveredFile.FileName}: {fileHash}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to calculate hash for {FileName}, using empty hash (will not deduplicate)", discoveredFile.FileName);
                    discoveredFile.FileHash = "";
                    fileHash = "";
                }

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

                        // Save extracted metadata back to discovered file so UI can display it immediately
                        if (extractedMetadata != null)
                        {
                            // Only overwrite if discovered file doesn't already have the value from API
                            if (!discoveredFile.ExtractedNozzleDiameter.HasValue && extractedMetadata.NozzleDiameter.HasValue)
                            {
                                discoveredFile.ExtractedNozzleDiameter = extractedMetadata.NozzleDiameter;
                            }

                            if (string.IsNullOrEmpty(discoveredFile.ExtractedMaterial) && !string.IsNullOrEmpty(extractedMetadata.Material))
                            {
                                discoveredFile.ExtractedMaterial = extractedMetadata.Material;
                            }

                            if (!discoveredFile.ExtractedPrintTime.HasValue && extractedMetadata.EstimatedPrintTimeMinutes.HasValue)
                            {
                                discoveredFile.ExtractedPrintTime = extractedMetadata.EstimatedPrintTimeMinutes;
                            }

                            if (!discoveredFile.ExtractedFilamentLength.HasValue && extractedMetadata.FilamentLengthMm.HasValue)
                            {
                                discoveredFile.ExtractedFilamentLength = extractedMetadata.FilamentLengthMm;
                            }

                            if (string.IsNullOrEmpty(discoveredFile.ExtractedSlicerName) && !string.IsNullOrEmpty(extractedMetadata.SlicerName))
                            {
                                discoveredFile.ExtractedSlicerName = extractedMetadata.SlicerName;
                            }

                            if (string.IsNullOrEmpty(discoveredFile.ExtractedSlicerVersion) && !string.IsNullOrEmpty(extractedMetadata.SlicerVersion))
                            {
                                discoveredFile.ExtractedSlicerVersion = extractedMetadata.SlicerVersion;
                            }

                            _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Updated discovered file metadata: nozzle={discoveredFile.ExtractedNozzleDiameter}, material={discoveredFile.ExtractedMaterial}, slicer={discoveredFile.ExtractedSlicerName}");
                        }

                        // Download thumbnail from printer API if available (preferred over extraction)
                        if (!string.IsNullOrEmpty(discoveredFile.ThumbnailUrl))
                        {
                            try
                            {
                                _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Downloading thumbnail from API: {discoveredFile.ThumbnailUrl}");
                                thumbnailPath = await DownloadThumbnailFromUrlAsync(discoveredFile.ThumbnailUrl, ct);
                                if (thumbnailPath != null)
                                {
                                    _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Successfully downloaded thumbnail from API to: {thumbnailPath}");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to download thumbnail from API for {FileName}, will try extraction", discoveredFile.FileName);
                                thumbnailPath = null;
                            }
                        }

                        // Fall back to extracting thumbnail from gcode content if not downloaded from API
                        if (thumbnailPath == null && extractedMetadata?.ThumbnailData != null && extractedMetadata.ThumbnailData.Length > 0)
                        {
                            try
                            {
                                _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Extracting thumbnail from gcode content for {discoveredFile.FileName}");
                                gcodeContent.Position = 0;
                                thumbnailPath = await _thumbnailExtractor.ExtractAndSaveThumbnailAsync(gcodeContent, ct);
                                if (thumbnailPath != null)
                                {
                                    _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Successfully extracted thumbnail to: {thumbnailPath}");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to extract thumbnail from gcode for {FileName}", discoveredFile.FileName);
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

                // Check if a file with this hash already exists in the library
                // If it does, reuse it instead of creating a duplicate
                GcodeFile? existingFile = null;
                if (!string.IsNullOrEmpty(discoveredFile.FileHash))
                {
                    existingFile = await scopedGcodeRepo.FindByHashAsync(discoveredFile.FileHash, ct);
                    if (existingFile != null)
                    {
                        _logger.LogInformationWithSource($"[IMPORT-LIFECYCLE] Found existing GcodeFile with same hash: {existingFile.Id} ({existingFile.FileName})");
                    }
                }

                GcodeFile gcodeFile;
                if (existingFile != null)
                {
                    // Reuse the existing file - no need to create a new one
                    gcodeFile = existingFile;
                    _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Reusing existing GcodeFile for duplicate content: Id={gcodeFile.Id}");
                }
                else
                {
                    // Get or create root folder for gcode files (use "/" as root, not the physical path)
                    // This ensures folder paths are relative and don't include "/app" prefix
                    var targetFolder = await GetOrCreateFolderAsync("/", "gcode", scopedServices, ct);
                    _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Got or created folder: {targetFolder.Path}, Id={targetFolder.Id}");

                    // Create library entry
                    // Note: We use TargetModelId instead of SourcePrinterId so the file is usable on ANY printer
                    // of the same model, not just the one it was harvested from
                    gcodeFile = new()
                    {
                        Id = Guid.NewGuid(),
                        FileName = discoveredFile.FileName,
                        FolderId = targetFolder.Id,
                        FilePath = filePath,
                        FileSizeBytes = discoveredFile.Size,
                        FileHash = discoveredFile.FileHash ?? "",
                        UploadedAt = DateTime.UtcNow,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
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
                        ThumbnailFileName = thumbnailPath, // This should be JUST The filename, not file path + filename.
                        Tags = request.DefaultTags != null ? JsonSerializer.Serialize(request.DefaultTags) : null
                    };

                    _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Created GcodeFile object: Id={gcodeFile.Id}, FolderId={targetFolder.Id}, TargetModelId={gcodeFile.TargetModelId}");

                    // Add the gcode file to the database
                    await scopedGcodeRepo.AddAsync(gcodeFile, ct);
                    await scopedGcodeRepo.SaveChangesAsync(ct);
                    _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Saved GcodeFile to database: {gcodeFile.Id}");
                }

                // Now create mapping between discovered file and imported gcode file
                // At this point both files are committed to the database
                _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Preparing to create mapping for discoveredFile={discoveredFile.Id} -> gcodeFile={gcodeFile.Id}");
                await scopedHarvestRepo.CreateFileImportMappingAsync(discoveredFile, gcodeFile, ct);

                _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Created file import mapping: discoveredFileId={discoveredFile.Id} -> gcodeFileId={gcodeFile.Id}");

                // Save the mapping
                await scopedHarvestRepo.SaveChangesAsync(ct);
                _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Saved mapping to database");

                // Mark as complete now that the mapping was persisted
                discoveredFile.Status = HarvestFileStatus.Complete;
                discoveredFile.CompletedAt = DateTime.UtcNow;
                _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Marked discoveredFile status as Complete");
                await scopedHarvestRepo.SaveChangesAsync(ct);
                _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Saved discovered file status: {discoveredFile.Id}, Status={discoveredFile.Status}");

                // Broadcast completion update
                await _harvestEventBroadcaster.BroadcastToGroupAsync(operation.Id, "harvestfileupdated", MapToDto(discoveredFile), ct);

                _logger.LogInformationWithSource($"✅ [IMPORT-LIFECYCLE] Successfully imported file: {discoveredFile.FileName} -> {gcodeFile.Id}");

                importedFileIds.Add(discoveredFile.Id.ToString());

                // Increment the operation's FilesAdded counter (now thread-safe since sequential)
                operation.FilesAdded++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ [IMPORT-LIFECYCLE] EXCEPTION in import for file {fileId}: {ex.GetType().Name} - {ex.Message}");
                _logger.LogDebug($"[IMPORT-LIFECYCLE] Exception stack: {ex.StackTrace}");

                // Extract the actual database error message if available
                string errorMessage = ex.Message;
                if (ex is DbUpdateException dbEx && dbEx.InnerException != null)
                {
                    // For database errors, try to extract the real error message
                    var innerEx = dbEx.InnerException;

                    // Try to get PostgresException details (Npgsql)
                    var sqlStateProperty = innerEx.GetType().GetProperty("SqlState");
                    var messageTextProperty = innerEx.GetType().GetProperty("MessageText");

                    if (sqlStateProperty?.GetValue(innerEx) is string sqlState &&
                        messageTextProperty?.GetValue(innerEx) is string messageText)
                    {
                        errorMessage = $"Database constraint violation: {messageText} (SQL State: {sqlState})";
                        _logger.LogError($"[IMPORT-LIFECYCLE] PostgreSQL Error Detail: {errorMessage}");
                    }
                    else
                    {
                        errorMessage = $"Database error: {innerEx.Message}";
                    }
                }

                // Save the failed status update in a fresh scoped context
                await using (AsyncServiceScope errorScope = _serviceScopeFactory.CreateAsyncScope())
                {
                    IUnitOfWork errorUnitOfWork = errorScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                    IHarvestRepository errorHarvestRepo = errorUnitOfWork.HarvestOperations;

                    _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Loading discovered file in error scope: {fileId}");

                    // Reload the discovered file to update its status
                    HarvestDiscoveredFile? dbFile = await errorHarvestRepo.GetDiscoveredFileByIdAsync(fileId, operation.Id, ct);
                    if (dbFile != null)
                    {
                        _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Found discovered file in error scope: {dbFile.FileName}, current status={dbFile.Status}");

                        dbFile.Status = HarvestFileStatus.Failed;
                        dbFile.Error = $"Failed to import {dbFile.FileName}: {errorMessage}";
                        dbFile.CompletedAt = DateTime.UtcNow;

                        _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Updated status to Failed, saving...");

                        await errorHarvestRepo.SaveChangesAsync(ct);

                        _logger.LogDebugWithSource($"[IMPORT-LIFECYCLE] Saved failed status, broadcasting event...");

                        // Send failure event to UI
                        await _harvestEventBroadcaster.BroadcastToGroupAsync(operation.Id, "harvestfileupdated", MapToDto(dbFile), ct);

                        failedFileIds.Add(fileId.ToString());
                        errorDetails[fileId.ToString()] = $"Failed to import {dbFile.FileName}: {errorMessage}";

                        _logger.LogWarning($"[IMPORT-LIFECYCLE] Marked file as Failed and sent event: {dbFile.FileName}");
                    }
                    else
                    {
                        _logger.LogWarning($"❌ [IMPORT-LIFECYCLE] Could not find discovered file {fileId} to mark as failed");
                        failedFileIds.Add(fileId.ToString());
                        errorDetails[fileId.ToString()] = "Failed to import (file not found)";
                    }
                }
            }
        }

        try
        {
            _logger.LogInformationWithSource($"Saving harvest operation with {importedFileIds.Count} files added to database");
            await _unitOfWork.SaveChangesAsync(ct);
            _logger.LogInformationWithSource($"Harvest operation saved successfully");
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithSource(ex, $"Error saving harvest operation: {ex.Message} | Inner: {ex.InnerException?.Message}");
            // Don't throw - we want to return partial results to the client
            // Add this error to the general errors list
            if (errorDetails == null)
            {
                errorDetails = new Dictionary<string, string>();
            }

            errorDetails["_operation"] = $"Failed to save harvest operation metadata: {ex.Message}";
        }

        GcodeHarvestResultDto result = new GcodeHarvestResultDto(
            request.HarvestOperationId,
            true,
            $"Imported {importedFileIds.Count} files",
            fileIdsToImport.Count,
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
        GcodeHarvestOperation? operation = await _unitOfWork.HarvestOperations.GetOperationByIdTrackedAsync(operationId, ct);

        if (operation == null || operation.Status != GcodeHarvestStatus.Running)
        {
            return false;
        }

        operation.Status = GcodeHarvestStatus.Cancelled;
        operation.CompletedAt = DateTime.UtcNow;
        await _unitOfWork.SaveChangesAsync(ct);

        // Log the cancellation for tracking purposes
        _logger.LogInformation($"Harvest operation {operationId} was cancelled");

        // Send cancellation event to SignalR clients
        await _harvestEventBroadcaster.BroadcastToGroupAsync(operationId, "harvestoperationcancelled", new
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
        GcodeHarvestOperation? operation = await _unitOfWork.HarvestOperations.GetOperationByIdTrackedAsync(operationId, ct);

        if (operation == null || operation.Status != GcodeHarvestStatus.Running)
        {
            return false;
        }

        // Get the printer to verify it exists and get its details
        Printer? printer = await _unitOfWork.Printers.FindByIdAsync(operation.PrinterId, ct);
        if (printer == null)
        {
            _logger.LogError($"Printer {operation.PrinterId} for harvest operation {operationId} not found");
            return false;
        }

        _logger.LogInformation($"Restarting file discovery for operation {operationId} on printer {printer.Name}");

        // Clear discovered files to restart fresh
        await _unitOfWork.HarvestOperations.DeleteDiscoveredFilesByOperationAsync(operationId, ct);

        // Reset operation statistics
        operation.FilesFound = 0;
        operation.FilesAdded = 0;
        operation.FilesSkipped = 0;
        operation.FilesErrored = 0;
        operation.TotalBytesProcessed = 0;

        // Save changes
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation($"Cleared discovered files for operation {operationId}, restarting discovery");

        // Send restart event to SignalR clients
        await _harvestEventBroadcaster.BroadcastToGroupAsync(operationId, "harvestdiscoveryrestarted", new
        {
            operationId,
            status = "restarting",
            restartedAt = DateTime.UtcNow
        }, ct);

        // Extract essential printer data BEFORE background task
        Guid printerId = printer.Id;
        string printerName = printer.Name;
        string printerBackendUrl = printer.BackendUrl;  // Use calculated BackendUrl with port
        string printerApiKey = printer.ApiKey ?? "";
        PrinterBackend printerBackend = (PrinterBackend)printer.Backend;

        // Start fresh discovery in background (using same pattern as StartHarvestAsync)
        _ = ThreadPool.QueueUserWorkItem(async (state) =>
        {
            try
            {
                _logger.LogInformation($"🔄 Background harvest restart task STARTED for operation {operationId} on printer {printerName}");
                await DiscoverAndQueueFilesAsync(operation, printerId, printerName, printerBackendUrl, printerApiKey, printerBackend);
                _logger.LogInformation($"✅ Background harvest restart task COMPLETED successfully for operation {operationId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Background harvest restart task FAILED for operation {operationId}: {ex.Message}");

                // Update the operation status to failed with detailed error info
                await using AsyncServiceScope scope = _serviceScopeFactory.CreateAsyncScope();
                IUnitOfWork scopedUnitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                IHarvestRepository scopedHarvestRepo = scopedUnitOfWork.HarvestOperations;
                GcodeHarvestOperation? dbOperation = await scopedHarvestRepo.GetOperationByIdTrackedAsync(operationId);
                if (dbOperation != null)
                {
                    HarvestErrorHelper.SetOperationError(
                        dbOperation,
                        ex,
                        nameof(HarvestErrorPhase.Discovery),
                        failedResource: printerBackendUrl);
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
                _logger.LogDebug($"Harvest restart background work completed for operation {operationId}");
            }
        });

        _logger.LogInformation($"Discovery restart queued for operation {operationId}");

        return true;
    }

    public async Task<GcodeHarvestOperationDto?> GetActiveHarvestAsync(Guid printerId, CancellationToken ct = default)
    {
        GcodeHarvestOperation? operation = await _unitOfWork.HarvestOperations.GetActiveOperationForPrinterAsync(printerId, ct);

        return operation == null ? null : MapToDto(operation);
    }

    public async Task<GcodeHarvestOperationDto[]> GetRecentHarvestsAsync(Guid printerId, int count = 10, CancellationToken ct = default)
    {
        List<GcodeHarvestOperation> operations = await _unitOfWork.HarvestOperations.GetRecentOperationsForPrinterAsync(printerId, count, ct);

        return operations.Select(MapToDto).ToArray();
    }

    public async Task<GcodeHarvestOperationDto[]> GetActiveHarvestsAsync(CancellationToken ct = default)
    {
        List<GcodeHarvestOperation> operations = await _unitOfWork.HarvestOperations.GetActiveOperationsAsync(ct);

        return operations.Select(MapToDto).ToArray();
    }

    public async Task<GcodeHarvestOperationDto[]> GetHarvestOperationsAsync(Guid? printerId = null, string? status = null, int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        GcodeHarvestStatus? statusEnum = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse(status, true, out GcodeHarvestStatus parsedStatus))
        {
            statusEnum = parsedStatus;
        }

        List<GcodeHarvestOperation> operations = await _unitOfWork.HarvestOperations.GetOperationsAsync(printerId, statusEnum, limit, offset, ct);

        return operations.Select(MapToDto).ToArray();
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
            null, // ExtractedInfill (not available)
            file.Status // Status enum - for UI display
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
        public double? FilamentWeightGrams { get; set; }
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

    /// <summary>
    /// Gets or creates a Folder entity for the given directory path and type
    /// </summary>
    private async Task<Folder> GetOrCreateFolderAsync(string directoryPath, string folderType, AsyncServiceScope scope, CancellationToken ct)
    {
        // Use the provided scoped AppDbContext to maintain consistency within the same transaction
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Normalize path
        string normalizedPath = string.IsNullOrWhiteSpace(directoryPath) ? "/" : directoryPath.TrimEnd(Path.DirectorySeparatorChar, '/');

        // Try to find existing folder
        var existingFolder = await db.Folders
            .FirstOrDefaultAsync(f => f.Path == normalizedPath && f.FolderType == folderType && !f.DeletedAt.HasValue, ct);

        if (existingFolder != null)
        {
            return existingFolder;
        }

        // Create new folder in the same context
        var newFolder = new Folder
        {
            Id = Guid.NewGuid(),
            Path = normalizedPath,
            FolderType = folderType,
            CreatedAt = DateTime.UtcNow
        };

        await db.AddAsync(newFolder, ct);
        await db.SaveChangesAsync(ct);

        return newFolder;
    }

}
