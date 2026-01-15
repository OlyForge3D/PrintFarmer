using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Contracts.FileManagement;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services; // needed for IGcodeUploadSettings
using Farm.Web.Api.Services.FileManagement;
using Farm.Web.Api.Services.Tags;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides a lightweight virtual file browser over the server-side G-code library directory (wwwroot/gcode-library).
/// This controller exists primarily to satisfy the React FileBrowser component which expects:
///   GET    /api/gcode-files                (query: path, sortBy, sortOrder, search, harvestId, printerId)
///   DELETE /api/gcode-files                (body: { filePaths: string[] })
///   GET    /api/gcode-files/download       (query: path)
/// The underlying persistent metadata &amp; deduplicated storage is still handled by GcodeLibraryController / database.
/// We intentionally do NOT expose recursive listing – only the immediate children of the requested path.
/// </summary>
[ApiController]
[Route("api/gcode-files")]
public class GcodeFilesController(
    IUnifiedLoggingService logger,
    IGcodeUploadSettings uploadSettings,
    IGcodeUploadQuotaService quotaService,
    Farm.Web.Api.Services.Gcode.IGcodeFilesService gcodeFilesService,
    Farm.Web.Api.Services.FileManagement.IChunkedUploadService chunkedUploadService,
    Farm.Web.Api.Services.FileManagement.IFileManagementService fileManagementService,
    IStoragePathService storagePathService,
    IStoredFileOperationsService storedFileOperationsService
) : ControllerBase
{
    // Dynamic allowed extensions supplied by runtime settings service.
    private IReadOnlyCollection<string> AllowedExtensions => uploadSettings.AllowedExtensions;

    /// <summary>
    /// Resolves a GCode file path to an absolute path.
    /// </summary>
    private string ResolveGcodePath(string? relativePath)
    {
        string gcodeRoot = storagePathService.GetGcodeStorageDirectory();
        return storedFileOperationsService.ResolveStoragePath(relativePath, gcodeRoot);
    }

    /// <summary>
    /// Computes a hash for an existing G-code file for deduplication/comparison (sha256 default; supports sha1).
    /// </summary>
    [HttpGet("hash")]
    [ProducesResponseType(typeof(GcodeFileHashResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [SuppressMessage("Security", "CA3003", Justification = "Path validated in service via IStoragePathService with root prefix check.")]
    public ActionResult<GcodeFileHashResponse> GetFileHash([FromQuery] string? path, [FromQuery] string? algorithm = "sha256")
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("path is required");
        }
        algorithm = (algorithm ?? "sha256").Trim();
        if (!algorithm.Equals("sha256", StringComparison.OrdinalIgnoreCase) && !algorithm.Equals("sha1", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Unsupported algorithm. Allowed: sha256, sha1");
        }
        try
        {
            // Use existing secure resolver on the parent directory, then combine sanitized filename
            string parentVirtual = Path.GetDirectoryName(path) ?? "/";
            string fileName = Path.GetFileName(path);
            (string _, string? parentFull, string _) = ResolveAndValidatePath(parentVirtual);
            string fullPath = Path.GetFullPath(Path.Combine(parentFull, fileName));
            if (!fullPath.StartsWith(parentFull, StringComparison.Ordinal))
            {
                return NotFound();
            }
            if (!System.IO.File.Exists(fullPath))
            {
                return NotFound();
            }
            // Only allow hashing of gcode/bgcode to avoid arbitrary file disclosure.
            if (!fileName.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase) && !fileName.EndsWith(".bgcode", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest("Only .gcode or .bgcode files supported");
            }
            using FileStream fs = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            IncrementalHash hasher = algorithm == "sha1" ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920];
            int read;
            long total = 0;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
                total += read;
            }
            string hex = fileManagementService.ToHex(hasher.GetHashAndReset());
            return Ok(new GcodeFileHashResponse(fileName, total, algorithm, hex));
        }
        catch (Exception ex)
        {
            logger.LogError($"Failed to hash file {path}: {ex.Message}");
            return Problem("Failed to compute hash", statusCode: 500);
        }
    }

    /// <summary>
    /// Returns a listing of G-code files.
    /// When path is null or empty, returns all files across all directories (paginated).
    /// When path is specified, returns only files in that specific directory (non-recursive).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(GcodeFileListResponse), 200)]
    public async Task<ActionResult<GcodeFileListResponse>> ListAsync(
    [FromQuery] string? path = null,
    [FromQuery] string? sortBy = "name",
    [FromQuery] string? sortOrder = "asc",
    [FromQuery] string? search = null,
    [FromQuery] int page = 1,
    [FromQuery] int pageSize = 100,
    [FromQuery] Guid? harvestId = null,
    [FromQuery] Guid? printerId = null
    )
    {
        try
        {
            GcodeFileListResponse response = await gcodeFilesService.QueryAsync(
                path, sortBy, sortOrder, search, page, pageSize, null, null, printerId, HttpContext.RequestAborted);
            return Ok(response);
        }
        catch (DirectoryNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError($"Error listing G-code files (path={path}): {ex.GetType().Name} - {ex.Message}\n{ex.StackTrace}");
            return Problem($"Failed to retrieve files: {ex.GetType().Name} - {ex.Message}", statusCode: 500);
        }
    }

    /// <summary>
    /// New efficient query endpoint that pushes all filtering, sorting, and pagination to the database.
    /// Supports comprehensive filtering including path, search, printer model, printer, and harvest.
    /// Intended to replace the base GET endpoint once frontend migration is complete.
    /// </summary>
    /// <param name="path">Virtual directory path. Null/empty returns all files. Non-null returns files in that directory only.</param>
    /// <param name="sortBy">Sort field: 'name', 'size', or 'date'.</param>
    /// <param name="sortOrder">Sort order: 'asc' or 'desc'.</param>
    /// <param name="search">Optional search term for file names.</param>
    /// <param name="page">Page number (1-based).</param>
    /// <param name="pageSize">Page size (1-500).</param>
    /// <param name="tagIds">Optional array of tag IDs for filtering (AND logic).</param>
    /// <param name="printerModelId">Optional filter by printer model ID.</param>
    /// <param name="printerId">Optional filter by source printer ID.</param>
    /// <returns>Paginated response containing files with metadata.</returns>
    [HttpGet("query")]
    [ProducesResponseType(typeof(GcodeFileListResponse), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<GcodeFileListResponse>> QueryAsync(
        [FromQuery] string? path = null,
        [FromQuery] string? sortBy = "name",
        [FromQuery] string? sortOrder = "asc",
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        [FromQuery] Guid[]? tagIds = null,
        [FromQuery] Guid? printerModelId = null,
        [FromQuery] Guid? printerId = null
    )
    {
        try
        {
            GcodeFileListResponse response = await gcodeFilesService.QueryAsync(
                path,
                sortBy,
                sortOrder,
                search,
                page,
                pageSize,
                tagIds,
                printerModelId,
                printerId,
                HttpContext.RequestAborted);
            return Ok(response);
        }
        catch (Exception ex)
        {
            logger.LogError($"Error querying G-code files (path={path}): {ex.GetType().Name} - {ex.Message}\n{ex.StackTrace}");
            return Problem($"Failed to query files: {ex.GetType().Name} - {ex.Message}", statusCode: 500);
        }
    }

    /// <summary>
    /// Lists all G-code folders recursively for building a folder tree structure.
    /// </summary>
    [HttpGet("folders")]
    [ProducesResponseType(typeof(List<GcodeFileEntryDto>), 200)]
    public async Task<ActionResult<List<GcodeFileEntryDto>>> ListAllFoldersAsync()
    {
        try
        {
            var folders = await gcodeFilesService.ListAllFoldersAsync(HttpContext.RequestAborted);
            return Ok(folders);
        }
        catch (Exception ex)
        {
            logger.LogError($"Error listing G-code folders: {ex.GetType().Name} - {ex.Message}\n{ex.StackTrace}");
            return Problem($"Failed to retrieve folders: {ex.GetType().Name} - {ex.Message}", statusCode: 500);
        }
    }

    // ---------------- Chunked Upload Endpoints ----------------
    [HttpPost("chunk/init")]
    [ProducesResponseType(typeof(ChunkInitResponse), 200)]
    [ProducesResponseType(400)]
    [SuppressMessage("Security", "CA3003", Justification = "All paths rooted under validated target directory; filenames sanitized and collisions resolved server-side.")]
    public ActionResult<ChunkInitResponse> InitChunkedUpload([FromBody] ChunkInitRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.FileName) || req.Size <= 0)
        {
            return BadRequest("fileName and positive size required");
        }

        string ext = Path.GetExtension(req.FileName) ?? string.Empty;
        if (!AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest($"Invalid file type '{ext}'. Allowed: {string.Join(", ", AllowedExtensions)}");
        }

        try
        {
            // Resolve and validate target directory
            (_, string? targetDirFullPath, string? virtualDir) = ResolveAndValidatePath(req.Path ?? "/");
            if (!Directory.Exists(targetDirFullPath))
            {
                _ = Directory.CreateDirectory(targetDirFullPath);
            }

            // Get user ID for quota tracking
            string userId = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anonymous";

            // Delegate to service - it handles all business logic (sanitization, collision resolution, temp file creation, etc.)
            ChunkedUploadInitResult result = chunkedUploadService.InitializeUpload(
                userId,
                req.FileName,
                req.Size,
                targetDirFullPath,
                AllowedExtensions,
                req.HashAlgorithm,
                req.ExpectedHash,
                virtualDir);

            string virtualFilePath = virtualDir == "/" ? "/" + result.SafeFileName : virtualDir?.TrimEnd('/') + "/" + result.SafeFileName;
            return Ok(new ChunkInitResponse(
                result.UploadId,
                result.SafeFileName,
                virtualFilePath,
                0,
                req.Size,
                result.RecommendedChunkSize,
                req.HashAlgorithm));
        }
        catch (Exception ex)
        {
            logger.LogError($"Chunk init failure for {req.FileName}: {ex.Message}");
            return Problem("Failed to initialize chunked upload", statusCode: 500);
        }
    }

    [HttpPut("chunk/{uploadId}")]
    [RequestSizeLimit(50_000_000)] // 50MB per request upper bound (clients should use recommended chunk size)
    [ProducesResponseType(typeof(ChunkStatusResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    [SuppressMessage("Security", "CA3003", Justification = "Temp file path stored in validated state object created during init; operations restricted to that directory.")]
    public async Task<ActionResult<ChunkStatusResponse>> UploadChunkAsync([FromRoute] string uploadId, [FromQuery] long offset)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            return NotFound();
        }

        try
        {
            // Read chunk data from request body
            await using MemoryStream ms = new();
            await Request.Body.CopyToAsync(ms);
            byte[] chunkBytes = ms.ToArray();

            if (chunkBytes.Length == 0)
            {
                return BadRequest("Empty chunk");
            }

            // Get user ID for service
            string userId = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anonymous";

            // Delegate to service - it handles all the validation, quota checking, hashing, and finalization
            ChunkedUploadStatus result = await chunkedUploadService.AppendChunkAsync(uploadId, offset, chunkBytes, userId, quotaService);

            // If upload is complete, finalize it to the database
            Guid? gcodeFileId = null;
            if (result.IsCompleted)
            {
                logger.LogInformation("Upload chunk: Upload is complete, calling FinalizeChunkedUploadAsync with thumbnailPath={ThumbnailPath}", result.ThumbnailPath ?? "(null)");
                try
                {
                    // Retrieve virtual directory from upload session
                    string? virtualDir = chunkedUploadService.GetUploadVirtualDirectory(uploadId);

                    // Try to finalize the upload to database, passing the thumbnail path and virtual directory
                    var gcodeFile = await gcodeFilesService.FinalizeChunkedUploadAsync(
                        GetUploadFilePath(result),
                        result.SafeFileName,
                        result.ThumbnailPath,
                        virtualDir,
                        chunkedUploadService,
                        CancellationToken.None);

                    if (gcodeFile != null)
                    {
                        gcodeFileId = gcodeFile.Id;
                        logger.LogInformation("Finalized chunked upload {UploadId} to database with GcodeFile ID {FileId}", uploadId, gcodeFileId);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to finalize chunked upload {UploadId} to database, but file is on disk", uploadId);
                    // Continue anyway - file is on disk, just not indexed in database
                }
            }

            return Ok(new ChunkStatusResponse(
                result.UploadId,
                result.SafeFileName,
                result.UploadedBytes,
                result.TotalSize,
                result.IsCompleted,
                result.FinalHash,
                result.IsPaused,
                result.ThumbnailPath,
                gcodeFileId));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return NotFound();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("offset"))
        {
            return Conflict(new { error = "offset_mismatch", message = ex.Message });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("paused"))
        {
            ChunkedUploadStatus? status = chunkedUploadService.GetOrResumeUpload(uploadId);
            if (status != null)
            {
                return StatusCode(StatusCodes.Status423Locked, new
                {
                    error = "upload_paused",
                    status = new ChunkStatusResponse(status.UploadId, status.SafeFileName, status.UploadedBytes, status.TotalSize, status.IsCompleted, status.FinalHash, status.IsPaused, status.ThumbnailPath, null)
                });
            }
            return NotFound();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("quota"))
        {
            // Extract quota info from exception message if available
            return StatusCode(StatusCodes.Status429TooManyRequests, "Daily upload quota exceeded");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("hash_mismatch"))
        {
            return UnprocessableEntity(new { error = "hash_mismatch", message = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError($"Chunk upload failure {uploadId}: {ex.Message}");
            return Problem("Failed to append chunk", statusCode: 500);
        }
    }

    /// <summary>
    /// Gets current status of a chunked upload. If not in memory but a metadata file exists (service restart scenario),
    /// the state will be rehydrated, enabling resume capability.
    /// </summary>
    [HttpGet("chunk/{uploadId}")]
    [ProducesResponseType(typeof(ChunkStatusResponse), 200)]
    [ProducesResponseType(404)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003", Justification = "Meta file path discovered via controlled search under validated root; uploadId only used as search token.")]
    public ActionResult<ChunkStatusResponse> GetOrResumeChunk([FromRoute] string uploadId)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            return NotFound();
        }

        try
        {
            // Delegate to service - it handles both in-memory and recovery from metadata
            ChunkedUploadStatus? status = chunkedUploadService.GetOrResumeUpload(uploadId);
            if (status == null)
            {
                return NotFound();
            }

            return Ok(new ChunkStatusResponse(
                status.UploadId,
                status.SafeFileName,
                status.UploadedBytes,
                status.TotalSize,
                status.IsCompleted,
                status.FinalHash,
                status.IsPaused,
                status.ThumbnailPath,
                null));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            logger.LogDebug($"Chunk resume failed {uploadId}: {ex.Message}");
            return NotFound();
        }
    }

    [HttpPost("chunk/{uploadId}/pause")]
    [ProducesResponseType(typeof(ChunkStatusResponse), 200)]
    [ProducesResponseType(404)]
    public ActionResult<ChunkStatusResponse> PauseChunkUpload([FromRoute] string uploadId)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            return NotFound();
        }

        try
        {
            // Delegate to service
            ChunkedUploadStatus? status = chunkedUploadService.PauseUpload(uploadId);
            if (status == null)
            {
                return NotFound();
            }

            return Ok(new ChunkStatusResponse(
                status.UploadId,
                status.SafeFileName,
                status.UploadedBytes,
                status.TotalSize,
                status.IsCompleted,
                status.FinalHash,
                status.IsPaused,
                status.ThumbnailPath,
                null));
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }

    [HttpPost("chunk/{uploadId}/resume")]
    [ProducesResponseType(typeof(ChunkStatusResponse), 200)]
    [ProducesResponseType(404)]
    public ActionResult<ChunkStatusResponse> ResumeChunkUpload([FromRoute] string uploadId)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            return NotFound();
        }

        try
        {
            // Delegate to service
            ChunkedUploadStatus? status = chunkedUploadService.ResumeUpload(uploadId);
            if (status == null)
            {
                return NotFound();
            }

            return Ok(new ChunkStatusResponse(
                status.UploadId,
                status.SafeFileName,
                status.UploadedBytes,
                status.TotalSize,
                status.IsCompleted,
                status.FinalHash,
                status.IsPaused,
                status.ThumbnailPath,
                null));
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }

    [HttpDelete("chunk/{uploadId}")]
    [ProducesResponseType(204)]
    [SuppressMessage("Security", "CA3003", Justification = "Temp file path stored in validated state object created during init; deletion restricted to that path.")]
    public IActionResult CancelChunkUpload([FromRoute] string uploadId)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            return NoContent();
        }

        try
        {
            // Delegate to service - it handles cleanup of temp files and metadata
            chunkedUploadService.CancelUpload(uploadId);
        }
        catch (InvalidOperationException)
        {
            // Upload not found - that's fine, just return success (idempotent)
        }
        catch (Exception ex)
        {
            logger.LogDebug($"Failed to cancel upload {uploadId}: {ex.Message}");
            // Still return success - we tried our best to clean up
        }

        return NoContent();
    }

    /// <summary>
    /// Delete a single gcode file by ID
    /// </summary>
    /// <param name="id">Gcode file ID</param>
    /// <returns>No content if successful</returns>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteGcodeFileAsync(Guid id)
    {
        try
        {
            bool success = await gcodeFilesService.DeleteFilesAsync(new[] { id }, HttpContext.RequestAborted);
            if (!success)
            {
                return NotFound(new { message = "File not found", fileId = id });
            }
            return NoContent();
        }
        catch (Exception ex)
        {
            logger.LogError($"Error deleting G-code file {id}: {ex.Message}");
            return Problem("Failed to delete file", statusCode: 500);
        }
    }

    /// <summary>
    /// Delete G-code files by file IDs
    /// </summary>
    /// <param name="request">Request with list of file IDs (GUIDs) to delete</param>
    /// <returns>Deletion result with count</returns>
    [HttpDelete]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> DeleteGcodeFilesAsync([FromBody] DeleteFilesRequest request)
    {
        if (request?.FileIds == null || request.FileIds.Count == 0)
        {
            return BadRequest("fileIds is required");
        }

        try
        {
            bool success = await gcodeFilesService.DeleteFilesAsync(request.FileIds, HttpContext.RequestAborted);
            return Ok(new
            {
                deleted = success ? request.FileIds.Count : 0,
                totalRequested = request.FileIds.Count
            });
        }
        catch (Exception ex)
        {
            logger.LogError($"Error deleting G-code files: {ex.Message}");
            return Problem("Failed to delete files", statusCode: 500);
        }
    }

    /// <summary>
    /// Downloads a single file by virtual path.
    /// </summary>
    [HttpGet("download")]
    [HttpHead("download")]
    [ProducesResponseType(typeof(FileContentResult), 200)]
    [ProducesResponseType(304)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> DownloadAsync([FromQuery] string path)
    {
        try
        {
            (byte[] bytes, string fileName)? result = await gcodeFilesService.DownloadAsync(path, HttpContext.RequestAborted);
            if (result == null)
            {
                return NotFound();
            }

            // ETag and conditional request handling remains in controller
            // (requires access to Request/Response headers and HTTP context)
            (string _, string? fullFilePath, string _) = ResolveAndValidatePath(path, treatAsFile: true);
            System.IO.FileInfo info = new(fullFilePath);
            DateTime lastWriteUtc = info.LastWriteTimeUtc;
            bool useWeak = Environment.GetEnvironmentVariable("GCODE_WEAK_ETAGS") == "1";
            string etag = GenerateEtag(info, useWeak);

            RequestHeaders typedHeaders = Request.GetTypedHeaders();
            IList<EntityTagHeaderValue> ifNoneMatch = typedHeaders.IfNoneMatch;
            if (ifNoneMatch != null && ifNoneMatch.Any(t => string.Equals(t.Tag.ToString(), etag, StringComparison.Ordinal)))
            {
                Response.Headers["ETag"] = etag;
                Response.Headers["Last-Modified"] = lastWriteUtc.ToString("R", CultureInfo.InvariantCulture);
                return StatusCode(StatusCodes.Status304NotModified);
            }

            DateTimeOffset? ifModifiedSince = typedHeaders.IfModifiedSince;
            if (ifModifiedSince.HasValue)
            {
                DateTime ims = ifModifiedSince.Value.UtcDateTime;
                if (lastWriteUtc <= ims || (lastWriteUtc - ims) < TimeSpan.FromSeconds(1))
                {
                    Response.Headers["ETag"] = etag;
                    Response.Headers["Last-Modified"] = lastWriteUtc.ToString("R", CultureInfo.InvariantCulture);
                    return StatusCode(StatusCodes.Status304NotModified);
                }
            }

            Response.Headers["ETag"] = etag;
            Response.Headers["Last-Modified"] = lastWriteUtc.ToString("R", CultureInfo.InvariantCulture);

            if (HttpContext.Request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            {
                Response.ContentLength = info.Length;
                return new StatusCodeResult(200);
            }

            return File(result.Value.bytes, "application/octet-stream", result.Value.fileName);
        }
        catch (Exception ex)
        {
            logger.LogError($"Error downloading G-code file {path}: {ex.Message}");
            return Problem("Failed to download file", statusCode: 500);
        }
    }

    /// <summary>
    /// Downloads a GCode file by ID.
    /// </summary>
    /// <param name="id">GCode file ID</param>
    /// <returns>GCode file</returns>
    [HttpGet("file/{id:guid}")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetGcodeFileAsync(Guid id)
    {
        try
        {
            logger.LogInformation($"Attempting to download GCode file {id}");

            // Get file path from service
            string? filePath = await gcodeFilesService.GetFilePathAsync(id, HttpContext.RequestAborted);

            if (filePath == null)
            {
                logger.LogWarning($"GCode file {id} not found in database");
                return NotFound(new { message = "File not found in database", fileId = id });
            }

            string gcodeRoot = storagePathService.GetGcodeStorageDirectory();
            string fullPath = ResolveGcodePath(filePath);

            logger.LogInformation($"GCode file {id} resolved path: {fullPath}");

            // Validate file safety and existence using consolidated service
            if (!storedFileOperationsService.FileExistsAndIsSafe(fullPath, gcodeRoot))
            {
                logger.LogWarning($"GCode file {id} is unsafe or does not exist: {fullPath}");
                return NotFound(new { message = "File not found or unsafe path", fileId = id });
            }

            // Get appropriate content type and filename from consolidated service
            string fileExtension = Path.GetExtension(fullPath);
            string contentType = storedFileOperationsService.GetContentTypeForFile(fileExtension);
            string fileName = Path.GetFileName(filePath);

            return PhysicalFile(fullPath, contentType, fileName);
        }
        catch (Exception ex)
        {
            logger.LogError($"Error retrieving GCode file {id}: {ex.Message}");
            return Problem("Failed to retrieve file", statusCode: 500);
        }
    }

    /// <summary>
    /// Get GCode file thumbnail image by ID.
    /// </summary>
    /// <param name="id">GCode file ID</param>
    /// <returns>Thumbnail image</returns>
    [HttpGet("thumbnail/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetGcodeThumbnailAsync(Guid id)
    {
        try
        {
            logger.LogInformation($"[Thumbnail] Retrieving thumbnail for GCode file {id}");

            string? thumbnailPath = await gcodeFilesService.GetThumbnailPathAsync(id, HttpContext.RequestAborted);

            if (thumbnailPath == null)
            {
                logger.LogWarning($"[Thumbnail] GCode file {id} not found or no thumbnail available");
                return NotFound("Thumbnail not available");
            }

            string absolutePath = ResolveGcodePath(thumbnailPath);
            string gcodeRoot = storagePathService.GetGcodeStorageDirectory();

            logger.LogInformation($"[Thumbnail] Resolved absolute path: {absolutePath}");

            bool fileExists = System.IO.File.Exists(absolutePath);
            logger.LogInformation($"[Thumbnail] File exists at '{absolutePath}': {fileExists}");

            if (!fileExists)
            {
                logger.LogWarning($"[Thumbnail] Thumbnail file not found at {absolutePath} for GCode file {id}");
                return NotFound("Thumbnail file not found on disk");
            }

            if (!fileManagementService.IsSafePath(absolutePath, gcodeRoot))
            {
                logger.LogWarning($"[Thumbnail] Unsafe path detected for thumbnail: {absolutePath}");
                return NotFound("Invalid thumbnail path");
            }

            string contentType = System.IO.Path.GetExtension(absolutePath).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                _ => "image/png"
            };

            logger.LogInformation($"[Thumbnail] Returning thumbnail for GCode file {id} with content type {contentType}");
            return PhysicalFile(absolutePath, contentType);
        }
        catch (Exception ex)
        {
            logger.LogError($"[Thumbnail] Error retrieving thumbnail for GCode file {id}: {ex.Message}");
            return Problem("Failed to retrieve thumbnail", statusCode: 500);
        }
    }

    /// <summary>
    /// Upload a new G-code file into the virtual library (non-recursive). Optional path query designates target directory.
    /// </summary>
    /// <param name="path">Optional virtual directory path (defaults to root '/').</param>
    /// <param name="file">Uploaded G-code file (multipart form field 'file').</param>
    [HttpPost("upload")]
    [RequestSizeLimit(200_000_000)] // 200 MB hard limit
    [ProducesResponseType(typeof(GcodeFileEntryDto), 201)]
    [ProducesResponseType(400)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Path is rooted under validated library root; filename sanitized and revalidated with GetFullPath + StartsWith check.")]
    public async Task<ActionResult<GcodeFileEntryDto>> UploadFileAsync([FromQuery] string? path = "/", [FromForm(Name = "file")] IFormFile? file = null)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest("File is required");
        }

        string ext = Path.GetExtension(file.FileName) ?? string.Empty;
        if (!AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest($"Invalid file type '{ext}'. Allowed: {string.Join(", ", AllowedExtensions)}");
        }

        // Quota check before writing
        string userId = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anonymous";
        if (!quotaService.TryAddUsage(userId, file.Length, out long used, out long limit))
        {
            Response.Headers["X-Upload-Quota-Limit"] = limit.ToString(CultureInfo.InvariantCulture);
            Response.Headers["X-Upload-Quota-Used"] = used.ToString(CultureInfo.InvariantCulture);
            return StatusCode(StatusCodes.Status429TooManyRequests, $"Daily upload quota exceeded ({used}/{limit} bytes)");
        }

        try
        {
            GcodeFileEntryDto dto = await gcodeFilesService.UploadFileAsync(
                path, file, uploadSettings, quotaService, HttpContext.RequestAborted);
            return Created($"/api/gcode-files?path={Uri.EscapeDataString(Path.GetDirectoryName(dto.Path) ?? "/")}", dto);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError($"Error uploading G-code file (path={path}): {ex.Message}");
            return Problem("Failed to upload file", statusCode: 500);
        }
    }

    /// <summary>
    /// Create a new virtual folder in the G-code library
    /// </summary>
    /// <param name="request">Request containing the folder path</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Operation result</returns>
    [HttpPost("folder")]
    [ProducesResponseType(typeof(FolderOperationResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(FolderOperationResultDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(FolderOperationResultDto), StatusCodes.Status409Conflict)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Directory name validated (invalid chars, separators removed) and combined under validated parent path; GetFullPath+StartsWith used prior to Directory.Exists.")]
    public async Task<ActionResult<FolderOperationResultDto>> CreateFolderAsync([FromBody] CreateFolderRequest request, CancellationToken ct)
    {
        string? path = null;
        string? name = null;

        try
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Path))
            {
                return BadRequest(new FolderOperationResultDto(false, "Folder path is required"));
            }

            // Extract parent path and folder name from the full path
            string fullPath = request.Path.Trim('/');
            string[] pathParts = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            name = pathParts.Length > 0 ? pathParts[^1] : null;
            path = pathParts.Length > 1 ? "/" + string.Join("/", pathParts[..^1]) : "/";

            if (string.IsNullOrWhiteSpace(name))
            {
                return BadRequest(new FolderOperationResultDto(false, "Folder path cannot be empty. Please provide a folder name."));
            }

            logger.LogDebug($"[CreateFolder] Input: '{request.Path}' -> path='{path}', name='{name}'");

            GcodeFileEntryDto dto = await gcodeFilesService.MakeDirectoryAsync(path, name, ct);

            logger.LogInformation($"[CreateFolder] Successfully created virtual folder: '{request.Path}'");
            return StatusCode(StatusCodes.Status201Created, new FolderOperationResultDto(true, "Folder created successfully"));
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning($"[CreateFolder] Invalid argument: {ex.Message}");
            return BadRequest(new FolderOperationResultDto(false, ex.Message));
        }
        catch (DirectoryNotFoundException ex)
        {
            logger.LogWarning($"[CreateFolder] Parent directory not found: {ex.Message}");
            return NotFound(new FolderOperationResultDto(false, ex.Message));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists"))
        {
            logger.LogInformation($"[CreateFolder] Folder already exists at: {request.Path}");
            return Conflict(new FolderOperationResultDto(false, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning($"[CreateFolder] Invalid operation: {ex.Message}");
            return BadRequest(new FolderOperationResultDto(false, ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError($"[CreateFolder] Unexpected error (path={path}, name={name}): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return StatusCode(StatusCodes.Status500InternalServerError, new FolderOperationResultDto(false, $"Failed to create folder: {ex.GetType().Name}"));
        }
    }

    // ------------------------------------------------------------
    // Helper models & utilities
    // ------------------------------------------------------------
    private (string rootFullPath, string resolvedFullPath, string virtualNormalized) ResolveAndValidatePath(
        string? virtualPath,
        string? rootFullPathOverride = null,
        bool treatAsFile = false)
    {
        // Delegate to storage service which handles all environment/config/default fallback logic
        // StoragePathService guarantees a non-null, non-empty path is always returned
        string baseRoot = rootFullPathOverride ?? storagePathService.GetGcodeStorageDirectory();

        // Ensure the resolved base root exists (idempotent) so later code relying on its presence succeeds.
        _ = Directory.CreateDirectory(baseRoot);
        string root = rootFullPathOverride ?? Path.GetFullPath(baseRoot);
        _ = Directory.CreateDirectory(root); // ensure exists

        // Normalize incoming virtual path
        string vPath = string.IsNullOrWhiteSpace(virtualPath) ? "/" : virtualPath.Trim();
        if (!vPath.StartsWith('/'))
        {
            vPath = "/" + vPath;
        }
        // Collapse .. segments
        string[] segments = vPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != "." && s != "..")
            .ToArray();
        string safeRel = segments.Length == 0 ? string.Empty : Path.Combine(segments);
        string candidate = Path.GetFullPath(Path.Combine(root, safeRel));
        if (!candidate.StartsWith(root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Path escapes library root");
        }

        if (!treatAsFile)
        {
            // Convert to directory path (candidate is directory path already if segments empty or represent folder)
            return (root, candidate, segments.Length == 0 ? "/" : "/" + string.Join('/', segments));
        }
        else
        {
            return (root, candidate, "/" + string.Join('/', segments));
        }
    }

    private static string GenerateEtag(System.IO.FileInfo info, bool weak = false)
    {
        string core = $"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}";
        return weak ? $"W/\"{core}\"" : $"\"{core}\"";
    }

    /// <summary>
    /// Helper method to derive the full file path from a chunked upload status.
    /// Used to finalize uploads to the database after completion.
    /// </summary>
    private static string GetUploadFilePath(ChunkedUploadStatus status)
    {
        if (string.IsNullOrWhiteSpace(status.FinalFilePath))
        {
            throw new InvalidOperationException("Upload has not completed - no final file path available");
        }
        return status.FinalFilePath;
    }

    // Note: ToHex has been moved to IFileManagementService.ToHex() - use that instead
    // Note: PersistChunkState has been moved to ChunkedUploadService - no longer needed here

    // ---------------- Settings & Move endpoints ----------------
    [HttpGet("settings")]
    [ProducesResponseType(typeof(GcodeUploadSettingsResponse), 200)]
    public async Task<ActionResult<GcodeUploadSettingsResponse>> GetSettingsAsync()
    {
        string userId = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anonymous";
        GcodeUploadSettingsResponse resp = await gcodeFilesService.GetSettingsAsync(userId, uploadSettings, quotaService, CancellationToken.None);
        return Ok(resp);
    }


    [HttpPut("settings")]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    public IActionResult UpdateSettings([FromBody] UpdateSettingsRequest req)
    {
        if (req?.AllowedExtensions == null || req.AllowedExtensions.Count == 0)
        {
            return BadRequest("allowedExtensions is required");
        }
        uploadSettings.UpdateAllowedExtensions(req.AllowedExtensions);
        return NoContent();
    }

    /// <summary>
    /// Move G-code files to a different virtual folder by file IDs
    /// </summary>
    /// <param name="request">Move request with file IDs and target folder</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Operation result</returns>
    [HttpPost("move")]
    [ProducesResponseType(typeof(FolderOperationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MoveFilesAsync([FromBody] MoveGcodeFilesRequest request, CancellationToken ct)
    {
        try
        {
            if (request == null || request.ModelIds == null || request.ModelIds.Count == 0)
            {
                return BadRequest(new FolderOperationResultDto(false, "At least one file ID is required"));
            }

            if (string.IsNullOrWhiteSpace(request.TargetDirectoryId))
            {
                return BadRequest(new FolderOperationResultDto(false, "Target directory ID is required"));
            }

            // Use the directory ID (virtual path) exactly as provided by the frontend
            // Frontend is responsible for constructing valid directory IDs
            string targetDirectoryPath = request.TargetDirectoryId;
            logger.LogDebug($"[MoveFiles] Moving {request.ModelIds.Count} G-code file(s) to virtual directory: '{targetDirectoryPath}'");

            int movedCount = 0;
            int failedCount = 0;
            var failedFiles = new List<(string id, string reason)>();

            // Move each file - virtual move (update database folder reference)
            foreach (var fileIdStr in request.ModelIds)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(fileIdStr))
                    {
                        continue;
                    }

                    if (!Guid.TryParse(fileIdStr, out Guid fileId))
                    {
                        logger.LogWarning($"[MoveFiles] Invalid file ID format: '{fileIdStr}'. Expected GUID from GcodeFileId field, not Path.");
                        failedFiles.Add((fileIdStr, "Invalid file ID format - use GcodeFileId, not Path"));
                        failedCount++;
                        continue;
                    }

                    // This will update the file's FolderId in the database
                    bool moved = await gcodeFilesService.MoveToFolderAsync(fileId, targetDirectoryPath, ct);

                    if (moved)
                    {
                        movedCount++;
                        logger.LogDebug($"[MoveFiles] Successfully moved file: {fileId}");
                    }
                    else
                    {
                        logger.LogWarning($"[MoveFiles] File not found or failed to move: {fileId}");
                        failedFiles.Add((fileIdStr, "File not found or failed to move"));
                        failedCount++;
                    }
                }
                catch (ArgumentException ex)
                {
                    logger.LogWarning($"[MoveFiles] Invalid argument for file {fileIdStr}: {ex.Message}");
                    failedFiles.Add((fileIdStr, $"Invalid argument: {ex.Message}"));
                    failedCount++;
                }
                catch (UnauthorizedAccessException ex)
                {
                    logger.LogWarning($"[MoveFiles] Access denied for file {fileIdStr}: {ex.Message}");
                    failedFiles.Add((fileIdStr, "Access denied"));
                    failedCount++;
                }
                catch (IOException ex)
                {
                    logger.LogWarning($"[MoveFiles] IO error for file {fileIdStr}: {ex.Message}");
                    failedFiles.Add((fileIdStr, $"IO error: {ex.Message}"));
                    failedCount++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"[MoveFiles] Unexpected error for file {fileIdStr}: {ex.GetType().Name}: {ex.Message}");
                    failedFiles.Add((fileIdStr, $"{ex.GetType().Name}: {ex.Message}"));
                    failedCount++;
                }
            }

            string message = failedCount == 0
                ? $"Successfully moved {movedCount} file(s)"
                : $"Moved {movedCount} file(s), failed to move {failedCount} file(s)";

            if (failedFiles.Count > 0)
            {
                var failureDetails = failedFiles.Take(3).Select(f => $"{f.id} ({f.reason})").ToList();
                message += $" - Failed: {string.Join(", ", failureDetails)}";
                if (failedFiles.Count > 3)
                {
                    message += $" and {failedFiles.Count - 3} more";
                }
            }

            logger.LogInformation($"[MoveFiles] Completed: {movedCount} succeeded, {failedCount} failed");
            return Ok(new FolderOperationResultDto(failedCount == 0, message));
        }
        catch (ArgumentException ex)
        {
            logger.LogError($"[MoveFiles] Invalid argument: {ex.Message}");
            return BadRequest(new FolderOperationResultDto(false, $"Invalid request: {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogError($"[MoveFiles] Access denied: {ex.Message}");
            return StatusCode(StatusCodes.Status403Forbidden, new FolderOperationResultDto(false, "Access denied: insufficient permissions"));
        }
        catch (IOException ex)
        {
            logger.LogError($"[MoveFiles] IO error: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new FolderOperationResultDto(false, $"I/O error: {ex.Message}"));
        }
        catch (Exception ex)
        {
            logger.LogError($"[MoveFiles] Unexpected error: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return StatusCode(StatusCodes.Status500InternalServerError, new FolderOperationResultDto(false, $"Failed to move files: {ex.GetType().Name}"));
        }
    }


}


// ---------------- Additional DTOs for move & settings ----------------
public record MoveGcodeFilesRequest(
    [property: JsonPropertyName("modelIds")] List<string> ModelIds,
    [property: JsonPropertyName("targetDirectoryId")] string TargetDirectoryId
);

public record MoveRequestDto(
    [property: JsonPropertyName("sourcePath")] string SourcePath,
    [property: JsonPropertyName("destinationPath")] string DestinationPath,
    [property: JsonPropertyName("overwrite")] bool Overwrite = false
);

public record GcodeUploadSettingsResponse(
    [property: JsonPropertyName("allowedExtensions")] IReadOnlyCollection<string> AllowedExtensions,
    [property: JsonPropertyName("dailyUploadLimitBytes")] long DailyUploadLimitBytes,
    [property: JsonPropertyName("userUsedBytes")] long UserUsedBytes
);

/// <summary>DTO describing a single file or directory entry in the virtual G-code library listing.</summary>
public record GcodeFileEntryDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("fileSize")] long FileSize,
    [property: JsonPropertyName("uploadedAt")] DateTime UploadedAt,
    [property: JsonPropertyName("isDirectory")] bool IsDirectory,
    [property: JsonPropertyName("name")] string? Name = null,  // Original filename for display
    [property: JsonPropertyName("thumbnailUrl")] string? ThumbnailUrl = null,
    [property: JsonPropertyName("id")] string? Id = null,  // Include file ID for efficient lookups (GUID as string)
    [property: JsonPropertyName("fileType")] string? FileType = null,  // File extension: gcode, bgcode
    [property: JsonPropertyName("directoryId")] string? DirectoryId = null,   // Include directory ID for efficient directory lookups (virtual path)
    [property: JsonPropertyName("targetModelName")] string? TargetModelName = null,  // Printer model this gcode was sliced for
    [property: JsonPropertyName("requiredMaterial")] string? RequiredMaterial = null,  // Required filament type (e.g., "PLA", "PETG")
    [property: JsonPropertyName("tags")] IReadOnlyList<TagDto>? Tags = null,  // Tags assigned to this gcode file
    [property: JsonPropertyName("extractedSlicerName")] string? ExtractedSlicerName = null,  // Slicer used (PrusaSlicer, OrcaSlicer, etc.)
    [property: JsonPropertyName("extractedSlicerVersion")] string? ExtractedSlicerVersion = null,
    [property: JsonPropertyName("extractedPrintTime")] double? ExtractedPrintTime = null,  // Minutes
    [property: JsonPropertyName("extractedFilamentLength")] double? ExtractedFilamentLength = null,  // Millimeters
    [property: JsonPropertyName("extractedNozzleDiameter")] double? ExtractedNozzleDiameter = null,  // Millimeters
    [property: JsonPropertyName("extractedMaterial")] string? ExtractedMaterial = null,
    [property: JsonPropertyName("extractedPrinterModel")] string? ExtractedPrinterModel = null,
    [property: JsonPropertyName("extractedPrinterModelName")] string? ExtractedPrinterModelName = null,  // Raw extracted name (fallback if resolution failed)
    [property: JsonPropertyName("extractedLayerHeight")] double? ExtractedLayerHeight = null,  // Millimeters
    [property: JsonPropertyName("extractedInfill")] double? ExtractedInfill = null,  // Percentage
    [property: JsonPropertyName("extractedPerimeters")] int? ExtractedPerimeters = null,  // Number of perimeter loops
    [property: JsonPropertyName("extractedHotendTemp")] double? ExtractedHotendTemp = null,  // Celsius
    [property: JsonPropertyName("extractedBedTemp")] double? ExtractedBedTemp = null  // Celsius
);

/// <summary>
/// Response envelope for a directory listing.
/// totalFiles/totalSize refer ONLY to regular files in the (unpaginated) result set (not directories).
/// totalItems counts both directories and files prior to pagination; it is used with page/pageSize to compute totalPages.
/// </summary>
public record GcodeFileListResponse(
    [property: JsonPropertyName("files")] IReadOnlyList<GcodeFileEntryDto> Files,
    [property: JsonPropertyName("totalFiles")] int TotalFiles,
    [property: JsonPropertyName("totalSize")] long TotalSize,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("totalPages")] int TotalPages,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("availablePrinterModels")] IReadOnlyList<PrinterModelSummary>? AvailablePrinterModels = null
);

/// <summary>Summary of printer model info for filtering.</summary>
public record PrinterModelSummary(
    [property: JsonPropertyName("id")] Guid? Id,
    [property: JsonPropertyName("name")] string Name
);

/// <summary>Response for multi-file upload endpoint.</summary>
public record MultiUploadResponse(
    [property: JsonPropertyName("created")] IReadOnlyList<GcodeFileEntryDto> Created,
    [property: JsonPropertyName("failed")] IReadOnlyList<MultiUploadFailure> Failed,
    [property: JsonPropertyName("succeededCount")] int SucceededCount,
    [property: JsonPropertyName("failedCount")] int FailedCount
);

/// <summary>Failure detail for an individual file during multi-upload.</summary>
public record MultiUploadFailure(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("error")] string Error
);

public sealed record UpdateSettingsRequest(
    [property: JsonPropertyName("allowedExtensions")] IReadOnlyCollection<string> AllowedExtensions
);


/// <summary>Request body for bulk deletion of virtual G-code files.</summary>
public sealed class DeleteFilesRequest
{
    [JsonPropertyName("fileIds")] public IList<Guid> FileIds { get; init; } = Array.Empty<Guid>();
}

// ---------------- Chunk Upload DTOs ----------------
public sealed record ChunkInitRequest(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("hashAlgorithm")] string? HashAlgorithm = null,
    [property: JsonPropertyName("expectedHash")] string? ExpectedHash = null
);

public sealed record ChunkInitResponse(
    [property: JsonPropertyName("uploadId")] string UploadId,
    [property: JsonPropertyName("finalFileName")] string FinalFileName,
    [property: JsonPropertyName("virtualPath")] string VirtualPath,
    [property: JsonPropertyName("uploadedBytes")] long UploadedBytes,
    [property: JsonPropertyName("totalSize")] long TotalSize,
    [property: JsonPropertyName("recommendedChunkSize")] int RecommendedChunkSize,
    [property: JsonPropertyName("hashAlgorithm")] string? HashAlgorithm
);

public sealed record ChunkStatusResponse(
    [property: JsonPropertyName("uploadId")] string UploadId,
    [property: JsonPropertyName("finalFileName")] string FinalFileName,
    [property: JsonPropertyName("uploadedBytes")] long UploadedBytes,
    [property: JsonPropertyName("totalSize")] long TotalSize,
    [property: JsonPropertyName("completed")] bool Completed,
    [property: JsonPropertyName("finalHash")] string? FinalHash = null,
    [property: JsonPropertyName("paused")] bool Paused = false,
    [property: JsonPropertyName("thumbnailPath")] string? ThumbnailPath = null,
    [property: JsonPropertyName("gcodeFileId")] Guid? GcodeFileId = null
);

public sealed record GcodeFileHashResponse(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("algorithm")] string Algorithm,
    [property: JsonPropertyName("hash")] string Hash
);
