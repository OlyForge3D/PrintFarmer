using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Gcode;

/// <summary>
/// Repository interface for G-code file persistence and retrieval.
/// Provides database access for GcodeFile entities with support for:
/// - Full-text search and metadata filtering
/// - Hierarchical file organization with directory browsing
/// - Batch operations for efficient querying
/// - Harvest operation correlation
/// </summary>
public interface IGcodeRepository
{
    /// <summary>
    /// Searches the G-code library using metadata filters.
    /// Supports material type, nozzle diameter, and printer model filtering.
    /// </summary>
    /// <param name="search">Optional full-text search term applied to file names and descriptions</param>
    /// <param name="material">Optional filter by required material (e.g., "PLA", "PETG")</param>
    /// <param name="nozzleDiameter">Optional filter by required nozzle diameter in mm (fuzzy match ±0.001mm)</param>
    /// <param name="printerModelId">Optional filter by printer model ID (model used when slicing the file)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>List of GcodeFile entities matching criteria, ordered by upload date (newest first)</returns>
    Task<List<GcodeFile>> QueryLibraryAsync(string? search, string? material, double? nozzleDiameter, Guid? printerModelId, CancellationToken ct);

    /// <summary>
    /// Retrieves a single G-code file by ID with related entities (printer, model) included.
    /// </summary>
    /// <param name="id">The file's unique identifier (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>The GcodeFile entity with related entities if found, otherwise null</returns>
    Task<GcodeFile?> GetByIdWithIncludesAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Finds a G-code file by its SHA256 file hash (used for deduplication detection).
    /// </summary>
    /// <param name="hash">The SHA256 hash of the file (hexadecimal string)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>The GcodeFile entity if a matching hash is found, otherwise null</returns>
    Task<GcodeFile?> FindByHashAsync(string hash, CancellationToken ct);

    /// <summary>
    /// Retrieves a G-code file by its full absolute path on disk.
    /// Used for correlating harvested files from printer storage.
    /// </summary>
    /// <param name="fullPath">The complete file path (e.g., "/home/pi/gcode/model.gcode")</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>The GcodeFile entity if the path exists in the database, otherwise null</returns>
    Task<GcodeFile?> GetByFullPathAsync(string fullPath, CancellationToken ct);

    /// <summary>
    /// Retrieves multiple G-code files by their full paths in a single efficient query.
    /// Preferred over multiple GetByFullPathAsync calls for batch operations.
    /// </summary>
    /// <param name="fullPaths">Collection of complete file paths to query</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>List of GcodeFile entities matching any of the provided paths</returns>
    Task<List<GcodeFile>> GetByFullPathsAsync(IEnumerable<string> fullPaths, CancellationToken ct);

    /// <summary>
    /// Retrieves all G-code files whose paths start with a given directory prefix.
    /// Includes files in subdirectories (recursive).
    /// </summary>
    /// <param name="directoryPrefix">The directory path prefix to match (e.g., "/home/pi/gcode")</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>List of GcodeFile entities in the directory and subdirectories</returns>
    Task<List<GcodeFile>> ListByDirectoryPrefixAsync(string directoryPrefix, CancellationToken ct);

    /// <summary>
    /// Retrieves G-code files with comprehensive database-level filtering, sorting, and pagination.
    /// Handles both "all files" queries (path=null) and directory-specific queries (path provided).
    /// </summary>
    /// <param name="path">Virtual directory path. Null/empty returns all files. Non-null returns files in that directory only.</param>
    /// <param name="search">Optional search term for file names</param>
    /// <param name="tagIds">Optional array of tag IDs for filtering (AND logic - file must have all tags)</param>
    /// <param name="printerModelId">Optional filter by printer model ID</param>
    /// <param name="printerId">Optional filter by source printer ID</param>
    /// <param name="sortBy">Sort field: "name", "size", or "date"</param>
    /// <param name="sortOrder">Sort direction: "asc" or "desc"</param>
    /// <param name="page">Page number (1-based)</param>
    /// <param name="pageSize">Number of items per page</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Tuple of (files for current page, total count of all matching files)</returns>
    Task<(List<GcodeFile> files, int totalCount)> QueryFilesAsync(
        string? path,
        string? search,
        Guid[]? tagIds,
        Guid? printerModelId,
        Guid? printerId,
        string? sortBy,
        string? sortOrder,
        int page,
        int pageSize,
        CancellationToken ct);

    /// <summary>
    /// Retrieves all unique subdirectories under a given parent directory.
    /// Returns only direct children (one level down from parent).
    /// Used for hierarchical UI directory browsing.
    /// </summary>
    /// <param name="parentDirectory">The parent directory path; empty string for root</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Sorted list of unique subdirectory names that are direct children of parent</returns>
    Task<List<string>> ListSubdirectoriesAsync(string parentDirectory, CancellationToken ct);

    /// <summary>
    /// Retrieves all G-code files in a specific directory (non-recursive).
    /// This enables flat directory-level browsing without traversing subdirectories.
    /// </summary>
    /// <param name="directory">The directory path to query (e.g., "gcode/models"); empty string for root</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>List of GcodeFile entities in the specified directory only</returns>
    Task<List<GcodeFile>> ListValidByDirectoryAsync(string directory, CancellationToken ct);

    /// <summary>
    /// Retrieves the latest harvest operation ID associated with a specific printer.
    /// Used to correlate harvested G-code files with their source printer.
    /// </summary>
    /// <param name="printerId">The printer's unique identifier (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>The latest GcodeHarvestOperation ID if one exists, otherwise null</returns>
    Task<Guid?> GetLatestHarvestOperationIdForPrinterAsync(Guid printerId, CancellationToken ct);

    /// <summary>
    /// Retrieves the latest harvest operation IDs for multiple printers in a single efficient query.
    /// Preferred over multiple GetLatestHarvestOperationIdForPrinterAsync calls.
    /// </summary>
    /// <param name="printerIds">Collection of printer IDs to query</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Dictionary mapping printer ID to latest harvest operation ID (null if no operations exist)</returns>
    Task<Dictionary<Guid, Guid?>> GetLatestHarvestOperationIdsByPrintersAsync(IEnumerable<Guid> printerIds, CancellationToken ct);

    /// <summary>
    /// Resolves a printer model name to its database ID.
    /// Attempts exact match first, then strips nozzle sizes (e.g., "Phrozen Arco 0.4" → "Phrozen Arco").
    /// Used for matching gcode metadata extracted model names to database PrinterModel records.
    /// </summary>
    /// <param name="extractedModelName">The printer model name extracted from gcode metadata</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>The PrinterModel ID if found, null otherwise</returns>
    Task<Guid?> ResolvePrinterModelIdAsync(string? extractedModelName, CancellationToken ct);

    /// <summary>
    /// Adds a new G-code file entity to the database (does not persist changes immediately).
    /// Call SaveChangesAsync() to commit the transaction.
    /// </summary>
    /// <param name="file">The GcodeFile entity to add</param>
    /// <param name="ct">Cancellation token for async operation</param>
    Task AddAsync(GcodeFile file, CancellationToken ct);

    /// <summary>
    /// Removes a G-code file entity from the database (does not persist changes immediately).
    /// Call SaveChangesAsync() to commit the transaction.
    /// </summary>
    /// <param name="file">The GcodeFile entity to remove</param>
    /// <param name="ct">Cancellation token for async operation</param>
    Task RemoveAsync(GcodeFile file, CancellationToken ct);

    /// <summary>
    /// Persists all pending changes (Add, Remove operations) to the database.
    /// This method must be called after Add/Remove to commit transactions.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    Task SaveChangesAsync(CancellationToken ct);
}
