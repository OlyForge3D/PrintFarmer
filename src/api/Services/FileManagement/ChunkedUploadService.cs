using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Telemetry;

namespace Farm.Web.Api.Services.FileManagement;

/// <summary>
/// Implementation of chunked upload state management.
/// Maintains in-memory session state with optional persistence for recovery.
/// </summary>
public sealed class ChunkedUploadService : IChunkedUploadService
{
    private const int DefaultRecommendedChunkSize = 1 * 1024 * 1024; // 1 MB

    private readonly ConcurrentDictionary<string, InternalUploadState> _uploadStates = new();
    private readonly IFileManagementService _fileManagementService;
    private readonly IGcodeThumbnailExtractorService _thumbnailExtractor;
    private readonly IUnifiedLoggingService _logger;

    public ChunkedUploadService(
        IFileManagementService fileManagementService,
        IGcodeThumbnailExtractorService thumbnailExtractor,
        IUnifiedLoggingService logger)
    {
        _fileManagementService = fileManagementService ?? throw new ArgumentNullException(nameof(fileManagementService));
        _thumbnailExtractor = thumbnailExtractor ?? throw new ArgumentNullException(nameof(thumbnailExtractor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ChunkedUploadInitResult InitializeUpload(
        string userId,
        string fileName,
        long fileSize,
        string targetDirectory,
        IReadOnlyCollection<string> allowedExtensions,
        string? hashAlgorithm = null,
        string? expectedHash = null)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("userId required", nameof(userId));
        }
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("fileName required", nameof(fileName));
        }
        if (fileSize <= 0)
        {
            throw new ArgumentException("fileSize must be positive", nameof(fileSize));
        }
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ArgumentException("targetDirectory required", nameof(targetDirectory));
        }
        if (allowedExtensions == null || allowedExtensions.Count == 0)
        {
            throw new ArgumentException("allowedExtensions required", nameof(allowedExtensions));
        }

        // Validate extension
        string ext = Path.GetExtension(fileName) ?? string.Empty;
        if (!allowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Invalid file type '{ext}'");
        }

