using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.AutoTagging;

/// <summary>
/// Service for automatically tagging print jobs with material, color, and nozzle information.
/// </summary>
public interface IAutoTagService
{
    /// <summary>
    /// Generates and applies auto-tags to a completed print job based on its metadata.
    /// Tags are additive — existing manual tags are never removed.
    /// </summary>
    Task GenerateTagsAsync(PrintJob job, Guid printerId, CancellationToken ct = default);
}
