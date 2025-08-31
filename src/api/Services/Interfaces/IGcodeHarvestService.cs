using Farm.Web.Api.Domain;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.Interfaces;

/// <summary>
/// Service for harvesting G-code files from registered printers
/// </summary>
public interface IGcodeHarvestService
{
    /// <summary>
    /// Start a harvest operation for a specific printer
    /// </summary>
    Task<GcodeHarvestResultDto> StartHarvestAsync(StartGcodeHarvestDto request, CancellationToken ct = default);
    
    /// <summary>
    /// Get the status of a harvest operation
    /// </summary>
    Task<GcodeHarvestOperationDto?> GetHarvestOperationAsync(Guid operationId, CancellationToken ct = default);
    
    /// <summary>
    /// Get discovered files from a harvest operation
    /// </summary>
    Task<DiscoveredGcodeFileDto[]> GetDiscoveredFilesAsync(Guid operationId, CancellationToken ct = default);
    
    /// <summary>
    /// Import selected discovered files to the library
    /// </summary>
    Task<GcodeHarvestResultDto> ImportSelectedFilesAsync(ImportSelectedGcodeFilesDto request, CancellationToken ct = default);
    
    /// <summary>
    /// Cancel a running harvest operation
    /// </summary>
    Task<bool> CancelHarvestAsync(Guid operationId, CancellationToken ct = default);
    
    /// <summary>
    /// Get recent harvest operations for a printer
    /// </summary>
    Task<GcodeHarvestOperationDto[]> GetRecentHarvestsAsync(Guid printerId, int count = 10, CancellationToken ct = default);
    
    /// <summary>
    /// Extract metadata from G-code content
    /// </summary>
    Task<GcodeMetadataDto> ExtractMetadataAsync(Stream gcodeStream, CancellationToken ct = default);
    
    /// <summary>
    /// Calculate SHA256 hash of G-code file for deduplication
    /// </summary>
    Task<string> CalculateFileHashAsync(Stream fileStream, CancellationToken ct = default);
}
