using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Thumbnails;

/// <summary>
/// Service for generating thumbnails from 3D model files
/// </summary>
public interface IThumbnailGenerationService
{
    /// <summary>
    /// Generates a thumbnail image for a 3D model file
    /// </summary>
    /// <param name="modelFilePath">Path to the 3D model file</param>
    /// <param name="fileFormat">Format of the 3D model file</param>
    /// <param name="outputPath">Path where the thumbnail should be saved</param>
    /// <param name="width">Desired thumbnail width in pixels (default: 512)</param>
    /// <param name="height">Desired thumbnail height in pixels (default: 512)</param>
    /// <param name="zoomPercent">Zoom percentage (e.g., 40, 100, 200); null uses preset default</param>
    /// <param name="view">Camera view name (front, back, left, right, top, bottom); null uses preset default</param>
    /// <param name="viewMode">Camera view mode (isometric or straight); null uses preset default</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if thumbnail was generated successfully, false otherwise</returns>
    Task<bool> GenerateThumbnailAsync(
        string modelFilePath,
        ModelFileFormat fileFormat,
        string outputPath,
        int width = 512,
        int height = 512,
        int? zoomPercent = null,
        string? view = null,
        string? viewMode = null,
        CancellationToken ct = default);

    /// <summary>
    /// Checks if thumbnail generation is supported for the given file format
    /// </summary>
    /// <param name="fileFormat">The 3D model file format</param>
    /// <returns>True if thumbnail generation is supported, false otherwise</returns>
    bool IsFormatSupported(ModelFileFormat fileFormat);

    /// <summary>
    /// Gets the recommended file extension for generated thumbnails
    /// </summary>
    string ThumbnailFileExtension { get; }
}
