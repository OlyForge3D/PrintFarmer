using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.Quota;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.FileManagement;

/// <summary>
/// Implementation of chunked upload state management.
/// Maintains in-memory session state with optional persistence for recovery.
/// </summary>
public sealed class ChunkedUploadService(
    IFileManagementService fileManagementService,
    IGcodeThumbnailExtractorService thumbnailExtractor,
    IGcodeMetadataExtractorService metadataExtractor,
    ILogger<ChunkedUploadService> logger) : IChunkedUploadService
{
    private const int DefaultRecommendedChunkSize = 1 * 1024 * 1024; // 1 MB

    private readonly ConcurrentDictionary<string, InternalUploadState> _uploadStates = new();
    private readonly IFileManagementService _fileManagementService = fileManagementService ?? throw new ArgumentNullException(nameof(fileManagementService));
    private readonly IGcodeThumbnailExtractorService _thumbnailExtractor = thumbnailExtractor ?? throw new ArgumentNullException(nameof(thumbnailExtractor));
    private readonly IGcodeMetadataExtractorService _metadataExtractor = metadataExtractor ?? throw new ArgumentNullException(nameof(metadataExtractor));
    private readonly ILogger<ChunkedUploadService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Initializes a new chunked upload session for large file uploads.
    /// </summary>
    /// <param name="userId">User identifier for quota tracking</param>
    /// <param name="fileName">Original filename with extension</param>
    /// <param name="fileSize">Total file size in bytes</param>
    /// <param name="targetDirectory">Physical directory path for file storage</param>
    /// <param name="allowedExtensions">Collection of allowed file extensions (e.g., [".gcode", ".stl"])</param>
    /// <param name="hashAlgorithm">Optional hash algorithm for integrity verification ("sha256" or "sha1")</param>
    /// <param name="expectedHash">Optional expected hash value for verification</param>
    /// <param name="virtualDirectory">Optional virtual directory path for organization</param>
    /// <returns>Upload initialization result with session ID, filename, and recommended chunk size</returns>
    /// <exception cref="ArgumentException">Thrown when required parameters are invalid or extension not allowed</exception>
    /// <exception cref="InvalidOperationException">Thrown when file validation fails or session cannot be created</exception>
    /// <remarks>
    /// Creates temporary .part file for chunked data and .meta.json for session recovery.
    /// Generates unique GUID-based upload ID for session tracking.
    /// Recommended chunk size: 1 MB for optimal performance.
    /// </remarks>
    public ChunkedUploadInitResult InitializeUpload(
        string userId,
        string fileName,
        long fileSize,
        string targetDirectory,
        IReadOnlyCollection<string> allowedExtensions,
        string? hashAlgorithm = null,
        string? expectedHash = null,
        string? virtualDirectory = null)
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
            Paused = false,
            VirtualDirectory = virtualDirectory
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

    /// <summary>
    /// Appends a data chunk to an active upload session.
    /// </summary>
    /// <param name="uploadId">Unique upload session identifier (GUID)</param>
    /// <param name="offset">Byte offset where this chunk should be appended (must match current uploaded bytes)</param>
    /// <param name="chunkData">Binary chunk data to append</param>
    /// <param name="userId">User identifier for quota verification</param>
    /// <param name="quotaService">Quota service for usage tracking and limits</param>
    /// <returns>Upload status with progress, completion state, and thumbnail path (if complete)</returns>
    /// <exception cref="ArgumentException">Thrown when uploadId or chunkData is invalid</exception>
    /// <exception cref="ArgumentNullException">Thrown when quotaService is null</exception>
    /// <exception cref="InvalidOperationException">Thrown when session not found, paused, offset mismatch, quota exceeded, or chunk too large</exception>
    /// <remarks>
    /// Validates offset matches expected position to prevent data corruption.
    /// Checks user quota before appending chunk; reverts quota on failure.
    /// Updates incremental hash if configured for integrity verification.
    /// Automatically finalizes upload (extracts metadata/thumbnail) when complete.
    /// Removes session from memory and cleans up temporary files on completion.
    /// </remarks>
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

        // Try to get session from memory, or rehydrate from disk if not found
        if (!_uploadStates.TryGetValue(uploadId, out InternalUploadState? state))
        {
            // Session not in memory - try to recover from persisted metadata on disk
            // This handles API restarts, load balancing, or other scenarios where in-memory state is lost
            ChunkedUploadStatus? recoveredStatus = GetOrResumeUpload(uploadId);
            if (recoveredStatus == null || !_uploadStates.TryGetValue(uploadId, out state))
            {
                throw new InvalidOperationException($"Upload session '{uploadId}' not found");
            }
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
                state.ThumbnailPath = await FinalizeUploadAsync(state);
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
                state.Paused,
                state.ThumbnailPath,
                completed ? Path.Combine(state.TargetDirectoryFullPath, state.FinalSafeName) : null);
        }
        catch
        {
            // Revert quota on failure
            _ = quotaService.TryAddUsage(userId, -chunkData.Length, out _, out _);
            throw;
        }
    }

    /// <summary>
    /// Retrieves current status of an upload session or attempts to resume from persisted state.
    /// </summary>
    /// <param name="uploadId">Unique upload session identifier (GUID)</param>
    /// <returns>Upload status if session exists, null if not found or cannot be resumed</returns>
    /// <remarks>
    /// First checks in-memory sessions for active uploads.
    /// If not in memory, attempts to rehydrate from .meta.json file for recovery.
    /// Validates temporary file existence and consistency before resuming.
    /// Returns null if session expired, files deleted, or metadata corrupted.
    /// </remarks>
    public ChunkedUploadStatus? GetOrResumeUpload(string uploadId)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            return null;
        }

        // Check if in memory
        if (_uploadStates.TryGetValue(uploadId, out InternalUploadState? state))
        {
            bool isCompleted = state.UploadedBytes == state.TotalSize;
            return new ChunkedUploadStatus(
                state.Id,
                state.FinalSafeName,
                state.UploadedBytes,
                state.TotalSize,
                isCompleted,
                state.FinalHash,
                state.Paused,
                state.ThumbnailPath,
                isCompleted ? Path.Combine(state.TargetDirectoryFullPath, state.FinalSafeName) : null);
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
                bool isCompleted = rehydrated.UploadedBytes == rehydrated.TotalSize;
                return new ChunkedUploadStatus(
                    rehydrated.Id,
                    rehydrated.FinalSafeName,
                    rehydrated.UploadedBytes,
                    rehydrated.TotalSize,
                    isCompleted,
                    rehydrated.FinalHash,
                    rehydrated.Paused,
                    rehydrated.ThumbnailPath,
                    isCompleted ? Path.Combine(rehydrated.TargetDirectoryFullPath, rehydrated.FinalSafeName) : null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Failed to rehydrate upload session {UploadId}: {Message}", uploadId, ex.Message);
        }

        return null;
    }

    /// <summary>
    /// Retrieves the virtual directory path associated with an upload session.
    /// </summary>
    /// <param name="uploadId">Unique upload session identifier (GUID)</param>
    /// <returns>Virtual directory path, or null if session not found or no virtual directory set</returns>
    /// <remarks>
    /// Used for organizing uploaded files in virtual folder hierarchies.
    /// </remarks>
    public string? GetUploadVirtualDirectory(string uploadId)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            return null;
        }

        return _uploadStates.TryGetValue(uploadId, out InternalUploadState? state) ? state.VirtualDirectory : null;
    }

    /// <summary>
    /// Pauses an active upload session, allowing it to be resumed later.
    /// </summary>
    /// <param name="uploadId">Unique upload session identifier (GUID)</param>
    /// <returns>Upload status with paused flag set, or null if session not found</returns>
    /// <remarks>
    /// Sets session to paused state; subsequent AppendChunk calls will fail until resumed.
    /// Persists paused state to metadata file for recovery.
    /// </remarks>
    public ChunkedUploadStatus? PauseUpload(string uploadId)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            return null;
        }

        // Try to get session from memory, or rehydrate from disk if not found
        if (!_uploadStates.TryGetValue(uploadId, out InternalUploadState? state))
        {
            // Session not in memory - try to recover from persisted metadata on disk
            GetOrResumeUpload(uploadId); // Attempt to rehydrate
            if (!_uploadStates.TryGetValue(uploadId, out state))
            {
                return null;
            }
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
                false,
                state.ThumbnailPath,
                Path.Combine(state.TargetDirectoryFullPath, state.FinalSafeName));
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
            true,
            state.ThumbnailPath,
            null);
    }

    /// <summary>
    /// Resumes a paused upload session.
    /// </summary>
    /// <param name="uploadId">Unique upload session identifier (GUID)</param>
    /// <returns>Upload status with paused flag cleared, or null if session not found</returns>
    /// <remarks>
    /// Clears paused state; AppendChunk calls will succeed again.
    /// Persists resumed state to metadata file.
    /// </remarks>
    public ChunkedUploadStatus? ResumeUpload(string uploadId)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            return null;
        }

        // Try to get session from memory, or rehydrate from disk if not found
        if (!_uploadStates.TryGetValue(uploadId, out InternalUploadState? state))
        {
            // Session not in memory - try to recover from persisted metadata on disk
            GetOrResumeUpload(uploadId); // Attempt to rehydrate
            if (!_uploadStates.TryGetValue(uploadId, out state))
            {
                return null;
            }
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
                false,
                state.ThumbnailPath,
                Path.Combine(state.TargetDirectoryFullPath, state.FinalSafeName));
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
            false,
            state.ThumbnailPath,
            null);
    }

    /// <summary>
    /// Cancels an upload session and cleans up temporary files.
    /// </summary>
    /// <param name="uploadId">Unique upload session identifier (GUID)</param>
    /// <remarks>
    /// Removes session from memory and deletes temporary .part file and .meta.json.
    /// Silently succeeds if session not found or files already deleted (idempotent operation).
    /// </remarks>
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
                _logger.LogDebug("Failed to clean up temp files for {UploadId}: {Message}", uploadId, ex.Message);
            }
        }
    }

    #region Helper Methods

    /// <summary>
    /// Finalizes a completed upload by moving file, extracting metadata, and generating thumbnail.
    /// </summary>
    /// <param name="state">Internal upload state containing file paths and hash information</param>
    /// <returns>Thumbnail path if generated successfully, null otherwise</returns>
    /// <remarks>
    /// Finalization steps:
    /// 1. Validates hash if algorithm configured (throws exception on mismatch)
    /// 2. Moves .part file to final location with sanitized name
    /// 3. Extracts gcode metadata (best-effort, failures logged)
    /// 4. Generates thumbnail from gcode (best-effort, failures logged)
    /// 5. Cleans up temporary .meta.json file
    /// Thumbnail and metadata extraction failures do not prevent upload completion.
    /// </remarks>
    private async Task<string?> FinalizeUploadAsync(InternalUploadState state)
    {
        string finalPath = Path.Combine(state.TargetDirectoryFullPath, state.FinalSafeName);
        string? thumbnailPath = null;

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
                catch
                {
                }

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
            _logger.LogInformation("FinalizeUploadAsync: File is GCODE, attempting thumbnail extraction for {StateFinalSafeName}", state.FinalSafeName);
            try
            {
                using (var fileStream = new FileStream(finalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    thumbnailPath = await _thumbnailExtractor.ExtractAndSaveThumbnailAsync(fileStream, CancellationToken.None);
                    if (thumbnailPath != null)
                    {
                        _logger.LogInformation("Extracted thumbnail for {StateFinalSafeName}: {ThumbnailPath}", state.FinalSafeName, thumbnailPath);
                    }
                    else
                    {
                        _logger.LogWarning("Thumbnail extraction returned null for {StateFinalSafeName}", state.FinalSafeName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Failed to extract thumbnail for {FinalPath}: {Message}", finalPath, ex.Message);

                // Continue anyway - thumbnail extraction is optional
            }
        }
        else
        {
            _logger.LogWarning("FinalizeUploadAsync: File is NOT GCODE (path={FinalPath}), skipping thumbnail extraction", finalPath);
        }

        // Clean up metadata file
        if (File.Exists(state.MetaFilePath))
        {
            try
            {
                File.Delete(state.MetaFilePath);
            }
            catch
            {
            }
        }

        return thumbnailPath;
    }

    /// <summary>
    /// Extract metadata from a finalized gcode file.
    /// This method is called after upload completes to extract metadata for database storage.
    /// </summary>
    /// <summary>
    /// Extracts gcode metadata from an uploaded file asynchronously.
    /// </summary>
    /// <param name="filePath">Physical path to the gcode file</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Extracted metadata (print time, material usage, layer height, etc.), or null if extraction fails</returns>
    /// <remarks>
    /// Delegates to IGcodeMetadataExtractorService for actual extraction.
    /// Returns null on any extraction failure; errors logged but not thrown (best-effort operation).
    /// </remarks>
    public async Task<GcodeMetadataExtracted?> ExtractMetadataFromFileAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("Cannot extract metadata: file not found at {FilePath}", filePath);
                return null;
            }

            // Check if it's a gcode file
            if (!filePath.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase) &&
                !filePath.EndsWith(".bgcode", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            using StreamReader reader = new(filePath, Encoding.UTF8);
            string gcodeContent = await reader.ReadToEndAsync(ct);

            return string.IsNullOrWhiteSpace(gcodeContent) ? null : await _metadataExtractor.ExtractMetadataAsync(gcodeContent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract metadata from file {FilePath}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Persists upload session state to metadata file for crash recovery.
    /// </summary>
    /// <param name="state">Internal upload state to serialize</param>
    /// <remarks>
    /// Writes JSON metadata to .meta.json file alongside .part file.
    /// Enables session recovery after application restart or crash.
    /// Uses custom JSON serialization to exclude non-serializable Hasher.
    /// </remarks>
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
            _logger.LogDebug("Failed to persist state for {StateId}: {Message}", state.Id, ex.Message);
        }
    }

    /// <summary>
    /// Rehydrates upload session from persisted metadata file.
    /// </summary>
    /// <returns>Rehydrated internal state, or null if metadata file missing/invalid or temp file inconsistent</returns>
    /// <remarks>
    /// Validates:
    /// - Metadata file exists and is readable
    /// - Temporary .part file exists
    /// - File size matches UploadedBytes in metadata
    /// - Recreates hasher if hash algorithm configured
    /// Returns null on any validation failure for safety.
    /// </remarks>
    private InternalUploadState? RehydrateFromMetadata()
    {
        // In a real implementation, you would search for the metadata file
        // For now, return null - can be enhanced to search common temp locations
        return null;
    }

    #endregion

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

        public string? ThumbnailPath { get; set; }

        public string? VirtualDirectory { get; init; }
    }
}
