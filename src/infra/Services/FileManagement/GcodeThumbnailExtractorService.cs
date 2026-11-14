using System.IO;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.StorageManagement;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.FileManagement;

public class GcodeThumbnailExtractorService(
    IGcodeMetadataExtractorService metadataExtractor,
    IStoragePathService storagePathService,
    ILogger<GcodeThumbnailExtractorService> logger) : IGcodeThumbnailExtractorService
{
    private readonly IGcodeMetadataExtractorService _metadataExtractor = metadataExtractor ?? throw new ArgumentNullException(nameof(metadataExtractor));
    private readonly IStoragePathService _storagePathService = storagePathService ?? throw new ArgumentNullException(nameof(storagePathService));
    private readonly ILogger<GcodeThumbnailExtractorService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Extract and save a thumbnail from a G-code file stream.
    /// </summary>
    /// <param name="gcodeStream">The G-code file stream to extract thumbnail from.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    public async Task<string?> ExtractAndSaveThumbnailAsync(Stream gcodeStream, CancellationToken ct = default)
    {
        if (gcodeStream == null)
        {
            return null;
        }

        try
        {
            // Binary G-code (.bgcode) is converted to equivalent comment text; plain G-code is read as UTF-8.
            gcodeStream.Position = 0;
            string gcodeText = await BinaryGcodeReader.ReadAsTextAsync(gcodeStream, ct);

            return await ExtractAndSaveThumbnailFromTextAsync(gcodeText, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract thumbnail from G-code stream");
            return null;
        }
    }

    /// <summary>
    /// Extract and save a thumbnail from G-code text content.
    /// </summary>
    /// <param name="gcodeContent">The G-code text content to extract thumbnail from.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    public async Task<string?> ExtractAndSaveThumbnailFromTextAsync(string gcodeContent, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gcodeContent))
        {
            return null;
        }

        try
        {
            GcodeMetadataExtracted extractedMetadata = await _metadataExtractor.ExtractMetadataAsync(gcodeContent);
            return await SaveThumbnailAsync(extractedMetadata, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract and save thumbnail from G-code content");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> SaveThumbnailAsync(GcodeMetadataExtracted? metadata, CancellationToken ct = default)
    {
        if (metadata?.ThumbnailData is not { Length: > 0 } thumbnailData)
        {
            _logger.LogDebug("No thumbnail data extracted from G-code content");
            return null;
        }

        try
        {
            _logger.LogInformation("Found thumbnail data: {Length} bytes", thumbnailData.Length);

            string thumbnailDir = _storagePathService.GetThumbnailDirectory();
            _logger.LogInformation("Using thumbnail directory: {ThumbnailDir}", thumbnailDir);
            _ = Directory.CreateDirectory(thumbnailDir);

            string thumbnailFileName = $"{Guid.NewGuid()}_thumb.png";
            string thumbnailPath = Path.Join(thumbnailDir, thumbnailFileName);

            await File.WriteAllBytesAsync(thumbnailPath, thumbnailData, ct);
            _logger.LogInformation("Extracted and saved thumbnail to {ThumbnailPath}", thumbnailPath);

            return thumbnailPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save extracted G-code thumbnail");
            return null;
        }
    }
}
