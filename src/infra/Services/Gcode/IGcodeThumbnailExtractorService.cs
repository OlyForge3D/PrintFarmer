namespace Farm.Infrastructure.Services.Gcode;

/// <summary>
/// Service for extracting and saving thumbnails from G-code files.
/// </summary>
public interface IGcodeThumbnailExtractorService
{
    /// <summary>
    /// Extract and save a thumbnail from a G-code file stream.
    /// Returns the path to the saved thumbnail, or null if no thumbnail was found/extracted.
    /// </summary>
    Task<string?> ExtractAndSaveThumbnailAsync(Stream gcodeStream, CancellationToken ct = default);

    /// <summary>
    /// Extract and save a thumbnail from G-code text content.
    /// Returns the path to the saved thumbnail, or null if no thumbnail was found/extracted.
    /// </summary>
    Task<string?> ExtractAndSaveThumbnailFromTextAsync(string gcodeContent, CancellationToken ct = default);
}
