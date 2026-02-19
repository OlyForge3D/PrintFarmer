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
}
