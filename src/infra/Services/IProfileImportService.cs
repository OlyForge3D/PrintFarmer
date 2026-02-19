namespace Farm.Infrastructure.Services;

/// <summary>
/// Abstracts slicer profile import operations so that the main API
/// (Farm.Web.Api) does not need a direct dependency on Farm.Slicer.Module.
/// </summary>
public interface IProfileImportService
{
    /// <summary>
    /// Imports slicer profiles for the specified printer model from the
    /// connected slicer worker.
    /// </summary>
    /// <param name="modelId">The printer model identifier.</param>
    /// <param name="modelName">The printer model name (used to query the slicer worker).</param>
    /// <param name="manufacturerName">The manufacturer name (used to query the slicer worker).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of profiles imported.</returns>
    Task<int> ImportProfilesForModelAsync(Guid modelId, string modelName, string manufacturerName, CancellationToken ct);
}