        // Sanitize and resolve unique filename
        string originalName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(originalName))
        {
            originalName = "upload" + ext;
        }

        string safeName = _fileManagementService.SanitizeFileName(originalName, ext);
        string uniqueName = _fileManagementService.ResolveUniqueFileName(targetDirectory, safeName);

        // Create upload session
        string uploadId = Guid.NewGuid().ToString("N");
        string tempFilePath = Path.Combine(targetDirectory, uniqueName + "." + uploadId + ".part");
        string metaFilePath = tempFilePath + ".meta.json";

        // Create empty temp file
        using (File.Create(tempFilePath))
        {
        }

        // Validate hash algorithm if provided
        IncrementalHash? hasher = null;
        string? normalizedHashAlgo = null;
        string? normalizedExpectedHash = null;

        if (!string.IsNullOrWhiteSpace(hashAlgorithm))
        {
            string algo = hashAlgorithm.Trim().ToLowerInvariant();
            if (algo != "sha256" && algo != "sha1")
            {
                throw new ArgumentException("Unsupported hashAlgorithm. Allowed: sha256, sha1");
            }
            normalizedHashAlgo = algo;
            normalizedExpectedHash = string.IsNullOrWhiteSpace(expectedHash) ? null : expectedHash.Trim();
            hasher = algo == "sha1" ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }

        InternalUploadState state = new InternalUploadState
        {
            Id = uploadId,
            UserId = userId,
            TempFilePath = tempFilePath,
            MetaFilePath = metaFilePath,
            TargetDirectoryFullPath = targetDirectory,
            FinalSafeName = uniqueName,
            TotalSize = fileSize,
            UploadedBytes = 0,
            CreatedUtc = DateTime.UtcNow,
            HashAlgorithm = normalizedHashAlgo,
            ExpectedHash = normalizedExpectedHash,
            Hasher = hasher,
            Paused = false
        };

        if (!_uploadStates.TryAdd(uploadId, state))
        {
            throw new InvalidOperationException("Failed to initialize upload session");
        }

        PersistState(state);

        // Build virtual path
        string virtualPath = "/" + uniqueName;

        return new ChunkedUploadInitResult(uploadId, uniqueName, virtualPath, DefaultRecommendedChunkSize);
    }

    public async Task<ChunkedUploadStatus> AppendChunkAsync(
        string uploadId,
        long offset,
        byte[] chunkData,
        string userId,
        IGcodeUploadQuotaService quotaService)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            throw new ArgumentException("uploadId required", nameof(uploadId));
        }

        if (chunkData == null || chunkData.Length == 0)
        {
            throw new ArgumentException("chunkData required", nameof(chunkData));
        }

        ArgumentNullException.ThrowIfNull(quotaService);

        if (!_uploadStates.TryGetValue(uploadId, out InternalUploadState? state))
        {
            throw new InvalidOperationException($"Upload session '{uploadId}' not found");
        }

        // Check if paused
        if (state.Paused)
        {
            throw new InvalidOperationException("Upload session is paused");
        }

        // Validate offset
        if (offset != state.UploadedBytes)
        {
            throw new InvalidOperationException($"Offset mismatch: expected {state.UploadedBytes}, got {offset}");
        }

        // Check remaining size
        long remaining = state.TotalSize - state.UploadedBytes;
        if (chunkData.Length > remaining)
        {
            throw new InvalidOperationException("Chunk exceeds remaining file size");
        }

        // Check quota
        if (!quotaService.TryAddUsage(userId, chunkData.Length, out long used, out long limit))
        {
            throw new InvalidOperationException($"Upload quota exceeded: {used}/{limit} bytes");
        }

        try
        {
            // Append chunk to temp file
            await using (FileStream fs = new(state.TempFilePath, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                await fs.WriteAsync(new ReadOnlyMemory<byte>(chunkData), CancellationToken.None);
            }

            // Update state
            state.UploadedBytes += chunkData.Length;
            state.Hasher?.AppendData(chunkData);

            bool completed = state.UploadedBytes == state.TotalSize;

            if (completed)
            {
                // Finalize upload asynchronously - must complete before returning response
                await FinalizeUploadAsync(state);
                _ = _uploadStates.TryRemove(uploadId, out _);
            }
            else
            {
                PersistState(state);
            }

            return new ChunkedUploadStatus(
                state.Id,
                state.FinalSafeName,
                state.UploadedBytes,
                state.TotalSize,
                completed,
                state.FinalHash,
                state.Paused);
        }
        catch
        {
            // Revert quota on failure
            _ = quotaService.TryAddUsage(userId, -chunkData.Length, out _, out _);
            throw;
        }
    }

    public ChunkedUploadStatus? GetOrResumeUpload(string uploadId)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            return null;
        }

        // Check if in memory
        if (_uploadStates.TryGetValue(uploadId, out InternalUploadState? state))
        {
            return new ChunkedUploadStatus(
                state.Id,
                state.FinalSafeName,
                state.UploadedBytes,
                state.TotalSize,
                state.UploadedBytes == state.TotalSize,
                state.FinalHash,
                state.Paused);
        }

        // Try to rehydrate from metadata file
        try
        {
            // Search for metadata file in temp directories
            // This is a simple implementation - production may need better discovery
            InternalUploadState? rehydrated = RehydrateFromMetadata();
            if (rehydrated != null)
            {
                _uploadStates[uploadId] = rehydrated;
                return new ChunkedUploadStatus(
                    rehydrated.Id,
                    rehydrated.FinalSafeName,
                    rehydrated.UploadedBytes,
                    rehydrated.TotalSize,
                    rehydrated.UploadedBytes == rehydrated.TotalSize,
                    rehydrated.FinalHash,
                    rehydrated.Paused);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"Failed to rehydrate upload session {uploadId}: {ex.Message}");
        }

        return null;
    }

    public ChunkedUploadStatus? PauseUpload(string uploadId)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            return null;
        }

        if (!_uploadStates.TryGetValue(uploadId, out InternalUploadState? state))
        {
            return null;
        }

        if (state.UploadedBytes == state.TotalSize)
        {
            // Already completed - cannot pause
            return new ChunkedUploadStatus(
                state.Id,
                state.FinalSafeName,
                state.UploadedBytes,
                state.TotalSize,
                true,
                state.FinalHash,
                false);
        }

        state.Paused = true;
        PersistState(state);

        return new ChunkedUploadStatus(
            state.Id,
            state.FinalSafeName,
            state.UploadedBytes,
            state.TotalSize,
            false,
            state.FinalHash,
            true);
    }

    public ChunkedUploadStatus? ResumeUpload(string uploadId)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            return null;
        }

        if (!_uploadStates.TryGetValue(uploadId, out InternalUploadState? state))
        {
            return null;
        }

        if (state.UploadedBytes == state.TotalSize)
        {
            // Already completed - cannot resume
            return new ChunkedUploadStatus(
                state.Id,
                state.FinalSafeName,
                state.UploadedBytes,
                state.TotalSize,
                true,
                state.FinalHash,
                false);
        }

        state.Paused = false;
        PersistState(state);

        return new ChunkedUploadStatus(
            state.Id,
            state.FinalSafeName,
            state.UploadedBytes,
            state.TotalSize,
            false,
            state.FinalHash,
            false);
    }

    public void CancelUpload(string uploadId)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            return;
        }

        if (_uploadStates.TryRemove(uploadId, out InternalUploadState? state))
        {
            try
            {
                if (File.Exists(state.TempFilePath))
                {
                    File.Delete(state.TempFilePath);
                }

                if (File.Exists(state.MetaFilePath))
                {
                    File.Delete(state.MetaFilePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Failed to clean up temp files for {uploadId}: {ex.Message}");
            }
        }
    }

    private async Task FinalizeUploadAsync(InternalUploadState state)
    {
        string finalPath = Path.Combine(state.TargetDirectoryFullPath, state.FinalSafeName);

        // Check for collision again (rare but possible)
        if (File.Exists(finalPath))
        {
            string uniqueName = _fileManagementService.ResolveUniqueFileName(
                state.TargetDirectoryFullPath,
                state.FinalSafeName);
            state.FinalSafeName = uniqueName;
            finalPath = Path.Combine(state.TargetDirectoryFullPath, uniqueName);
        }

        // Validate hash if provided
        if (state.Hasher != null)
        {
            byte[] hashBytes = state.Hasher.GetHashAndReset();
            string hex = _fileManagementService.ToHex(hashBytes);
            state.FinalHash = hex;

            if (state.ExpectedHash != null && !hex.Equals(state.ExpectedHash, StringComparison.OrdinalIgnoreCase))
            {
                // Hash mismatch - delete temp file and fail
                try
                {
                    if (File.Exists(state.TempFilePath))
                    {
                        File.Delete(state.TempFilePath);
                    }
                    if (File.Exists(state.MetaFilePath))
                    {
                        File.Delete(state.MetaFilePath);
                    }
                }
                catch { }

                throw new InvalidOperationException($"Hash mismatch: expected {state.ExpectedHash}, got {hex}");
            }
        }

        // Move temp file to final destination asynchronously
        // Use async file operations to properly await completion
        if (File.Exists(state.TempFilePath))
        {
            // Copy asynchronously then delete
            using (var sourceStream = new FileStream(state.TempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
            using (var destStream = new FileStream(finalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await sourceStream.CopyToAsync(destStream);
            }
            // Delete after successful copy
            File.Delete(state.TempFilePath);
        }

        // Extract and save thumbnail if this is a .gcode or .bgcode file
        if (finalPath.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase) || 
            finalPath.EndsWith(".bgcode", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using (var fileStream = new FileStream(finalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    _ = await _thumbnailExtractor.ExtractAndSaveThumbnailAsync(fileStream, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Failed to extract thumbnail for {finalPath}: {ex.Message}");
                // Continue anyway - thumbnail extraction is optional
            }
        }

        // Clean up metadata file
        if (File.Exists(state.MetaFilePath))
        {
            try
            {
                File.Delete(state.MetaFilePath);
            }
            catch { }
        }
    }

    private void PersistState(InternalUploadState state)
    {
        try
        {
            var model = new
            {
                state.Id,
                state.UserId,
                state.TempFilePath,
                state.TargetDirectoryFullPath,
                state.FinalSafeName,
                state.TotalSize,
                state.UploadedBytes,
                state.CreatedUtc,
                state.HashAlgorithm,
                state.ExpectedHash,
                state.FinalHash,
                state.Paused
            };

            string json = JsonSerializer.Serialize(model);
            File.WriteAllText(state.MetaFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"Failed to persist state for {state.Id}: {ex.Message}");
        }
    }

    private InternalUploadState? RehydrateFromMetadata()
    {
        // In a real implementation, you would search for the metadata file
        // For now, return null - can be enhanced to search common temp locations
        return null;
    }

    /// <summary>
    /// Internal state object for tracking an active upload session.
    /// </summary>
    private sealed class InternalUploadState
    {
        public required string Id { get; init; }
        public required string UserId { get; init; }
        public required string TempFilePath { get; init; }
        public required string MetaFilePath { get; init; }
        public required string TargetDirectoryFullPath { get; init; }
        public required string FinalSafeName { get; set; }
        public required long TotalSize { get; init; }
        public long UploadedBytes { get; set; }
        public required DateTime CreatedUtc { get; init; }
        public string? HashAlgorithm { get; init; }
        public string? ExpectedHash { get; init; }
        public string? FinalHash { get; set; }
        public IncrementalHash? Hasher { get; init; }
        public bool Paused { get; set; }
    }
}
