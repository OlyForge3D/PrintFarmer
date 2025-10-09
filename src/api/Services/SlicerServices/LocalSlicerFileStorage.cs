using System.Text.Json;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Shared;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Services.SlicerServices;

/// <summary>
/// Local file system implementation of slicer file storage
/// </summary>
public class LocalSlicerFileStorage : ISlicerFileStorage
{
    private readonly LocalFileStorageOptions _options;
    private readonly IUnifiedLoggingService _logger;

    public LocalSlicerFileStorage(IOptions<LocalFileStorageOptions> options, IUnifiedLoggingService logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Ensure base directory exists
        if (!Directory.Exists(_options.BasePath))
        {
            _ = Directory.CreateDirectory(_options.BasePath);
        }
    }

    public async Task<string> UploadFileAsync(string key, Stream fileStream, string contentType, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(fileStream);
            ArgumentNullException.ThrowIfNull(contentType);
            string filePath = GetFilePath(key);
            string? directory = Path.GetDirectoryName(filePath);

            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                _ = Directory.CreateDirectory(directory);
            }

            await using FileStream fileWriteStream = File.Create(filePath);
            await fileStream.CopyToAsync(fileWriteStream, cancellationToken);

            // Persist minimal metadata (e.g., content type) alongside the file
            TryWriteSidecarMetadata(filePath, contentType);

            _logger.LogDebug($"Uploaded file {key} to {filePath}");

