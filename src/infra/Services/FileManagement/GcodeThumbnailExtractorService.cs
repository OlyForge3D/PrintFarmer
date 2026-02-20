using System.IO;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Telemetry;

namespace Farm.Infrastructure.Services.FileManagement;

public class GcodeThumbnailExtractorService(
    IGcodeMetadataExtractorService metadataExtractor,
    IStoragePathService storagePathService,
    IUnifiedLoggingService logger) : IGcodeThumbnailExtractorService
{
    private readonly IGcodeMetadataExtractorService _metadataExtractor = metadataExtractor ?? throw new ArgumentNullException(nameof(metadataExtractor));
    private readonly IStoragePathService _storagePathService = storagePathService ?? throw new ArgumentNullException(nameof(storagePathService));
    private readonly IUnifiedLoggingService _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
            // Read the stream content as text
            gcodeStream.Position = 0;
            using StreamReader reader = new(gcodeStream, System.Text.Encoding.UTF8, leaveOpen: true);
            string gcodeText = await reader.ReadToEndAsync(ct);

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
            // Extract metadata which includes thumbnail
            GcodeMetadataExtracted extractedMetadata = await _metadataExtractor.ExtractMetadataAsync(gcodeContent);

            if (extractedMetadata?.ThumbnailData == null || extractedMetadata.ThumbnailData.Length == 0)
            {
                _logger.LogDebug("No thumbnail data extracted from G-code content");
                return null;
            }

            _logger.LogInformation($"Found thumbnail data: {extractedMetadata.ThumbnailData.Length} bytes");

            // Create thumbnails directory if needed (same as GCODE files directory now)
            string thumbnailDir = _storagePathService.GetThumbnailDirectory();
            _logger.LogInformation($"Using thumbnail directory: {thumbnailDir}");
            _ = Directory.CreateDirectory(thumbnailDir);

            // Save thumbnail with temporary GUID name - will be renamed by FinalizeChunkedUploadAsync
            // to match the GCODE file ID with _thumb.png suffix
            string thumbnailFileName = $"{Guid.NewGuid()}_thumb.png";
            string thumbnailPath = Path.Combine(thumbnailDir, thumbnailFileName);

            await File.WriteAllBytesAsync(thumbnailPath, extractedMetadata.ThumbnailData, ct);
            _logger.LogInformation($"Extracted and saved thumbnail to {thumbnailPath}");

            return thumbnailPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract and save thumbnail from G-code content");
            return null;
        }
    }
}
