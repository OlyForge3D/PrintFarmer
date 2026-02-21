using Farm.Slicer.Module.Domain;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// Repository for <see cref="Model3D"/> file persistence and retrieval.
/// Provides database access with support for filtering by folder, search, and pagination.
/// </summary>
/// <remarks>
/// Unlike the core-domain equivalent, this repository does not have navigation properties
/// to <c>FolderNode</c> or <c>Model3DTag</c>. Folder-based filtering uses the
/// <see cref="StoredFileBase.FolderId"/> soft reference. Tag filtering is handled at the
/// service layer via cross-context queries.
/// </remarks>
public interface IModel3DFileRepository
{
    /// <summary>Retrieves a valid model by its unique identifier.</summary>
    /// <param name="id">The model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Model3D?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Retrieves a model by its SHA-256 file hash (deduplication).</summary>
    /// <param name="fileHash">The SHA-256 hash of the model file.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Model3D?> GetByHashAsync(string fileHash, CancellationToken ct);

    /// <summary>Retrieves all valid models ordered by upload date descending.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Model3D>> ListValidAsync(CancellationToken ct);

    /// <summary>Retrieves all valid models in a specific folder.</summary>
    /// <param name="folderId">The folder identifier (soft reference to core FolderNode).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<Model3D>> ListValidByFolderAsync(Guid folderId, CancellationToken ct);

    /// <summary>Counts the total number of valid models.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<int> CountValidAsync(CancellationToken ct);

    /// <summary>
    /// Queries models with comprehensive filtering, sorting, and pagination.
    /// Path and tag filtering require external resolution at the service layer.
    /// </summary>
    /// <param name="folderIds">Optional set of folder identifiers to restrict results to.</param>
    /// <param name="search">Optional search query applied to file name.</param>
    /// <param name="sortBy">Sort field: "name", "size", or "date".</param>
    /// <param name="sortOrder">Sort direction: "asc" or "desc".</param>
    /// <param name="page">Page number (1-based).</param>
    /// <param name="pageSize">Number of items per page.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of (models list, total count before pagination).</returns>
    Task<(List<Model3D> Models, int TotalCount)> QueryModelsAsync(
        Guid[]? folderIds,
        string? search,
        string? sortBy,
        string? sortOrder,
        int page,
        int pageSize,
        CancellationToken ct);

    /// <summary>
    /// Searches for models by display name or description with pagination and sorting.
    /// </summary>
    /// <param name="query">Optional text search (case-insensitive).</param>
    /// <param name="sortBy">Sort field: "name", "size", or "date".</param>
    /// <param name="descending">True for descending sort order.</param>
    /// <param name="skip">Number of results to skip.</param>
    /// <param name="take">Maximum number of results to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Model3D>> SearchAsync(string? query, string sortBy, bool descending, int skip, int take, CancellationToken ct);

    /// <summary>Adds a new model entity (call <see cref="SaveChangesAsync"/> to persist).</summary>
    /// <param name="model">The model to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddAsync(Model3D model, CancellationToken ct);

    /// <summary>Removes a model entity (call <see cref="SaveChangesAsync"/> to persist).</summary>
    /// <param name="model">The model to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveAsync(Model3D model, CancellationToken ct);

    /// <summary>Updates a model entity (call <see cref="SaveChangesAsync"/> to persist).</summary>
    /// <param name="model">The model to update.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateAsync(Model3D model, CancellationToken ct);

    /// <summary>Persists all pending changes to the database.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task SaveChangesAsync(CancellationToken ct);
}
