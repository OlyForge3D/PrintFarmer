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
    /// Get discovered files (paged) with optional name filter
    /// </summary>
    Task<PagedResult<DiscoveredGcodeFileDto>> GetDiscoveredFilesPagedAsync(Guid operationId, int page = 1, int pageSize = 50, string? search = null, CancellationToken ct = default);

    /// <summary>
    /// Import selected discovered files to the library
    /// </summary>
    Task<GcodeHarvestResultDto> ImportSelectedFilesAsync(ImportSelectedGcodeFilesDto request, CancellationToken ct = default);

    /// <summary>
    /// Cancel a running harvest operation
    /// </summary>
    Task<bool> CancelHarvestAsync(Guid operationId, CancellationToken ct = default);

    /// <summary>
    /// Get the active harvest operation for a printer, if any
    /// </summary>
    Task<GcodeHarvestOperationDto?> GetActiveHarvestAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Get recent harvest operations for a printer
    /// </summary>
    Task<GcodeHarvestOperationDto[]> GetRecentHarvestsAsync(Guid printerId, int count = 10, CancellationToken ct = default);

    /// <summary>
    /// Get all active (running) harvest operations
    /// </summary>
    Task<GcodeHarvestOperationDto[]> GetActiveHarvestsAsync(CancellationToken ct = default);

    /// <summary>
    /// Get harvest operations with optional filtering
    /// </summary>
    Task<GcodeHarvestOperationDto[]> GetHarvestOperationsAsync(Guid? printerId = null, string? status = null, int limit = 100, int offset = 0, CancellationToken ct = default);

    /// <summary>
    /// Extract metadata from G-code content
    /// </summary>
    Task<GcodeMetadataDto> ExtractMetadataAsync(Stream gcodeStream, CancellationToken ct = default);

    /// <summary>
    /// Calculate SHA256 hash of G-code file for deduplication
    /// </summary>
    Task<string> CalculateFileHashAsync(Stream fileStream, CancellationToken ct = default);

    /// <summary>
    /// Gets information about all currently running harvest tasks
    /// </summary>
    /// <returns>Dictionary of operation IDs and their current status</returns>
    IDictionary<Guid, bool> GetActiveTasksStatus();

    /// <summary>
    /// Wait for all active tasks to complete or cancel them after timeout
    /// </summary>
    /// <param name="timeout">Maximum time to wait</param>
    /// <param name="ct">Cancellation token</param>
    Task WaitForAllTasksAsync(TimeSpan timeout, CancellationToken ct = default);
}
