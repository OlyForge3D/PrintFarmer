using Farm.Web.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Services.SlicerServices;

/// <summary>
/// Local file system implementation of slicer file storage
/// </summary>
public class LocalSlicerFileStorage : ISlicerFileStorage
{
    private readonly LocalFileStorageOptions _options;
    private readonly ILogger<LocalSlicerFileStorage> _logger;

    public LocalSlicerFileStorage(IOptions<LocalFileStorageOptions> options, ILogger<LocalSlicerFileStorage> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        // Ensure base directory exists
        if (!Directory.Exists(_options.BasePath))
        {
            Directory.CreateDirectory(_options.BasePath);
        }
    }

    public async Task<string> UploadFileAsync(string key, Stream fileStream, string contentType, CancellationToken cancellationToken = default)
    {
        try
        {
            var filePath = GetFilePath(key);
            var directory = Path.GetDirectoryName(filePath);
            
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var fileWriteStream = File.Create(filePath);
            await fileStream.CopyToAsync(fileWriteStream, cancellationToken);

            _logger.LogDebug("Uploaded file {Key} to {FilePath}", key, filePath);
            
            return GetFileUrl(key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload file {Key}", key);
            throw;
        }
    }

    public async Task<string> UploadFileAsync(string key, byte[] fileData, string contentType, CancellationToken cancellationToken = default)
    {
        try
        {
            var filePath = GetFilePath(key);
            var directory = Path.GetDirectoryName(filePath);
            
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(filePath, fileData, cancellationToken);

            _logger.LogDebug("Uploaded file {Key} to {FilePath}", key, filePath);
            
            return GetFileUrl(key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload file {Key}", key);
            throw;
        }
    }

    public async Task<Stream> DownloadFileAsync(string keyOrUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            var filePath = GetFilePathFromKeyOrUrl(keyOrUrl);
            
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File not found: {keyOrUrl}");
            }

            var fileStream = File.OpenRead(filePath);
            _logger.LogDebug("Downloaded file {KeyOrUrl} from {FilePath}", keyOrUrl, filePath);
            
            return fileStream;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download file {KeyOrUrl}", keyOrUrl);
            throw;
        }
    }

    public async Task<byte[]> DownloadFileBytesAsync(string keyOrUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            var filePath = GetFilePathFromKeyOrUrl(keyOrUrl);
            
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File not found: {keyOrUrl}");
            }

            var fileBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
            _logger.LogDebug("Downloaded file bytes {KeyOrUrl} from {FilePath}", keyOrUrl, filePath);
            
            return fileBytes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download file bytes {KeyOrUrl}", keyOrUrl);
            throw;
        }
    }

    public async Task<bool> FileExistsAsync(string keyOrUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            var filePath = GetFilePathFromKeyOrUrl(keyOrUrl);
            return File.Exists(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check if file exists {KeyOrUrl}", keyOrUrl);
            return false;
        }
    }

    public async Task DeleteFileAsync(string keyOrUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            var filePath = GetFilePathFromKeyOrUrl(keyOrUrl);
            
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogDebug("Deleted file {KeyOrUrl} from {FilePath}", keyOrUrl, filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete file {KeyOrUrl}", keyOrUrl);
            throw;
        }
    }

    public async Task<SlicerFileMetadata?> GetFileMetadataAsync(string keyOrUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            var filePath = GetFilePathFromKeyOrUrl(keyOrUrl);
            
            if (!File.Exists(filePath))
            {
                return null;
            }

            var fileInfo = new System.IO.FileInfo(filePath);
            var key = GetKeyFromFilePath(filePath);
            
            return new SlicerFileMetadata
            {
                Key = key,
                SizeBytes = fileInfo.Length,
                ContentType = GetContentType(fileInfo.Extension),
                CreatedAt = fileInfo.CreationTimeUtc,
                LastModified = fileInfo.LastWriteTimeUtc,
                ETag = GenerateETag(fileInfo),
                CustomMetadata = new Dictionary<string, string>
                {
                    ["FilePath"] = filePath,
                    ["Extension"] = fileInfo.Extension
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get file metadata {KeyOrUrl}", keyOrUrl);
            throw;
        }
    }

    public async Task<string> GenerateSignedUrlAsync(string keyOrUrl, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        // For local file storage, we'll just return the file URL as-is
        // In a real implementation with S3/Azure Blob, this would generate a signed URL
        return GetFileUrlFromKeyOrUrl(keyOrUrl);
    }

    public async Task CleanupTempFilesAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        try
        {
            var cutoffTime = DateTime.UtcNow.Subtract(maxAge);
            var tempDirectory = Path.Combine(_options.BasePath, "temp");
            
            if (!Directory.Exists(tempDirectory))
            {
                return;
            }

            var files = Directory.GetFiles(tempDirectory, "*", SearchOption.AllDirectories);
            var deletedCount = 0;

            foreach (var file in files)
            {
                try
                {
                    var fileInfo = new System.IO.FileInfo(file);
                    if (fileInfo.LastWriteTimeUtc < cutoffTime)
                    {
                        File.Delete(file);
                        deletedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete temp file {File}", file);
                }
            }

            // Also cleanup empty directories
            var directories = Directory.GetDirectories(tempDirectory, "*", SearchOption.AllDirectories);
            foreach (var dir in directories.OrderByDescending(d => d.Length)) // Delete deepest first
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
                    _logger.LogWarning(ex, "Failed to delete empty temp directory {Directory}", dir);
                }
            }

            _logger.LogInformation("Cleaned up {DeletedCount} temp files older than {MaxAge}", deletedCount, maxAge);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup temp files");
            throw;
        }
    }

    private string GetFilePath(string key)
    {
        // Ensure key is safe for file system
        var safeKey = key.Replace("..", "").Replace(":", "_").Replace("?", "_").Replace("&", "_");
        return Path.Combine(_options.BasePath, safeKey);
    }

    private string GetFilePathFromKeyOrUrl(string keyOrUrl)
    {
        // If it looks like a URL, extract the key portion
        if (keyOrUrl.StartsWith("http://") || keyOrUrl.StartsWith("https://"))
        {
            var uri = new Uri(keyOrUrl);
            var key = uri.AbsolutePath.TrimStart('/');
            return GetFilePath(key);
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
            var key = GetKeyFromFilePath(keyOrUrl);
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
        return extension.ToLowerInvariant() switch
        {
            ".stl" => "model/stl",
            ".obj" => "model/obj",
            ".3mf" => "model/3mf",
            ".ply" => "model/ply",
            ".gcode" => "text/plain",
            ".json" => "application/json",
            ".txt" => "text/plain",
            ".log" => "text/plain",
            _ => "application/octet-stream"
        };
    }

    private static string GenerateETag(System.IO.FileInfo fileInfo)
    {
        // Simple ETag based on last write time and size
        var hash = $"{fileInfo.LastWriteTimeUtc.Ticks}-{fileInfo.Length}".GetHashCode();
        return $"\"{hash:X}\"";
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