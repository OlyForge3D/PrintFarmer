using Farm.Slicer.Module.Dtos;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Queries the 3D-model and G-code stores as one globally ordered file library.
/// </summary>
public interface IUnifiedFilesQueryService
{
    /// <summary>
    /// Returns one authoritative page from the globally filtered and sorted file library.
    /// </summary>
    /// <param name="request">The query parameters.</param>
    /// <param name="ct">Cancellation token for all database work.</param>
    /// <returns>The requested global page and its authoritative totals.</returns>
    Task<UnifiedFilesQueryResponse> QueryAsync(
        UnifiedFilesQueryRequestDto request,
        CancellationToken ct);
}