            return GetFileUrl(key);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to upload file {key}: {ex.Message}");
            throw;
        }
    }

    public async Task<string> UploadFileAsync(string key, byte[] fileData, string contentType, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(fileData);
            ArgumentNullException.ThrowIfNull(contentType);
            string filePath = GetFilePath(key);
            string? directory = Path.GetDirectoryName(filePath);

            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                _ = Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(filePath, fileData, cancellationToken);

            // Persist minimal metadata (e.g., content type) alongside the file
            TryWriteSidecarMetadata(filePath, contentType);

            _logger.LogDebug($"Uploaded file {key} to {filePath}");

            return GetFileUrl(key);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to upload file {key}: {ex.Message}");
            throw;
        }
    }

    public Task<Stream> DownloadFileAsync(string keyOrUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(keyOrUrl);
            string filePath = GetFilePathFromKeyOrUrl(keyOrUrl);

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File not found: {keyOrUrl}");
            }

            Stream fileStream = (Stream)File.OpenRead(filePath);
            _logger.LogDebug($"Downloaded file {keyOrUrl} from {filePath}");
            return Task.FromResult(fileStream);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to download file {keyOrUrl}: {ex.Message}");
            throw;
        }
    }

    public async Task<byte[]> DownloadFileBytesAsync(string keyOrUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(keyOrUrl);
            string filePath = GetFilePathFromKeyOrUrl(keyOrUrl);

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File not found: {keyOrUrl}");
            }

            byte[] fileBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
            _logger.LogDebug($"Downloaded file bytes {keyOrUrl} from {filePath}");
            return fileBytes;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to download file bytes {keyOrUrl}: {ex.Message}");
            throw;
        }
    }

    public Task<bool> FileExistsAsync(string keyOrUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(keyOrUrl);
            string filePath = GetFilePathFromKeyOrUrl(keyOrUrl);
            return Task.FromResult(File.Exists(filePath));
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to check if file exists {keyOrUrl}: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    public Task DeleteFileAsync(string keyOrUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(keyOrUrl);
            string filePath = GetFilePathFromKeyOrUrl(keyOrUrl);

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogDebug($"Deleted file {keyOrUrl} from {filePath}");
            }
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to delete file {keyOrUrl}: {ex.Message}");
            throw;
        }
    }

    public Task<SlicerFileMetadata?> GetFileMetadataAsync(string keyOrUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(keyOrUrl);
            string filePath = GetFilePathFromKeyOrUrl(keyOrUrl);

            if (!File.Exists(filePath))
            {
                return Task.FromResult<SlicerFileMetadata?>(null);
            }

            System.IO.FileInfo fileInfo = new(filePath);
            string key = GetKeyFromFilePath(filePath);
            string? storedContentType = TryReadSidecarContentType(filePath);

            SlicerFileMetadata meta = new()
            {
                Key = key,
                SizeBytes = fileInfo.Length,
                ContentType = storedContentType ?? GetContentType(fileInfo.Extension),
                CreatedAt = fileInfo.CreationTimeUtc,
                LastModified = fileInfo.LastWriteTimeUtc,
                ETag = GenerateETag(fileInfo)
            };
            meta.CustomMetadata["FilePath"] = filePath;
            // Store extension as-is; consumers should compare using OrdinalIgnoreCase when needed
            meta.CustomMetadata["Extension"] = fileInfo.Extension;
            return Task.FromResult<SlicerFileMetadata?>(meta);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to get file metadata {keyOrUrl}: {ex.Message}");
            throw;
        }
    }

    public Task<string> GenerateSignedUrlAsync(string keyOrUrl, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        // For local file storage, we'll just return the file URL as-is
        // In a real implementation with S3/Azure Blob, this would generate a signed URL
        ArgumentNullException.ThrowIfNull(keyOrUrl);
        return Task.FromResult(GetFileUrlFromKeyOrUrl(keyOrUrl));
    }

    public void CleanupTempFiles(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        try
        {
            DateTime cutoffTime = DateTime.UtcNow.Subtract(maxAge);
            string tempDirectory = Path.Combine(_options.BasePath, "temp");

            if (!Directory.Exists(tempDirectory))
            {
                return;
            }

            string[] files = Directory.GetFiles(tempDirectory, "*", SearchOption.AllDirectories);
            int deletedCount = 0;

            foreach (string file in files)
            {
                try
                {
                    System.IO.FileInfo fileInfo = new(file);
                    if (fileInfo.LastWriteTimeUtc < cutoffTime)
                    {
                        File.Delete(file);
                        deletedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to delete temp file {file}: {ex.Message}");
                }
            }

            // Also cleanup empty directories
            string[] directories = Directory.GetDirectories(tempDirectory, "*", SearchOption.AllDirectories);
            foreach (string? dir in directories.OrderByDescending(d => d.Length)) // Delete deepest first
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to delete empty temp directory {dir}: {ex.Message}");
                }
            }

            _logger.LogInformation($"Cleaned up {deletedCount} temp files older than {maxAge}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to cleanup temp files: {ex.Message}");
            throw;
        }
    }

    private string GetFilePath(string key)
    {
        // Ensure key is safe for file system
        string safeKey = key.Replace("..", "").Replace(":", "_").Replace("?", "_").Replace("&", "_");
        return Path.Combine(_options.BasePath, safeKey);
    }

    private string GetFilePathFromKeyOrUrl(string keyOrUrl)
    {
        // If it looks like a URL, extract the key portion
        if (keyOrUrl.StartsWith("http://") || keyOrUrl.StartsWith("https://"))
        {
            Uri uri = new(keyOrUrl);
            string key = uri.AbsolutePath.TrimStart('/');
            return GetFilePath(key);
        }
        if (keyOrUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(keyOrUrl, UriKind.Absolute, out Uri? fileUri))
        {
            // Use local file path from URI
            return fileUri.LocalPath;
        }

        // If it's already a file path within our base directory, use it directly
        if (keyOrUrl.StartsWith(_options.BasePath))
        {
            return keyOrUrl;
        }

        // Otherwise treat as a key
        return GetFilePath(keyOrUrl);
    }

    private string GetFileUrl(string key)
    {
        if (string.IsNullOrEmpty(_options.BaseUrl))
        {
            return $"file://{GetFilePath(key)}";
        }

        return $"{_options.BaseUrl.TrimEnd('/')}/{key}";
    }

    private string GetFileUrlFromKeyOrUrl(string keyOrUrl)
    {
        // If it's already a URL, return as-is
        if (keyOrUrl.StartsWith("http://") || keyOrUrl.StartsWith("https://") || keyOrUrl.StartsWith("file://"))
        {
            return keyOrUrl;
        }

        // If it's a file path, convert to key and then to URL
        if (keyOrUrl.StartsWith(_options.BasePath))
        {
            string key = GetKeyFromFilePath(keyOrUrl);
            return GetFileUrl(key);
        }

        // Otherwise treat as a key
        return GetFileUrl(keyOrUrl);
    }

    private string GetKeyFromFilePath(string filePath)
    {
        return Path.GetRelativePath(_options.BasePath, filePath).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string GetContentType(string extension)
    {
        if (extension.Equals(".stl", StringComparison.OrdinalIgnoreCase))
        {
            return "model/stl";
        }
        if (extension.Equals(".obj", StringComparison.OrdinalIgnoreCase))
        {
            return "model/obj";
        }
        if (extension.Equals(".3mf", StringComparison.OrdinalIgnoreCase))
        {
            return "application/vnd.ms-3mfdocument";
        }
        if (extension.Equals(".ply", StringComparison.OrdinalIgnoreCase))
        {
            return "model/ply";
        }
        if (extension.Equals(".gcode", StringComparison.OrdinalIgnoreCase))
        {
            return "text/plain";
        }
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            return "application/json";
        }
        if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return "text/plain";
        }
        if (extension.Equals(".log", StringComparison.OrdinalIgnoreCase))
        {
            return "text/plain";
        }
        return "application/octet-stream";
    }

    private static string GenerateETag(System.IO.FileInfo fileInfo)
    {
        // Simple ETag based on last write time and size
        int hash = $"{fileInfo.LastWriteTimeUtc.Ticks}-{fileInfo.Length}".GetHashCode();
        return $"\"{hash:X}\"";
    }

    private static string GetSidecarPath(string filePath) => filePath + ".meta.json";

    private void TryWriteSidecarMetadata(string filePath, string contentType)
    {
        try
        {
            string metaPath = GetSidecarPath(filePath);
            var meta = new { ContentType = contentType };
            string json = System.Text.Json.JsonSerializer.Serialize(meta);
            File.WriteAllText(metaPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to write sidecar metadata for {filePath}: {ex.Message}");
        }
    }

    private string? TryReadSidecarContentType(string filePath)
    {
        try
        {
            string metaPath = GetSidecarPath(filePath);
            if (!File.Exists(metaPath))
            {
                return null;
            }
            string json = File.ReadAllText(metaPath);
            JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("ContentType", out JsonElement ctElem) && ctElem.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return ctElem.GetString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to read sidecar metadata for {filePath}: {ex.Message}");
        }
        return null;
    }
}

/// <summary>
/// Configuration options for local file storage
/// </summary>
public class LocalFileStorageOptions
{
    public string BasePath { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "storage");
    public string? BaseUrl { get; set; }
}
