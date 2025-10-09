using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Services.Interfaces;

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
    /// <param name="width">Desired thumbnail width in pixels (default: 256)</param>
    /// <param name="height">Desired thumbnail height in pixels (default: 256)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if thumbnail was generated successfully, false otherwise</returns>
    Task<bool> GenerateThumbnailAsync(
        string modelFilePath,
        ModelFileFormat fileFormat,
        string outputPath,
        int width = 256,
        int height = 256,
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
