using System.Security.Cryptography;

namespace Farm.Web.Api.Services.FileManagement;

/// <summary>
/// Manages chunked file upload state and lifecycle.
/// Handles initialization, chunk appending, pause/resume, completion, and session recovery.
/// </summary>
public interface IChunkedUploadService
{
    /// <summary>
    /// Initializes a new chunked upload session.
    /// Returns upload session ID and metadata for the client to begin uploading chunks.
    /// </summary>
    /// <param name="userId">User identifier for quota tracking</param>
    /// <param name="fileName">Original filename provided by client</param>
    /// <param name="fileSize">Total file size in bytes</param>
    /// <param name="targetDirectory">Full path to target directory (validated)</param>
    /// <param name="allowedExtensions">Collection of allowed file extensions</param>
    /// <param name="hashAlgorithm">Optional hash algorithm ("sha256" or "sha1") for integrity checking</param>
    /// <param name="expectedHash">Optional expected hash value for validation on completion</param>
    /// <returns>Upload session metadata containing ID, safe filename, and upload parameters</returns>
    ChunkedUploadInitResult InitializeUpload(
        string userId,
        string fileName,
        long fileSize,
        string targetDirectory,
        IReadOnlyCollection<string> allowedExtensions,
        string? hashAlgorithm = null,
        string? expectedHash = null);

    /// <summary>
    /// Appends a chunk of data to an active upload session.
    /// Validates offset alignment and quota before appending.
    /// Finalizes and moves temp file to destination if all data received.
    /// </summary>
    /// <param name="uploadId">Upload session ID from initialization</param>
    /// <param name="offset">Expected byte offset (must match current uploaded bytes)</param>
    /// <param name="chunkData">Byte data for this chunk</param>
    /// <param name="userId">User ID for quota enforcement</param>
    /// <param name="quotaService">Service for checking/enforcing upload quotas</param>
    /// <returns>Updated session status including uploaded bytes and completion flag</returns>
    Task<ChunkedUploadStatus> AppendChunkAsync(
        string uploadId,
        long offset,
        byte[] chunkData,
        string userId,
        IGcodeUploadQuotaService quotaService);

    /// <summary>
    /// Retrieves current status of an upload session.
    /// If session not in memory but metadata file exists (service restart scenario),
    /// rehydrates state to enable resume capability.
    /// </summary>
    /// <param name="uploadId">Upload session ID</param>
    /// <returns>Current session status, or null if not found</returns>
    ChunkedUploadStatus? GetOrResumeUpload(string uploadId);

    /// <summary>
    /// Pauses an active upload session.
    /// Prevents further chunk uploads until explicitly resumed.
    /// </summary>
    /// <param name="uploadId">Upload session ID</param>
    /// <returns>Updated session status with paused=true, or null if not found</returns>
    ChunkedUploadStatus? PauseUpload(string uploadId);

    /// <summary>
    /// Resumes a paused upload session.
    /// Allows further chunk uploads to continue.
    /// </summary>
    /// <param name="uploadId">Upload session ID</param>
    /// <returns>Updated session status with paused=false, or null if not found</returns>
    ChunkedUploadStatus? ResumeUpload(string uploadId);

    /// <summary>
    /// Cancels and cleans up an upload session.
    /// Deletes temporary files and removes session state.
    /// </summary>
    /// <param name="uploadId">Upload session ID</param>
    void CancelUpload(string uploadId);
}

/// <summary>
/// Result of initializing a chunked upload session.
/// </summary>
public sealed record ChunkedUploadInitResult(
    string UploadId,
    string SafeFileName,
    string VirtualFilePath,
    int RecommendedChunkSize);

/// <summary>
/// Current status of a chunked upload session.
/// </summary>
public sealed record ChunkedUploadStatus(
    string UploadId,
    string SafeFileName,
    long UploadedBytes,
    long TotalSize,
    bool IsCompleted,
    string? FinalHash,
    bool IsPaused);
