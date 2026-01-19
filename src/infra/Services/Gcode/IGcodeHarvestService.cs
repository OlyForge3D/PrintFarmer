using Farm.Infrastructure;

namespace Farm.Infrastructure.Services.Gcode;

/// <summary>
/// Service for harvesting G-code files from registered printers
/// </summary>
public interface IGcodeHarvestService
{
    /// <summary>
    /// Skip a discovered file in a harvest operation
    /// </summary>
    /// <param name="operationId">The harvest operation identifier</param>
    /// <param name="fileId">The discovered file identifier to skip</param>
    /// <param name="ct">Cancellation token</param>
    Task<bool> SkipDiscoveredFileAsync(Guid operationId, Guid fileId, CancellationToken ct = default);

    /// <summary>
    /// Retry a failed discovered file in a harvest operation
    /// </summary>
    /// <param name="operationId">The harvest operation identifier</param>
    /// <param name="fileId">The discovered file identifier to retry</param>
    /// <param name="ct">Cancellation token</param>
    Task<bool> RetryDiscoveredFileAsync(Guid operationId, Guid fileId, CancellationToken ct = default);

    /// <summary>
    /// Start a harvest operation for a specific printer
    /// </summary>
    /// <param name="request">The harvest request details</param>
    /// <param name="ct">Cancellation token</param>
    Task<GcodeHarvestResultDto> StartHarvestAsync(StartGcodeHarvestDto request, CancellationToken ct = default);

    /// <summary>
    /// Get the status of a harvest operation
    /// </summary>
    /// <param name="operationId">The harvest operation identifier</param>
    /// <param name="ct">Cancellation token</param>
    Task<GcodeHarvestOperationDto?> GetHarvestOperationAsync(Guid operationId, CancellationToken ct = default);

    /// <summary>
    /// Get discovered files from a harvest operation
    /// </summary>
    /// <param name="operationId">The harvest operation identifier</param>
    /// <param name="ct">Cancellation token</param>
    Task<DiscoveredGcodeFileDto[]> GetDiscoveredFilesAsync(Guid operationId, CancellationToken ct = default);

    /// <summary>
    /// Get discovered files (paged) with optional name filter
    /// </summary>
    /// <param name="operationId">The harvest operation identifier</param>
    /// <param name="page">Page number (1-based)</param>
    /// <param name="pageSize">Number of items per page</param>
    /// <param name="search">Optional search filter for file names</param>
    /// <param name="ct">Cancellation token</param>
    Task<PagedResult<DiscoveredGcodeFileDto>> GetDiscoveredFilesPagedAsync(Guid operationId, int page = 1, int pageSize = 50, string? search = null, CancellationToken ct = default);

    /// <summary>
    /// Import selected discovered files to the library
    /// </summary>
    /// <param name="request">The import request with selected file identifiers</param>
    /// <param name="ct">Cancellation token</param>
    Task<GcodeHarvestResultDto> ImportSelectedFilesAsync(ImportSelectedGcodeFilesDto request, CancellationToken ct = default);

    /// <summary>
    /// Cancel a running harvest operation
    /// </summary>
    /// <param name="operationId">The harvest operation identifier to cancel</param>
    /// <param name="ct">Cancellation token</param>
    Task<bool> CancelHarvestAsync(Guid operationId, CancellationToken ct = default);

    /// <summary>
    /// Restart/resume file discovery for a stalled or paused harvest operation
    /// Clears discovered files and restarts the discovery process from scratch
    /// </summary>
    /// <param name="operationId">The harvest operation identifier to restart</param>
    /// <param name="ct">Cancellation token</param>
    Task<bool> RestartDiscoveryAsync(Guid operationId, CancellationToken ct = default);

    /// <summary>
    /// Get the active harvest operation for a printer, if any
    /// </summary>
    /// <param name="printerId">The printer identifier</param>
    /// <param name="ct">Cancellation token</param>
    Task<GcodeHarvestOperationDto?> GetActiveHarvestAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Get recent harvest operations for a printer
    /// </summary>
    /// <param name="printerId">The printer identifier</param>
    /// <param name="count">Maximum number of operations to return</param>
    /// <param name="ct">Cancellation token</param>
    Task<GcodeHarvestOperationDto[]> GetRecentHarvestsAsync(Guid printerId, int count = 10, CancellationToken ct = default);

    /// <summary>
    /// Get all active (running) harvest operations
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    Task<GcodeHarvestOperationDto[]> GetActiveHarvestsAsync(CancellationToken ct = default);

    /// <summary>
    /// Get harvest operations with optional filtering
    /// </summary>
    /// <param name="printerId">Optional printer identifier to filter by</param>
    /// <param name="status">Optional status to filter by</param>
    /// <param name="limit">Maximum number of operations to return</param>
    /// <param name="offset">Number of operations to skip</param>
    /// <param name="ct">Cancellation token</param>
    Task<GcodeHarvestOperationDto[]> GetHarvestOperationsAsync(Guid? printerId = null, string? status = null, int limit = 100, int offset = 0, CancellationToken ct = default);

    /// <summary>
    /// Extract metadata from G-code content
    /// </summary>
    /// <param name="gcodeStream">The G-code file stream to extract metadata from</param>
    /// <param name="ct">Cancellation token</param>
    Task<GcodeMetadataDto> ExtractMetadataAsync(Stream gcodeStream, CancellationToken ct = default);

    /// <summary>
    /// Calculate SHA256 hash of G-code file for deduplication
    /// </summary>
    /// <param name="fileStream">The file stream to calculate hash for</param>
    /// <param name="ct">Cancellation token</param>
    Task<string> CalculateFileHashAsync(Stream fileStream, CancellationToken ct = default);

    /// <summary>
    /// Gets information about all currently running harvest tasks
    /// </summary>
    /// <returns>Dictionary of operation IDs and their current status</returns>
    IDictionary<Guid, bool> GetActiveTasksStatus();

    /// <summary>
    /// Harvest a single file directly - download, extract metadata, add to library
    /// </summary>
    /// <param name="printerId">The printer identifier to harvest from</param>
    /// <param name="filename">The filename to harvest</param>
    /// <param name="ct">Cancellation token</param>
    Task<GcodeHarvestResultDto> HarvestSingleFileDirectAsync(Guid printerId, string filename, CancellationToken ct = default);

    /// <summary>
    /// Wait for all active tasks to complete or cancel them after timeout
    /// </summary>
    /// <param name="timeout">Maximum time to wait</param>
    /// <param name="ct">Cancellation token</param>
    Task WaitForAllTasksAsync(TimeSpan timeout, CancellationToken ct = default);
}
