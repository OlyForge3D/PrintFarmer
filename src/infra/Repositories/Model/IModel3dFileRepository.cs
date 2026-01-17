using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Model
{
    /// <summary>
    /// Repository interface for 3D model file persistence and retrieval.
    /// Provides database access for Model3D entities with support for hierarchical file organization.
    /// </summary>
    public interface IModel3DFileRepository
    {
        /// <summary>
        /// Retrieves a single valid model by its unique identifier.
        /// </summary>
        /// <param name="id">The model's unique identifier (GUID)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>The Model3D entity if found and valid, otherwise null</returns>
        Task<Model3D?> GetByIdAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Retrieves a single valid model by its unique identifier, including associated tags.
        /// </summary>
        /// <param name="id">The model's unique identifier (GUID)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>The Model3D entity with tag mappings if found and valid, otherwise null</returns>
        Task<Model3D?> GetByIdWithTagsAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Retrieves a model by its SHA256 file hash (used for deduplication detection).
        /// </summary>
        /// <param name="fileHash">The SHA256 hash of the model file (hexadecimal string)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>The Model3D entity if a matching hash is found, otherwise null</returns>
        Task<Model3D?> GetByHashAsync(string fileHash, CancellationToken ct);

        /// <summary>
        /// Retrieves all valid models in the database.
        /// Valid models are those where IsValid flag is true.
        /// Results are ordered by upload date (newest first).
        /// </summary>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Read-only list of all valid Model3D entities, ordered by UploadedAt descending</returns>
        Task<IReadOnlyList<Model3D>> ListValidAsync(CancellationToken ct);

        /// <summary>
        /// Retrieves all valid models in a specific directory (non-recursive).
        /// This enables hierarchical folder-based browsing of models.
        /// </summary>
        /// <param name="directory">The directory path to query (e.g., "models/mechanical"); empty string for root</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>List of valid Model3D entities in the specified directory only, ordered by UploadedAt descending</returns>
        Task<List<Model3D>> ListValidByDirectoryAsync(string directory, CancellationToken ct);

        /// <summary>
        /// Retrieves all unique subdirectories under a given parent directory.
        /// Returns only direct children (one level down from parent).
        /// This enables hierarchical directory browsing in the UI.
        /// </summary>
        /// <param name="parentDirectory">The parent directory path; empty string for root</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Sorted list of unique subdirectory names that are direct children of parent</returns>
        Task<List<string>> ListSubdirectoriesAsync(string parentDirectory, CancellationToken ct);

        /// <summary>
        /// Counts the total number of valid models in the database.
        /// </summary>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Total count of valid models</returns>
        Task<int> CountValidAsync(CancellationToken ct);

        /// <summary>
        /// Queries models with comprehensive filtering, sorting, and pagination (all at database level).
        /// This is the efficient query method that matches the pattern used in GcodeFilesService.
        /// </summary>
        /// <param name="path">Optional directory path filter (e.g., "/models/mechanical"); null for all directories</param>
        /// <param name="search">Optional search query applied to file name (case-insensitive)</param>
        /// <param name="tagIds">Optional array of tag IDs for filtering (AND logic - model must have all tags)</param>
        /// <param name="sortBy">Sort field: "name", "size", or "date" (default: "name")</param>
        /// <param name="sortOrder">Sort order: "asc" or "desc" (default: "asc")</param>
        /// <param name="page">Page number (1-based)</param>
        /// <param name="pageSize">Number of items per page</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Tuple of (models list, total count) for the query before pagination</returns>
        Task<(List<Model3D> models, int totalCount)> QueryModelsAsync(
            string? path,
            string? search,
            Guid[]? tagIds,
            string? sortBy,
            string? sortOrder,
            int page,
            int pageSize,
            CancellationToken ct);

        /// <summary>
        /// Searches for models using full-text search and/or tag filtering.
        /// Supports pagination and flexible sorting.
        /// </summary>
        /// <param name="query">Optional search query applied to display name and description (case-insensitive)</param>
        /// <param name="tagIds">Optional array of tag IDs for filtering (AND logic - model must have all tags)</param>
        /// <param name="sortBy">Sort field: "name", "size", or "date" (default: "date")</param>
        /// <param name="descending">True for descending sort order, false for ascending</param>
        /// <param name="skip">Number of results to skip for pagination</param>
        /// <param name="take">Maximum number of results to return</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Paginated list of Model3D entities matching criteria, with tag mappings included</returns>
        Task<IReadOnlyList<Model3D>> SearchAsync(string? query, Guid[]? tagIds, string sortBy, bool descending, int skip, int take, CancellationToken ct);

        /// <summary>
        /// Adds a new model entity to the database (does not persist changes immediately).
        /// Call SaveChangesAsync() to commit the transaction.
        /// </summary>
        /// <param name="model">The Model3D entity to add</param>
        /// <param name="ct">Cancellation token for async operation</param>
        Task AddAsync(Model3D model, CancellationToken ct);

        /// <summary>
        /// Removes a model entity from the database (does not persist changes immediately).
        /// Call SaveChangesAsync() to commit the transaction.
        /// </summary>
        /// <param name="model">The Model3D entity to remove</param>
        /// <param name="ct">Cancellation token for async operation</param>
        Task RemoveAsync(Model3D model, CancellationToken ct);

        /// <summary>
        /// Updates an existing model entity in the database (does not persist changes immediately).
        /// Call SaveChangesAsync() to commit the transaction.
        /// </summary>
        /// <param name="model">The Model3D entity with updated values</param>
        /// <param name="ct">Cancellation token for async operation</param>
        Task UpdateAsync(Model3D model, CancellationToken ct);

        /// <summary>
        /// Persists all pending changes (Add, Update, Remove operations) to the database.
        /// This method must be called after Add/Update/Remove to commit transactions.
        /// </summary>
        /// <param name="ct">Cancellation token for async operation</param>
        Task SaveChangesAsync(CancellationToken ct);
    }
}
