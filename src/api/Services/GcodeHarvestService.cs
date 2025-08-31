using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Api.Services.Interfaces;
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
    private readonly IWebHostEnvironment _environment;
    
    private static readonly string GcodeStoragePath = "gcode-library";
    
    public GcodeHarvestService(
        AppDbContext db,
        IMoonrakerClient moonraker,
        IPrusaLinkClient prusa,
        ISdcpClient sdcp,
        ILogger<GcodeHarvestService> logger,
        IWebHostEnvironment environment)
    {
        _db = db;
        _moonraker = moonraker;
        _prusa = prusa;
        _sdcp = sdcp;
        _logger = logger;
        _environment = environment;
    }

    public async Task<GcodeHarvestResultDto> StartHarvestAsync(StartGcodeHarvestDto request, CancellationToken ct = default)
    {
        var printer = await _db.Printers.FirstOrDefaultAsync(p => p.Id == request.PrinterId, ct);
        if (printer == null)
        {
            return new GcodeHarvestResultDto(Guid.Empty, false, "Printer not found");
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
            ModifiedAfter = request.ModifiedAfter
        };

        _db.GcodeHarvestOperations.Add(operation);
        await _db.SaveChangesAsync(ct);

        // Start harvest in background
        _ = Task.Run(async () => await PerformHarvestAsync(operation, printer), ct);

        return new GcodeHarvestResultDto(
            operation.Id, 
            true, 
            "Harvest operation started",
            DiscoveredFiles: 0,
            ImportedFiles: 0);
    }

    private async Task PerformHarvestAsync(GcodeHarvestOperation operation, Printer printer)
    {
        try
        {
            _logger.LogInformation("Starting G-code harvest for printer {PrinterName} ({PrinterId})", 
                printer.Name, printer.Id);

            var discoveredFiles = new List<DiscoveredGcodeFile>();
            
            // Get file list from printer based on backend type
            var backend = (PrinterBackend)printer.Backend;
            var fileList = backend switch
            {
                PrinterBackend.Moonraker => await GetMoonrakerFilesAsync(printer.ServerUrl),
                PrinterBackend.PrusaLink => await GetPrusaLinkFilesAsync(printer.ServerUrl, printer.ApiKey),
                PrinterBackend.SDCP => await GetSdcpFilesAsync(printer.ServerUrl),
                _ => new List<PrinterFileInfo>()
            };

            operation.FilesFound = fileList.Count;
            await UpdateOperationAsync(operation);

            // Process each file
            foreach (var fileInfo in fileList)
            {
                if (!fileInfo.Name.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Check size limit
                if (operation.MaxFileSizeBytes.HasValue && fileInfo.Size > operation.MaxFileSizeBytes.Value)
                {
                    _logger.LogDebug("Skipping file {FileName} - too large ({Size} bytes)", 
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
                    var gcodeContent = await DownloadFileAsync(backend, printer, fileInfo.Path);
                    if (gcodeContent != null)
                    {
                        // Calculate hash for deduplication
                        discoveredFile.FileHash = await CalculateFileHashAsync(gcodeContent);
                        
                        // Check if already in library
                        var existingFile = await _db.GcodeFiles
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
                    _logger.LogWarning(ex, "Failed to process file {FileName}", fileInfo.Name);
                    discoveredFile.ProcessingFailed = true;
                    discoveredFile.ErrorMessage = ex.Message;
                    operation.FilesErrored++;
                }

                discoveredFiles.Add(discoveredFile);
                await UpdateOperationAsync(operation);
            }

            // Save all discovered files
            _db.DiscoveredGcodeFiles.AddRange(discoveredFiles);
            operation.Status = GcodeHarvestStatus.Completed;
            operation.CompletedAt = DateTime.UtcNow;
            
            await _db.SaveChangesAsync();
            
            _logger.LogInformation("Completed G-code harvest for printer {PrinterName}. Found: {Found}, Skipped: {Skipped}, Errors: {Errors}",
                printer.Name, operation.FilesFound, operation.FilesSkipped, operation.FilesErrored);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Harvest operation failed for printer {PrinterName}", printer.Name);
            operation.Status = GcodeHarvestStatus.Failed;
            operation.ErrorMessage = ex.Message;
            operation.CompletedAt = DateTime.UtcNow;
            await UpdateOperationAsync(operation);
        }
    }

    private async Task<MemoryStream?> DownloadFileAsync(PrinterBackend backend, Printer printer, string filePath)
    {
        try
        {
            return backend switch
            {
                PrinterBackend.Moonraker => await DownloadMoonrakerFileAsync(printer.ServerUrl, filePath),
                PrinterBackend.PrusaLink => await DownloadPrusaLinkFileAsync(printer.ServerUrl, printer.ApiKey, filePath),
                PrinterBackend.SDCP => await DownloadSdcpFileAsync(printer.ServerUrl, filePath),
                _ => null
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download file {FilePath} from printer {PrinterName}", 
                filePath, printer.Name);
            return null;
        }
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
        var files = await _db.DiscoveredGcodeFiles
            .Where(d => d.HarvestOperationId == operationId)
            .OrderBy(d => d.FileName)
            .ToArrayAsync(ct);
            
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
                var storageDir = Path.Combine(_environment.ContentRootPath, "wwwroot", GcodeStoragePath);
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
        
        return true;
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

    // Helper methods for different printer backends
    private async Task<List<PrinterFileInfo>> GetMoonrakerFilesAsync(string serverUrl)
    {
        // Implementation depends on Moonraker file API
        // This is a placeholder - implement based on Moonraker's file listing API
        return new List<PrinterFileInfo>();
    }

    private async Task<List<PrinterFileInfo>> GetPrusaLinkFilesAsync(string serverUrl, string? apiKey)
    {
        // Implementation depends on PrusaLink file API
        return new List<PrinterFileInfo>();
    }

    private async Task<List<PrinterFileInfo>> GetSdcpFilesAsync(string serverUrl)
    {
        // Implementation depends on SDCP file API
        return new List<PrinterFileInfo>();
    }

    private async Task<MemoryStream?> DownloadMoonrakerFileAsync(string serverUrl, string filePath)
    {
        // Implementation for downloading files from Moonraker
        return null;
    }

    private async Task<MemoryStream?> DownloadPrusaLinkFileAsync(string serverUrl, string? apiKey, string filePath)
    {
        // Implementation for downloading files from PrusaLink
        return null;
    }

    private async Task<MemoryStream?> DownloadSdcpFileAsync(string serverUrl, string filePath)
    {
        // Implementation for downloading files from SDCP
        return null;
    }

    private async Task UpdateOperationAsync(GcodeHarvestOperation operation)
    {
        _db.GcodeHarvestOperations.Update(operation);
        await _db.SaveChangesAsync();
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
}
