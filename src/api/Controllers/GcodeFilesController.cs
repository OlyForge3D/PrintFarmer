using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Telemetry;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Data;
using Farm.Web.Api.Services; // added for IGcodeUploadSettings & quota services
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.AspNetCore.Mvc;
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
    IWebHostEnvironment env,
    IUnifiedLoggingService logger,
    AppDbContext db,
    IGcodeUploadSettings uploadSettings,
    IGcodeUploadQuotaService quotaService
) : ControllerBase
{
    // Dynamic allowed extensions supplied by runtime settings service.
    private IReadOnlyCollection<string> AllowedExtensions => uploadSettings.AllowedExtensions;

    // ---------------- Chunked upload (in-memory state) ----------------
    private static readonly ConcurrentDictionary<string, ChunkUploadState> _chunkStates = new();
    private const int DefaultChunkSize = 1 * 1024 * 1024; // 1 MB recommended chunk size

    private sealed class ChunkUploadState
    {
        public required string Id { get; init; }
        public required string UserId { get; init; }
        public required string TempFilePath { get; init; }
        public required string MetaFilePath { get; init; }
        public required string TargetDirectoryFullPath { get; init; }
        public required string FinalSafeName { get; set; }
        public required long TotalSize { get; init; }
        public long UploadedBytes; // atomic via Interlocked only if multithreaded
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
        public string? HashAlgorithm { get; init; }
        public string? ExpectedHash { get; init; }
        public string? FinalHash { get; set; }
        public IncrementalHash? Hasher { get; init; }
        public bool Paused { get; set; }
    }

    /// <summary>
    /// Computes a hash for an existing G-code file for deduplication/comparison (sha256 default; supports sha1).
    /// </summary>
    [HttpGet("hash")]
    [ProducesResponseType(typeof(GcodeFileHashResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [SuppressMessage("Security", "CA3003", Justification = "Path validated via ResolveAndValidatePath and enforced root prefix check.")]
    public ActionResult<GcodeFileHashResponse> GetFileHash([FromQuery] string? path, [FromQuery] string? algorithm = "sha256")
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("path is required");
        }
        algorithm = (algorithm ?? "sha256").Trim().ToLowerInvariant();
        if (algorithm is not ("sha256" or "sha1"))
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
            string hex = ToHex(hasher.GetHashAndReset());
            return Ok(new GcodeFileHashResponse(fileName, total, algorithm, hex));
        }
        catch (Exception ex)
        {
            logger.LogError($"Failed to hash file {path}: {ex.Message}");
            return Problem("Failed to compute hash", statusCode: 500);
        }
    }

    /// <summary>
    /// Returns a non-recursive listing of the given virtual path within the G-code library folder.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(GcodeFileListResponse), 200)]
    public ActionResult<GcodeFileListResponse> List(
    [FromQuery] string? path = "/",
    [FromQuery] string? sortBy = "name",
    [FromQuery] string? sortOrder = "asc",
    [FromQuery] string? search = null,
    [FromQuery] int page = 1,
    [FromQuery] int pageSize = 100,
    [FromQuery] Guid? harvestId = null, // now used to filter files by harvest session
    [FromQuery] Guid? printerId = null  // presently unused
    )
    {
        try
        {
            if (page < 1)
            {
                page = 1;
            }
            if (pageSize < 1)
            {
                pageSize = 1;
            }
            if (pageSize > 500)
            {
                pageSize = 500; // hard cap to avoid huge payloads
            }
            (string _, string? requestedDirFullPath, string? virtualPathNormalized) = ResolveAndValidatePath(path);
            if (!Directory.Exists(requestedDirFullPath))
            {
                return NotFound($"Directory '{virtualPathNormalized}' not found");
            }

            // Gather directory & file entries (only immediate children)
            System.IO.DirectoryInfo dirInfo = new(requestedDirFullPath);
            List<GcodeFileEntryDto> entries = new();

            // Directories
            foreach (System.IO.DirectoryInfo dir in dirInfo.EnumerateDirectories())
            {
                if (dir.Name.StartsWith('.'))
                {
                    continue; // skip hidden
                }
                if (!IsMatch(dir.Name, search))
                {
                    continue;
                }
                string childVirtual = CombineVirtual(virtualPathNormalized, dir.Name);
                entries.Add(new GcodeFileEntryDto(
                    Path: childVirtual,
                    Name: dir.Name,
                    Size: 0,
                    ModifiedAt: dir.LastWriteTimeUtc,
                    IsDirectory: true
                ));
            }

            // Files (.gcode + .bgcode)
            foreach (string? pattern in new[] { "*.gcode", "*.bgcode" })
            {
                foreach (System.IO.FileInfo file in dirInfo.EnumerateFiles(pattern))
                {
                    if (!IsMatch(file.Name, search))
                    {
                        continue;
                    }
                    string childVirtual = CombineVirtual(virtualPathNormalized, file.Name);

                    // Attempt to correlate with DB entry for potential future harvest association.
                    Guid? harvestOpId = null;
                    try
                    {
                        GcodeFile? dbEntry = db.GcodeFiles.FirstOrDefault(g => g.FilePath == file.FullName);
                        if (dbEntry != null && dbEntry.SourcePrinterId != null)
                        {
                            GcodeHarvestOperation? op = db.GcodeHarvestOperations
                                .Where(o => o.PrinterId == dbEntry.SourcePrinterId)
                                .OrderByDescending(o => o.StartedAt)
                                .FirstOrDefault();
                            harvestOpId = op?.Id;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug($"Non-fatal DB correlation failure for file {file.FullName}: {ex.Message}");
                    }

                    // If harvestId is specified, only include files with matching HarvestOperationId
                    if (harvestId.HasValue && harvestOpId != harvestId)
                    {
                        continue;
                    }

                    entries.Add(new GcodeFileEntryDto(
                        Path: childVirtual,
                        Name: file.Name,
                        Size: file.Length,
                        ModifiedAt: file.LastWriteTimeUtc,
                        IsDirectory: false,
                        HarvestOperationId: harvestOpId
                    ));
                }
            }

            // Sorting
            entries = (sortBy?.ToLowerInvariant(), sortOrder?.ToLowerInvariant()) switch
            {
                ("size", "desc") => entries.OrderByDescending(e => e.IsDirectory).ThenByDescending(e => e.Size).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                ("size", _) => entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Size).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                ("date", "desc") => entries.OrderByDescending(e => e.IsDirectory).ThenByDescending(e => e.ModifiedAt).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                ("date", _) => entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.ModifiedAt).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                ("name", "desc") => entries.OrderByDescending(e => e.IsDirectory).ThenByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                _ => entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList()
            };

            int totalFiles = entries.Count(e => !e.IsDirectory);
            long totalSize = entries.Where(e => !e.IsDirectory).Sum(e => e.Size);

            // Apply pagination AFTER computing totals so client can derive total pages.
            int skip = (page - 1) * pageSize;
            IReadOnlyList<GcodeFileEntryDto> pagedEntries = skip >= entries.Count ? Array.Empty<GcodeFileEntryDto>() : entries.Skip(skip).Take(pageSize).ToList();
            int totalItems = entries.Count; // directories + files for pagination context
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
            return Ok(new GcodeFileListResponse(
                Files: pagedEntries,
                TotalFiles: totalFiles,
                TotalSize: totalSize,
                Page: page,
                PageSize: pageSize,
                TotalPages: totalPages,
                TotalItems: totalItems));
        }
        catch (Exception ex)
        {
            logger.LogError($"Error listing G-code files (path={path}): {ex.Message}");
            return Problem("Failed to retrieve files", statusCode: 500);
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
            (string _, string? targetDirFullPath, string? virtualDir) = ResolveAndValidatePath(req.Path ?? "/");
            if (!Directory.Exists(targetDirFullPath))
            {
                Directory.CreateDirectory(targetDirFullPath);
            }
            // Sanitize filename & collision resolution (reserve final name now so user sees stable name)
            string originalName = Path.GetFileName(req.FileName);
            if (string.IsNullOrWhiteSpace(originalName))
            {
                originalName = "upload" + ext;
            }
            string safeName = originalName;
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                safeName = safeName.Replace(c, '_');
            }
            if (!safeName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                safeName += ext;
            }

            string destinationPath = Path.Combine(targetDirFullPath, safeName);
            string fullTarget = Path.GetFullPath(destinationPath);
            if (!fullTarget.StartsWith(targetDirFullPath, StringComparison.Ordinal))
            {
                return BadRequest("Unsafe target path");
            }
            if (System.IO.File.Exists(fullTarget))
            {
                string baseName = Path.GetFileNameWithoutExtension(safeName);
                int counter = 1;
                string candidate;
                do
                {
                    candidate = baseName + " (" + counter++ + ")" + ext;
                    fullTarget = Path.GetFullPath(Path.Combine(targetDirFullPath, candidate));
                } while (System.IO.File.Exists(fullTarget));
                safeName = Path.GetFileName(fullTarget);
            }
            // Create temp part file path (unique by GUID) to avoid partial naming collisions
            string uploadId = Guid.NewGuid().ToString("N");
            string tempFilePath = Path.Combine(targetDirFullPath, safeName + "." + uploadId + ".part");
            string metaFilePath = tempFilePath + ".meta.json";
            using (System.IO.File.Create(tempFilePath))
            {
                // create empty temp file
            }
            string userId = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anonymous";
            // Optional hashing (sha256 default if provided "sha256" or "sha1")
            IncrementalHash? hasher = null;
            string? hashAlgo = null;
            string? expectedHash = null;
            if (!string.IsNullOrWhiteSpace(req.HashAlgorithm))
            {
                string algo = req.HashAlgorithm.Trim().ToLowerInvariant();
                if (algo is not ("sha256" or "sha1"))
                {
                    return BadRequest("Unsupported hashAlgorithm. Allowed: sha256, sha1");
                }
                hashAlgo = algo;
                expectedHash = string.IsNullOrWhiteSpace(req.ExpectedHash) ? null : req.ExpectedHash.Trim().ToLowerInvariant();
                hasher = algo switch
                {
                    "sha1" => IncrementalHash.CreateHash(HashAlgorithmName.SHA1),
                    _ => IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                };
            }
            ChunkUploadState state = new()
            {
                Id = uploadId,
                UserId = userId,
                TempFilePath = tempFilePath,
                MetaFilePath = metaFilePath,
                TargetDirectoryFullPath = targetDirFullPath,
                FinalSafeName = safeName,
                TotalSize = req.Size,
                UploadedBytes = 0,
                HashAlgorithm = hashAlgo,
                ExpectedHash = expectedHash,
                Hasher = hasher,
                Paused = false
            };
            if (!_chunkStates.TryAdd(uploadId, state))
            {
                return Problem("Failed to initialize upload", statusCode: 500);
            }
            PersistChunkState(state, logger);
            string virtualFilePath = CombineVirtual(virtualDir, safeName);
            return Ok(new ChunkInitResponse(uploadId, safeName, virtualFilePath, 0, req.Size, DefaultChunkSize, hashAlgo));
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
        if (!_chunkStates.TryGetValue(uploadId, out ChunkUploadState? state))
        {
            return NotFound();
        }
        // If server-side paused, block further chunk data until resumed to avoid inconsistent state.
        if (state.Paused)
        {
            return StatusCode(423, new
            {
                error = "upload_paused",
                status = new ChunkStatusResponse(state.Id, state.FinalSafeName, state.UploadedBytes, state.TotalSize, state.UploadedBytes == state.TotalSize, state.FinalHash, true)
            });
        }
        if (offset != state.UploadedBytes)
        {
            return Conflict(new { expectedOffset = state.UploadedBytes, providedOffset = offset });
        }
        try
        {
            // Read body stream fully into buffer (could stream append but we need size for quota)
            await using MemoryStream ms = new();
            await Request.Body.CopyToAsync(ms);
            byte[] chunkBytes = ms.ToArray();
            if (chunkBytes.Length == 0)
            {
                return BadRequest("Empty chunk");
            }
            long remaining = state.TotalSize - state.UploadedBytes;
            if (chunkBytes.Length > remaining)
            {
                return BadRequest("Chunk exceeds remaining size");
            }
            string userId = state.UserId;
            if (!quotaService.TryAddUsage(userId, chunkBytes.Length, out long used, out long limit))
            {
                Response.Headers["X-Upload-Quota-Limit"] = limit.ToString(CultureInfo.InvariantCulture);
                Response.Headers["X-Upload-Quota-Used"] = used.ToString(CultureInfo.InvariantCulture);
                return StatusCode(429, "Daily upload quota exceeded");
            }
            await System.IO.File.AppendAllTextAsync(state.TempFilePath, string.Empty); // ensure exists
            await using (FileStream fs = new(state.TempFilePath, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                await fs.WriteAsync(new ReadOnlyMemory<byte>(chunkBytes), CancellationToken.None);
            }
            state.UploadedBytes += chunkBytes.Length;
            state.Hasher?.AppendData(chunkBytes);
            bool completed = state.UploadedBytes == state.TotalSize;
            if (completed)
            {
                // Finalize: move temp file to final destination name
                string finalFull = Path.Combine(state.TargetDirectoryFullPath, state.FinalSafeName);
                if (System.IO.File.Exists(finalFull))
                {
                    // Extremely rare: file created after init; collision resolve again
                    string ext = Path.GetExtension(state.FinalSafeName);
                    string baseName = Path.GetFileNameWithoutExtension(state.FinalSafeName);
                    int counter = 1;
                    string candidate;
                    string newFull;
                    do
                    {
                        candidate = baseName + " (" + counter++ + ")" + ext;
                        newFull = Path.GetFullPath(Path.Combine(state.TargetDirectoryFullPath, candidate));
                    } while (System.IO.File.Exists(newFull));
                    state.FinalSafeName = Path.GetFileName(newFull);
                    finalFull = newFull;
                }
                if (state.Hasher != null)
                {
                    byte[] hashBytes = state.Hasher.GetHashAndReset();
                    string hex = ToHex(hashBytes);
                    state.FinalHash = hex;
                    if (state.ExpectedHash != null && !hex.Equals(state.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            System.IO.File.Delete(state.TempFilePath);
                            if (System.IO.File.Exists(state.MetaFilePath))
                            {
                                System.IO.File.Delete(state.MetaFilePath);
                            }
                        }
                        catch { }
                        _chunkStates.TryRemove(uploadId, out _);
                        return UnprocessableEntity(new { error = "hash_mismatch", expected = state.ExpectedHash, actual = hex });
                    }
                }
                System.IO.File.Move(state.TempFilePath, finalFull, overwrite: false);
                if (System.IO.File.Exists(state.MetaFilePath))
                {
                    System.IO.File.Delete(state.MetaFilePath);
                }
                _chunkStates.TryRemove(uploadId, out _);
            }
            else
            {
                PersistChunkState(state, logger);
            }
            return Ok(new ChunkStatusResponse(uploadId, state.FinalSafeName, state.UploadedBytes, state.TotalSize, completed, state.FinalHash, state.Paused));
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
        if (_chunkStates.TryGetValue(uploadId, out ChunkUploadState? existing))
        {
            return Ok(new ChunkStatusResponse(existing.Id, existing.FinalSafeName, existing.UploadedBytes, existing.TotalSize, existing.UploadedBytes == existing.TotalSize, existing.FinalHash, existing.Paused));
        }
        // Attempt rehydrate by searching known temp roots (current web root gcode-library)
        try
        {
            (string? root, string _, string _) = ResolveAndValidatePath("/");
            List<string> metas = Directory.EnumerateFiles(root, $"*.{uploadId}.part.meta.json", SearchOption.AllDirectories).Take(1).ToList();
            if (metas.Count == 0)
            {
                return NotFound();
            }
            string metaFile = metas[0];
            string json = System.IO.File.ReadAllText(metaFile);
            JsonDocument doc = JsonDocument.Parse(json);
            long totalSize = doc.RootElement.GetProperty("TotalSize").GetInt64();
            long uploadedBytes = doc.RootElement.GetProperty("UploadedBytes").GetInt64();
            string finalSafeName = doc.RootElement.GetProperty("FinalSafeName").GetString() ?? "unknown";
            string targetDir = doc.RootElement.GetProperty("TargetDirectoryFullPath").GetString() ?? Path.GetDirectoryName(metaFile)!;
            string tempPath = doc.RootElement.GetProperty("TempFilePath").GetString() ?? Path.Combine(targetDir, finalSafeName + "." + uploadId + ".part");
            string? hashAlgo = doc.RootElement.TryGetProperty("HashAlgorithm", out JsonElement ha) ? ha.GetString() : null;
            string? expectedHash = doc.RootElement.TryGetProperty("ExpectedHash", out JsonElement eh) ? eh.GetString() : null;
            bool paused = doc.RootElement.TryGetProperty("Paused", out JsonElement pEl) && pEl.GetBoolean();
            // Recreate IncrementalHash cannot continue (need bytes). Without full rehash we only allow resume if no hash specified.
            IncrementalHash? hasher = null;
            if (!string.IsNullOrWhiteSpace(hashAlgo) && uploadedBytes > 0)
            {
                // Can't reconstruct incremental state without re-reading file; do a full hash of existing partial content.
                try
                {
                    hasher = hashAlgo == "sha1" ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using FileStream fs = new(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        hasher.AppendData(buffer, 0, read);
                    }
                }
                catch { hasher = null; }
            }
            ChunkUploadState state = new()
            {
                Id = uploadId,
                UserId = "anonymous", // Unknown after restart; treat quota via new requests only
                TempFilePath = tempPath,
                MetaFilePath = metaFile,
                TargetDirectoryFullPath = targetDir,
                FinalSafeName = finalSafeName,
                TotalSize = totalSize,
                UploadedBytes = uploadedBytes,
                HashAlgorithm = hashAlgo,
                ExpectedHash = expectedHash,
                Hasher = hasher,
                Paused = paused
            };
            _chunkStates[uploadId] = state;
            return Ok(new ChunkStatusResponse(state.Id, state.FinalSafeName, state.UploadedBytes, state.TotalSize, state.UploadedBytes == state.TotalSize, state.FinalHash, state.Paused));
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
        if (string.IsNullOrWhiteSpace(uploadId) || !_chunkStates.TryGetValue(uploadId, out ChunkUploadState? state))
        {
            return NotFound();
        }
        if (state.UploadedBytes == state.TotalSize)
        {
            // Already completed – treat as not pausable; return final status.
            return Ok(new ChunkStatusResponse(state.Id, state.FinalSafeName, state.UploadedBytes, state.TotalSize, true, state.FinalHash, false));
        }
        state.Paused = true;
        PersistChunkState(state, logger);
        return Ok(new ChunkStatusResponse(state.Id, state.FinalSafeName, state.UploadedBytes, state.TotalSize, false, state.FinalHash, true));
    }

    [HttpPost("chunk/{uploadId}/resume")]
    [ProducesResponseType(typeof(ChunkStatusResponse), 200)]
    [ProducesResponseType(404)]
    public ActionResult<ChunkStatusResponse> ResumeChunkUpload([FromRoute] string uploadId)
    {
        if (string.IsNullOrWhiteSpace(uploadId) || !_chunkStates.TryGetValue(uploadId, out ChunkUploadState? state))
        {
            return NotFound();
        }
        if (state.UploadedBytes == state.TotalSize)
        {
            return Ok(new ChunkStatusResponse(state.Id, state.FinalSafeName, state.UploadedBytes, state.TotalSize, true, state.FinalHash, false));
        }
        state.Paused = false;
        PersistChunkState(state, logger);
        return Ok(new ChunkStatusResponse(state.Id, state.FinalSafeName, state.UploadedBytes, state.TotalSize, false, state.FinalHash, false));
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
        if (_chunkStates.TryRemove(uploadId, out ChunkUploadState? state))
        {
            try
            {
                if (System.IO.File.Exists(state.TempFilePath))
                {
                    System.IO.File.Delete(state.TempFilePath);
                }
                if (System.IO.File.Exists(state.MetaFilePath))
                {
                    System.IO.File.Delete(state.MetaFilePath);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug($"Failed to delete temp file {state.TempFilePath}: {ex.Message}");
            }
        }
        return NoContent();
    }

    /// <summary>
    /// Deletes one or more files (directories are not supported currently).
    /// </summary>
    [HttpDelete]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(400)]
    public ActionResult Delete([FromBody] DeleteFilesRequest request, [FromQuery] bool recursive = false)
    {
        if (request?.FilePaths == null || request.FilePaths.Count == 0)
        {
            return BadRequest("filePaths is required");
        }

        (string? rootFullPath, string _, string _) = ResolveAndValidatePath("/");
        int deleted = 0;
        List<string> deletedFiles = new();
        List<string> skipped = new();
        List<string> failed = new();
        List<string> directoriesRequested = new();
        // Pre-scan to identify directories (unsupported) but defer decision to allow partial success semantics.
        foreach (string virtualPath in request.FilePaths)
        {
            try
            {
                (string _, string? fullCandidatePath, string _) = ResolveAndValidatePath(virtualPath, rootFullPathOverride: rootFullPath, treatAsFile: true);
                if (Directory.Exists(fullCandidatePath))
                {
                    directoriesRequested.Add(virtualPath);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug($"Validation failure while pre-scanning delete targets {virtualPath}: {ex.Message}");
                skipped.Add(virtualPath);
            }
        }
        if (directoriesRequested.Count == request.FilePaths.Count)
        {
            // Retain legacy behavior: if ONLY directories were requested treat as a hard failure.
            return BadRequest($"Cannot delete directories ({string.Join(", ", directoriesRequested)}) – directory deletion is not supported");
        }
        foreach (string virtualPath in request.FilePaths)
        {
            try
            {
                (string _, string? fullFilePath, string _) = ResolveAndValidatePath(virtualPath, rootFullPathOverride: rootFullPath, treatAsFile: true);
                if (Directory.Exists(fullFilePath))
                {
                    if (recursive)
                    {
                        try
                        {
                            Directory.Delete(fullFilePath, true);
                            deleted++;
                            deletedFiles.Add(virtualPath);
                        }
                        catch (Exception exDel)
                        {
                            logger.LogWarning($"Failed recursive delete for {virtualPath}: {exDel.Message}");
                            failed.Add(virtualPath);
                        }
                    }
                    else
                    {
                        failed.Add(virtualPath); // directories not supported unless recursive
                    }
                }
                else if (System.IO.File.Exists(fullFilePath))
                {
                    System.IO.File.Delete(fullFilePath);
                    deleted++;
                    deletedFiles.Add(virtualPath);
                }
                else
                {
                    skipped.Add(virtualPath);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Failed to delete file {virtualPath}: {ex.Message}");
                failed.Add(virtualPath);
            }
        }
        return Ok(new
        {
            requested = request.FilePaths,
            deleted,
            deletedFiles,
            skipped,
            failed = failed.Concat(directoriesRequested).Distinct().ToList(),
            totalRequested = request.FilePaths.Count,
            totalSucceeded = deleted,
            totalSkipped = skipped.Count,
            totalFailed = failed.Concat(directoriesRequested).Distinct().Count()
        });
    }

    /// <summary>
    /// Downloads a single file by virtual path.
    /// </summary>
    [HttpGet("download")]
    [HttpHead("download")]
    [ProducesResponseType(typeof(FileContentResult), 200)]
    [ProducesResponseType(304)]
    [ProducesResponseType(404)]
    public ActionResult Download([FromQuery] string path)
    {
        try
        {
            (string _, string? fullFilePath, string? virtualNorm) = ResolveAndValidatePath(path, treatAsFile: true);
            if (!System.IO.File.Exists(fullFilePath))
            {
                return NotFound();
            }

            System.IO.FileInfo info = new(fullFilePath);
            DateTime lastWriteUtc = info.LastWriteTimeUtc;
            // Allow opting into weak ETags via env var (set GCODE_WEAK_ETAGS=1) so upstream caches can do
            // semantic equivalence while still letting us change representation details later.
            bool useWeak = Environment.GetEnvironmentVariable("GCODE_WEAK_ETAGS") == "1";
            string etag = GenerateEtag(info, useWeak); // uniqueness: mtime + size (sufficient for local FS scenarios)

            // Conditional ETag handling
            RequestHeaders typedHeaders = Request.GetTypedHeaders();
            IList<EntityTagHeaderValue> ifNoneMatch = typedHeaders.IfNoneMatch;
            if (ifNoneMatch != null && ifNoneMatch.Any(t => string.Equals(t.Tag.ToString(), etag, StringComparison.Ordinal)))
            {
                Response.Headers["ETag"] = etag;
                Response.Headers["Last-Modified"] = lastWriteUtc.ToString("R", CultureInfo.InvariantCulture);
                return StatusCode(304);
            }
            DateTimeOffset? ifModifiedSince = typedHeaders.IfModifiedSince;
            if (ifModifiedSince.HasValue)
            {
                // Browsers (and HttpClient) serialize Last-Modified with second-level precision (RFC1123).
                // Our filesystem mtime (ticks) may have higher precision so a direct <= comparison can fail
                // even though no modification occurred. Allow a 1s tolerance window.
                DateTime ims = ifModifiedSince.Value.UtcDateTime;
                if (lastWriteUtc <= ims || (lastWriteUtc - ims) < TimeSpan.FromSeconds(1))
                {
                    Response.Headers["ETag"] = etag;
                    Response.Headers["Last-Modified"] = lastWriteUtc.ToString("R", CultureInfo.InvariantCulture);
                    return StatusCode(304);
                }
            }

            Response.Headers["ETag"] = etag;
            Response.Headers["Last-Modified"] = lastWriteUtc.ToString("R", CultureInfo.InvariantCulture);

            if (HttpContext.Request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            {
                // Provide accurate Content-Length without sending body (tests assert size now)
                Response.ContentLength = info.Length;
                return new StatusCodeResult(200);
            }

            byte[] bytes = System.IO.File.ReadAllBytes(fullFilePath);
            string fileName = Path.GetFileName(virtualNorm);
            return File(bytes, "application/octet-stream", fileName);
        }
        catch (Exception ex)
        {
            logger.LogError($"Error downloading G-code file {path}: {ex.Message}");
            return Problem("Failed to download file", statusCode: 500);
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
    public async Task<ActionResult<GcodeFileEntryDto>> UploadAsync([FromQuery] string? path = "/", [FromForm(Name = "file")] IFormFile? file = null)
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

        try
        {
            (string _, string? targetDirFullPath, string? virtualDir) = ResolveAndValidatePath(path);
            if (!Directory.Exists(targetDirFullPath))
            {
                Directory.CreateDirectory(targetDirFullPath);
            }

            // Sanitize filename (basic) - strip path separators
            string originalName = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(originalName))
            {
                originalName = "upload.gcode";
            }
            string safeName = originalName;
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                safeName = safeName.Replace(c, '_');
            }
            if (!safeName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                safeName += ext; // enforce extension if somehow dropped
            }

            string destinationPath = Path.Combine(targetDirFullPath, safeName);
            // Prevent escape (re-evaluate after combine) and enforce directory root
            string fullTarget = Path.GetFullPath(destinationPath);
            if (!fullTarget.StartsWith(targetDirFullPath, StringComparison.Ordinal))
            {
                return BadRequest("Unsafe target path");
            }

            // Collision handling: append (1), (2), ... before extension
            if (System.IO.File.Exists(fullTarget))
            {
                string baseName = Path.GetFileNameWithoutExtension(safeName);
                int counter = 1;
                string candidate;
                do
                {
                    candidate = baseName + " (" + counter++ + ")" + ext;
                    fullTarget = Path.GetFullPath(Path.Combine(targetDirFullPath, candidate));
                } while (System.IO.File.Exists(fullTarget));
                safeName = Path.GetFileName(fullTarget);
            }

            // Quota check before writing
            string userId = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anonymous";
            if (!quotaService.TryAddUsage(userId, file.Length, out long used, out long limit))
            {
                Response.Headers["X-Upload-Quota-Limit"] = limit.ToString(CultureInfo.InvariantCulture);
                Response.Headers["X-Upload-Quota-Used"] = used.ToString(CultureInfo.InvariantCulture);
                return StatusCode(429, $"Daily upload quota exceeded ({used}/{limit} bytes)");
            }
            await using FileStream fs = new(fullTarget, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await file.CopyToAsync(fs);

            System.IO.FileInfo info = new(fullTarget);
            string virtualFilePath = CombineVirtual(virtualDir, safeName);
            GcodeFileEntryDto dto = new(
                Path: virtualFilePath,
                Name: safeName,
                Size: info.Length,
                ModifiedAt: info.LastWriteTimeUtc,
                IsDirectory: false
            );
            // 201 Created with Location header (use listing endpoint path param representation)
            return Created($"/api/gcode-files?path={Uri.EscapeDataString(virtualDir)}", dto);
        }
        catch (Exception ex)
        {
            logger.LogError($"Error uploading G-code file (path={path}): {ex.Message}");
            return Problem("Failed to upload file", statusCode: 500);
        }
    }

    /// <summary>
    /// Upload multiple G-code files in a single multipart request. Each file is validated independently.
    /// </summary>
    /// <param name="path">Target virtual directory (default root '/').</param>
    /// <param name="files">Multipart form field 'files' (one or more).</param>
    [HttpPost("upload-multiple")]
    [RequestSizeLimit(500_000_000)] // 500 MB aggregate limit
    [ProducesResponseType(typeof(MultiUploadResponse), 201)]
    [ProducesResponseType(400)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Same safety guarantees as single upload; each filename sanitized and rooted under validated directory.")]
    public async Task<ActionResult<MultiUploadResponse>> UploadMultipleAsync([FromQuery] string? path = "/", [FromForm(Name = "files")] IFormFileCollection? files = null)
    {
        if (files == null || files.Count == 0)
        {
            return BadRequest("At least one file is required");
        }
        List<GcodeFileEntryDto> created = new();
        List<MultiUploadFailure> failed = new();
        try
        {
            (string _, string? targetDirFullPath, string? virtualDir) = ResolveAndValidatePath(path);
            if (!Directory.Exists(targetDirFullPath))
            {
                Directory.CreateDirectory(targetDirFullPath);
            }
            foreach (IFormFile? f in files)
            {
                try
                {
                    if (f == null || f.Length == 0)
                    {
                        failed.Add(new MultiUploadFailure(SafeOriginalName(f?.FileName), "Empty file"));
                        continue;
                    }
                    string ext = Path.GetExtension(f.FileName) ?? string.Empty;
                    if (!AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    {
                        failed.Add(new MultiUploadFailure(SafeOriginalName(f.FileName), $"Invalid file type '{ext}'"));
                        continue;
                    }
                    // Quota per file (aggregate effect). If exceeds, mark failed.
                    string userId = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anonymous";
                    if (!quotaService.TryAddUsage(userId, f.Length, out long used, out long limit))
                    {
                        failed.Add(new MultiUploadFailure(SafeOriginalName(f.FileName), $"Quota exceeded ({used}/{limit})"));
                        continue;
                    }
                    (string? fullTarget, string? safeName) = await SaveUploadedFileAsync(f, targetDirFullPath);
                    System.IO.FileInfo info = new(fullTarget);
                    string virtualFilePath = CombineVirtual(virtualDir, safeName);
                    created.Add(new GcodeFileEntryDto(
                        Path: virtualFilePath,
                        Name: safeName,
                        Size: info.Length,
                        ModifiedAt: info.LastWriteTimeUtc,
                        IsDirectory: false
                    ));
                }
                catch (Exception exFile)
                {
                    logger.LogWarning($"Failed to save uploaded file {f?.FileName}: {exFile.Message}");
                    failed.Add(new MultiUploadFailure(SafeOriginalName(f?.FileName), exFile.Message));
                }
            }
            MultiUploadResponse response = new(created, failed, created.Count, failed.Count);
            // 201 Created referencing directory listing location
            return Created($"/api/gcode-files?path={Uri.EscapeDataString(virtualDir)}", response);
        }
        catch (Exception ex)
        {
            logger.LogError($"Bulk upload failure (path={path}): {ex.Message}");
            return Problem("Failed to upload files", statusCode: 500);
        }
    }

    /// <summary>
    /// Create a new directory inside the virtual G-code library.
    /// </summary>
    /// <param name="path">Parent virtual directory (defaults to root '/').</param>
    /// <param name="name">Directory name (no path separators).</param>
    [HttpPost("mkdir")]
    [ProducesResponseType(typeof(GcodeFileEntryDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Directory name validated (invalid chars, separators removed) and combined under validated parent path; GetFullPath+StartsWith used prior to Directory.Exists.")]
    public ActionResult<GcodeFileEntryDto> MakeDirectory([FromQuery] string? path = "/", [FromQuery] string? name = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest("name is required");
        }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\n') || name.Contains('\r'))
        {
            return BadRequest("Invalid directory name");
        }
        try
        {
            (string _, string? parentDirFullPath, string? virtualParent) = ResolveAndValidatePath(path);
            if (!Directory.Exists(parentDirFullPath))
            {
                return NotFound("Parent directory does not exist");
            }
            string newDirFullPath = Path.GetFullPath(Path.Combine(parentDirFullPath, name));
            if (!newDirFullPath.StartsWith(parentDirFullPath, StringComparison.Ordinal))
            {
                return BadRequest("Unsafe directory target");
            }
            if (Directory.Exists(newDirFullPath))
            {
                return Conflict("Directory already exists");
            }
            Directory.CreateDirectory(newDirFullPath);
            GcodeFileEntryDto dto = new(
                Path: CombineVirtual(virtualParent, name),
                Name: name,
                Size: 0,
                ModifiedAt: Directory.GetLastWriteTimeUtc(newDirFullPath),
                IsDirectory: true
            );
            return Created($"/api/gcode-files?path={Uri.EscapeDataString(virtualParent)}", dto);
        }
        catch (Exception ex)
        {
            logger.LogError($"Failed to create directory (path={path}, name={name}): {ex.Message}");
            return Problem("Failed to create directory", statusCode: 500);
        }
    }

    // ------------------------------------------------------------
    // Helper models & utilities
    // ------------------------------------------------------------
    private static bool IsMatch(string name, string? search)
        => string.IsNullOrWhiteSpace(search) || name.Contains(search, StringComparison.OrdinalIgnoreCase);

    private (string rootFullPath, string resolvedFullPath, string virtualNormalized) ResolveAndValidatePath(
        string? virtualPath,
        string? rootFullPathOverride = null,
        bool treatAsFile = false)
    {
        // Allow explicit override via environment variable (useful for integration tests)
        string? envOverride = Environment.GetEnvironmentVariable("GCODE_LIBRARY_ROOT");
        string baseRoot;
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            baseRoot = Path.GetFullPath(envOverride);
        }
        else
        {
            baseRoot = env.WebRootPath;
        }

        // Fallback: if WebRootPath is null/empty (common in API-only container when no wwwroot copied)
        // use a local wwwroot under the content root (env.ContentRootPath) and ensure it exists so that
        // downstream Path.Combine / Directory.CreateDirectory calls do not throw ArgumentNullException.
        if (string.IsNullOrWhiteSpace(baseRoot))
        {
            baseRoot = Path.Combine(env.ContentRootPath, "wwwroot");
        }
        // Ensure the resolved base root exists (idempotent) so later code relying on its presence succeeds.
        Directory.CreateDirectory(baseRoot);
        string root = rootFullPathOverride ?? Path.GetFullPath(Path.Combine(baseRoot, "gcode-library"));
        Directory.CreateDirectory(root); // ensure exists

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

    private static string CombineVirtual(string baseVirtual, string childName)
    {
        if (baseVirtual == "/")
        {
            return "/" + childName;
        }
        return baseVirtual.TrimEnd('/') + "/" + childName;
    }
    private static string GenerateEtag(System.IO.FileInfo info, bool weak = false)
    {
        string core = $"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}";
        return weak ? $"W/\"{core}\"" : $"\"{core}\"";
    }

    private static string SafeOriginalName(string? name)
        => string.IsNullOrWhiteSpace(name) ? "(unnamed)" : Path.GetFileName(name);

    private static async Task<(string fullTargetPath, string safeName)> SaveUploadedFileAsync(IFormFile file, string targetDirFullPath)
    {
        string ext = Path.GetExtension(file.FileName) ?? string.Empty;
        string originalName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(originalName))
        {
            originalName = "upload" + ext;
        }
        string safeName = originalName;
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            safeName = safeName.Replace(c, '_');
        }
        if (!safeName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
        {
            safeName += ext;
        }
        string destinationPath = Path.Combine(targetDirFullPath, safeName);
        string fullTarget = Path.GetFullPath(destinationPath);
        if (!fullTarget.StartsWith(targetDirFullPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unsafe target path");
        }
        if (System.IO.File.Exists(fullTarget))
        {
            string baseName = Path.GetFileNameWithoutExtension(safeName);
            int counter = 1;
            string candidate = baseName + " (" + counter + ")" + ext;
            // CA3003: Path validated and generated by server logic, not user input
            do
            {
#pragma warning disable CA3003 // Review code for file path injection vulnerabilities
                fullTarget = Path.GetFullPath(Path.Combine(targetDirFullPath, candidate));
#pragma warning restore CA3003
                counter++;
                candidate = baseName + " (" + counter + ")" + ext;
            } while (System.IO.File.Exists(fullTarget));
            safeName = Path.GetFileName(fullTarget);
        }
        await using FileStream fs = new(fullTarget, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await file.CopyToAsync(fs);
        return (fullTarget, safeName);
    }

    private static void PersistChunkState(ChunkUploadState state, IUnifiedLoggingService logger)
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
            // CA3003: Path validated and generated by server logic, not user input
#pragma warning disable CA3003 // Review code for file path injection vulnerabilities
            System.IO.File.WriteAllText(state.MetaFilePath, json);
#pragma warning restore CA3003
        }
        catch (Exception ex)
        {
            logger.LogDebug($"Failed to persist chunk state {state.Id}: {ex.Message}");
        }
    }

    private static string ToHex(byte[] bytes)
    {
        char[] c = new char[bytes.Length * 2];
        int i = 0;
        foreach (byte b in bytes)
        {
            c[i++] = (char)(b >> 4 < 10 ? '0' + (b >> 4) : 'a' + (b >> 4) - 10);
            c[i++] = (char)((b & 0xF) < 10 ? '0' + (b & 0xF) : 'a' + (b & 0xF) - 10);
        }
        return new string(c);
    }

    // ---------------- Settings & Move endpoints ----------------
    [HttpGet("settings")]
    [ProducesResponseType(typeof(GcodeUploadSettingsResponse), 200)]
    public ActionResult<GcodeUploadSettingsResponse> GetSettings()
    {
        string userId = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anonymous";
        quotaService.TryAddUsage(userId, 0, out long used, out long limit); // peek usage
        return Ok(new GcodeUploadSettingsResponse(AllowedExtensions, limit, used));
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

    [HttpPost("move")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    public IActionResult Move([FromBody] MoveRequestDto request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.SourcePath) || string.IsNullOrWhiteSpace(request.DestinationPath))
        {
            return BadRequest("sourcePath and destinationPath required");
        }
        try
        {
            (string? root, string? sourceFull, string _) = ResolveAndValidatePath(request.SourcePath, treatAsFile: true);
            (string _, string? destFull, string? destVirtual) = ResolveAndValidatePath(request.DestinationPath, rootFullPathOverride: root, treatAsFile: true);
            if (!System.IO.File.Exists(sourceFull) && !Directory.Exists(sourceFull))
            {
                return NotFound("Source not found");
            }
            bool isDirectory = Directory.Exists(sourceFull);
            bool destExistsFile = System.IO.File.Exists(destFull);
            bool destExistsDir = Directory.Exists(destFull);
            if ((destExistsFile || destExistsDir) && !request.Overwrite)
            {
                return Conflict("Destination already exists");
            }
            if (destExistsFile)
            {
                System.IO.File.Delete(destFull);
            }
            if (destExistsDir && !isDirectory)
            {
                return Conflict("Destination directory exists");
            }
            if (isDirectory)
            {
                // Use Directory.Move (cannot overwrite) -> implement manual copy+delete if overwrite needed
                if (destExistsDir)
                {
                    return Conflict("Destination directory exists (cannot overwrite)");
                }
                Directory.Move(sourceFull, destFull);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
                System.IO.File.Move(sourceFull, destFull, overwrite: request.Overwrite);
            }
            return Ok(new { path = destVirtual, isDirectory });
        }
        catch (Exception ex)
        {
            logger.LogError($"Move failed {request.SourcePath} -> {request.DestinationPath}: {ex.Message}");
            return Problem("Failed to move", statusCode: 500);
        }
    }
}

// ---------------- Additional DTOs for move & settings ----------------
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
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("modifiedAt")] DateTime ModifiedAt,
    [property: JsonPropertyName("isDirectory")] bool IsDirectory,
    [property: JsonPropertyName("harvestOperationId")] Guid? HarvestOperationId = null
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
    [property: JsonPropertyName("totalItems")] int TotalItems
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
    [JsonPropertyName("filePaths")] public IList<string> FilePaths { get; init; } = Array.Empty<string>();
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
    [property: JsonPropertyName("paused")] bool Paused = false
);

public sealed record GcodeFileHashResponse(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("algorithm")] string Algorithm,
    [property: JsonPropertyName("hash")] string Hash
);
