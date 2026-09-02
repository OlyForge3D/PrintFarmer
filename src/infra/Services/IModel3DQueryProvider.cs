namespace Farm.Infrastructure.Services;

/// <summary>
/// Provides lightweight Model3D ID queries for repositories that must
/// not depend on <c>SlicerDbContext</c> directly (e.g. <c>EfTagRepository</c>).
/// When the slicer module is not loaded, the default implementation returns
/// empty/false so tag operations degrade gracefully.
/// </summary>
public interface IModel3DQueryProvider
{
    /// <summary>Checks whether a Model3D with the given id exists.</summary>
    Task<bool> ExistsAsync(Guid id, CancellationToken ct);

    /// <summary>Returns all Model3D identifiers.</summary>
    Task<IReadOnlyList<Guid>> GetAllIdsAsync(CancellationToken ct);

    /// <summary>Returns the latest <c>UpdatedAt</c> across the given ids.</summary>
    /// <returns>The latest <c>UpdatedAt</c> value, or <c>null</c> if none of the ids match any Model3D.</returns>
    Task<DateTime?> GetLatestUpdatedAtAsync(IEnumerable<Guid> ids, CancellationToken ct);

    /// <summary>
    /// Returns the per-model <c>UpdatedAt</c> timestamp for each of the given Model3D ids that
    /// exist, in a single fixed query. Used by <c>EfTagRepository</c> to compute per-tag
    /// last-used timestamps for a batch of tags without issuing one query per tag (issue
    /// #2362) — the repository cannot join <c>Model3DTagMapping</c> directly against
    /// <c>SlicerDbContext</c>'s <c>Model3D</c> table without crossing the module boundary, so
    /// it fetches per-model timestamps here and performs the per-tag grouping in memory.
    /// </summary>
    /// <param name="ids">The Model3D ids to look up.</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>A dictionary of Model3D id to <c>UpdatedAt</c>. Ids with no matching Model3D are omitted.</returns>
    Task<IReadOnlyDictionary<Guid, DateTime>> GetUpdatedAtByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
}
