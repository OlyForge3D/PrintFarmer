using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Harvest;

/// <summary>
/// Repository for managing G-code harvest operations and discovered files.
/// Provides persistence for harvest workflows including discovery, selection, and import.
/// </summary>
public interface IHarvestRepository
{
    // GcodeHarvestOperation operations

    /// <summary>Gets a harvest operation by ID (no tracking).</summary>
    /// <param name="operationId">The operation identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<GcodeHarvestOperation?> GetOperationByIdAsync(Guid operationId, CancellationToken ct = default);

    /// <summary>Gets a harvest operation by ID with change tracking enabled.</summary>
    /// <param name="operationId">The operation identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<GcodeHarvestOperation?> GetOperationByIdTrackedAsync(Guid operationId, CancellationToken ct = default);

    /// <summary>Gets a harvest operation with its associated printer loaded.</summary>
    /// <param name="operationId">The operation identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<GcodeHarvestOperation?> GetOperationWithPrinterAsync(Guid operationId, CancellationToken ct = default);

    /// <summary>Gets the active (running) harvest operation for a printer.</summary>
    /// <param name="printerId">The printer identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<GcodeHarvestOperation?> GetActiveOperationForPrinterAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>Gets harvest operations with optional filtering and pagination.</summary>
    /// <param name="printerId">Optional printer ID to filter by.</param>
    /// <param name="status">Optional status to filter by.</param>
    /// <param name="limit">Maximum number of operations to return.</param>
    /// <param name="offset">Number of operations to skip.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<GcodeHarvestOperation>> GetOperationsAsync(Guid? printerId, GcodeHarvestStatus? status, int limit, int offset, CancellationToken ct = default);

    /// <summary>Gets recent harvest operations for a printer.</summary>
    /// <param name="printerId">The printer identifier.</param>
    /// <param name="count">Maximum number of operations to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<GcodeHarvestOperation>> GetRecentOperationsForPrinterAsync(Guid printerId, int count, CancellationToken ct = default);

    /// <summary>Gets all currently active (running) harvest operations.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<List<GcodeHarvestOperation>> GetActiveOperationsAsync(CancellationToken ct = default);

    /// <summary>Gets running operations that have discovered files.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<List<GcodeHarvestOperation>> GetRunningOperationsWithFilesFoundAsync(CancellationToken ct = default);

    /// <summary>Adds a new harvest operation.</summary>
    /// <param name="operation">The operation to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddOperationAsync(GcodeHarvestOperation operation, CancellationToken ct = default);

    /// <summary>Updates an existing harvest operation.</summary>
    /// <param name="operation">The operation to update.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateOperationAsync(GcodeHarvestOperation operation, CancellationToken ct = default);

    // HarvestDiscoveredFile operations

    /// <summary>Gets a discovered file by ID within an operation.</summary>
    /// <param name="fileId">The file identifier.</param>
    /// <param name="operationId">The operation identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<HarvestDiscoveredFile?> GetDiscoveredFileByIdAsync(Guid fileId, Guid operationId, CancellationToken ct = default);

    /// <summary>Gets all discovered files for an operation.</summary>
    /// <param name="operationId">The operation identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<HarvestDiscoveredFile>> GetDiscoveredFilesAsync(Guid operationId, CancellationToken ct = default);

    /// <summary>Gets discovered files by a list of IDs.</summary>
    /// <param name="fileIds">List of file identifiers.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<HarvestDiscoveredFile[]> GetDiscoveredFilesByIdsAsync(List<Guid> fileIds, CancellationToken ct = default);

    /// <summary>Gets the count of discovered files for an operation.</summary>
    /// <param name="operationId">The operation identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<int> GetDiscoveredFilesCountAsync(Guid operationId, CancellationToken ct = default);

    /// <summary>Gets the count of discovered files matching a search query.</summary>
    /// <param name="operationId">The operation identifier.</param>
    /// <param name="search">Search query to filter files.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<int> GetDiscoveredFilesCountWithSearchAsync(Guid operationId, string search, CancellationToken ct = default);

    /// <summary>Gets discovered files with pagination and optional search.</summary>
    /// <param name="operationId">The operation identifier.</param>
    /// <param name="page">Page number (1-based).</param>
    /// <param name="pageSize">Number of items per page.</param>
    /// <param name="search">Optional search query.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<HarvestDiscoveredFile>> GetDiscoveredFilesPagedAsync(Guid operationId, int page, int pageSize, string? search, CancellationToken ct = default);

    /// <summary>Checks if a discovered file exists by filename in an operation.</summary>
    /// <param name="operationId">The operation identifier.</param>
    /// <param name="fileName">The filename to check.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> DiscoveredFileExistsByNameAsync(Guid operationId, string fileName, CancellationToken ct = default);

    /// <summary>Gets a discovered file by operation and filename.</summary>
    /// <param name="operationId">The operation identifier.</param>
    /// <param name="fileName">The filename to find.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<HarvestDiscoveredFile?> GetDiscoveredFileByOperationAndFileNameAsync(Guid operationId, string fileName, CancellationToken ct = default);

    /// <summary>Adds a discovered file.</summary>
    /// <param name="file">The file to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddDiscoveredFileAsync(HarvestDiscoveredFile file, CancellationToken ct = default);

    /// <summary>Updates a discovered file.</summary>
    /// <param name="file">The file to update.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateDiscoveredFileAsync(HarvestDiscoveredFile file, CancellationToken ct = default);

    /// <summary>Deletes a discovered file.</summary>
    /// <param name="file">The file to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteDiscoveredFileAsync(HarvestDiscoveredFile file, CancellationToken ct = default);

    /// <summary>Deletes all discovered files for an operation.</summary>
    /// <param name="operationId">The operation identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteDiscoveredFilesByOperationAsync(Guid operationId, CancellationToken ct = default);

    // Harvest file mapping operations

    /// <summary>Creates a mapping between a discovered file and an imported G-code file.</summary>
    /// <param name="discoveredFile">The discovered file.</param>
    /// <param name="gcodeFile">The imported G-code file.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CreateFileImportMappingAsync(HarvestDiscoveredFile discoveredFile, GcodeFile gcodeFile, CancellationToken ct = default);

    /// <summary>
    /// Deletes any file import mappings that reference the specified G-code file.
    /// This allows library file deletion without leaving FK references.
    /// </summary>
    /// <param name="gcodeFileId">The G-code file ID</param>
    /// <param name="ct">Cancellation token</param>
    Task DeleteFileImportMappingsForGcodeFileAsync(Guid gcodeFileId, CancellationToken ct = default);

    // Combined operations

    /// <summary>Saves pending changes to the database.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task SaveChangesAsync(CancellationToken ct = default);
}
