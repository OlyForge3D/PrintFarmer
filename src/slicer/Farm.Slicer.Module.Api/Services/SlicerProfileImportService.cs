using Farm.Infrastructure.Services;
using Farm.Slicer.Module.Services;

namespace Farm.Slicer.Module.Api.Services;

/// <summary>
/// Implements <see cref="IProfileImportService"/> by delegating to
/// <see cref="ISlicersService"/>, bridging the infrastructure abstraction
/// to the slicer module's concrete service.
/// </summary>
public class SlicerProfileImportService(ISlicersService slicersService) : IProfileImportService
{
    /// <inheritdoc/>
    public Task<int> ImportProfilesForModelAsync(Guid modelId, string modelName, string manufacturerName, CancellationToken ct)
        => slicersService.ImportProfilesForModelAsync(modelId, modelName, manufacturerName, ct);
}
