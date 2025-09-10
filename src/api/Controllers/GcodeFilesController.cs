using System.Globalization;
using System.Text.Json.Serialization;
using Farm.Web.Api.Data;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides a lightweight virtual file browser over the server-side G-code library directory (wwwroot/gcode-library).
/// This controller exists primarily to satisfy the React FileBrowser component which expects:
///   GET    /api/gcode-files                (query: path, sortBy, sortOrder, search, harvestId, printerId)
///   DELETE /api/gcode-files                (body: { filePaths: string[] })
///   GET    /api/gcode-files/download       (query: path)
/// The underlying persistent metadata & deduplicated storage is still handled by GcodeLibraryController / database.
/// We intentionally do NOT expose recursive listing – only the immediate children of the requested path.
/// </summary>
[ApiController]
[Route("api/gcode-files")]
public class GcodeFilesController(IWebHostEnvironment env, ILogger<GcodeFilesController> logger, AppDbContext db) : ControllerBase
{

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
        [FromQuery] Guid? harvestId = null, // presently unused – placeholder for future DB correlation
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
            var (_, requestedDirFullPath, virtualPathNormalized) = ResolveAndValidatePath(path);
            if (!Directory.Exists(requestedDirFullPath))
            {
                return NotFound($"Directory '{virtualPathNormalized}' not found");
            }

            // Gather directory & file entries (only immediate children)
            var dirInfo = new DirectoryInfo(requestedDirFullPath);
            var entries = new List<GcodeFileEntryDto>();

            // Directories
            foreach (var dir in dirInfo.EnumerateDirectories())
            {
                if (dir.Name.StartsWith('.'))
                {
                    continue; // skip hidden
                }
                if (!IsMatch(dir.Name, search))
                {
                    continue;
                }
                var childVirtual = CombineVirtual(virtualPathNormalized, dir.Name);
                entries.Add(new GcodeFileEntryDto(
                    Path: childVirtual,
                    Name: dir.Name,
                    Size: 0,
                    ModifiedAt: dir.LastWriteTimeUtc,
                    IsDirectory: true
                ));
            }

            // Files (only .gcode for now)
            foreach (var file in dirInfo.EnumerateFiles("*.gcode"))
            {
                if (!IsMatch(file.Name, search))
                {
                    continue;
                }
                var childVirtual = CombineVirtual(virtualPathNormalized, file.Name);

                // Attempt to correlate with DB entry for potential future harvest association.
                Guid? harvestOpId = null;
                try
                {
                    var dbEntry = db.GcodeFiles.FirstOrDefault(g => g.FilePath == file.FullName);
                    if (dbEntry != null && dbEntry.SourcePrinterId != null)
                    {
                        // If this file originated from a harvest, attempt to find last harvest op for that printer containing this original name.
                        var op = db.GcodeHarvestOperations
                            .Where(o => o.PrinterId == dbEntry.SourcePrinterId)
                            .OrderByDescending(o => o.StartedAt)
                            .FirstOrDefault();
                        harvestOpId = op?.Id;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Non-fatal DB correlation failure for file {File}", file.FullName);
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

            // Sorting
            entries = (sortBy?.ToLowerInvariant(), sortOrder?.ToLowerInvariant()) switch
            {
                ("size", "desc") => [.. entries.OrderByDescending(e => e.IsDirectory).ThenByDescending(e => e.Size).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)],
                ("size", _) => [.. entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Size).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)],
                ("date", "desc") => [.. entries.OrderByDescending(e => e.IsDirectory).ThenByDescending(e => e.ModifiedAt).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)],
                ("date", _) => [.. entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.ModifiedAt).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)],
                ("name", "desc") => [.. entries.OrderByDescending(e => e.IsDirectory).ThenByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase)],
                _ => [.. entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)]
            };

            var totalFiles = entries.Count(e => !e.IsDirectory);
            var totalSize = entries.Where(e => !e.IsDirectory).Sum(e => e.Size);

            // Apply pagination AFTER computing totals so client can derive total pages.
            var skip = (page - 1) * pageSize;
            var pagedEntries = skip >= entries.Count ? new List<GcodeFileEntryDto>(0) : [.. entries.Skip(skip).Take(pageSize)];
            var totalItems = entries.Count; // directories + files for pagination context
            var totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
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
            logger.LogError(ex, "Error listing G-code files (path={Path})", path);
            return Problem("Failed to retrieve files", statusCode: 500);
        }
    }

    /// <summary>
    /// Deletes one or more files (directories are not supported currently).
    /// </summary>
    [HttpDelete]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(400)]
    public ActionResult Delete([FromBody] DeleteFilesRequest request)
    {
        if (request?.FilePaths == null || request.FilePaths.Count == 0)
        {
            return BadRequest("filePaths is required");
        }

        var (rootFullPath, _, _) = ResolveAndValidatePath("/");
    int deleted = 0;
    var deletedFiles = new List<string>();
    var skipped = new List<string>();
    var failed = new List<string>();
        var directoriesRequested = new List<string>();
        // Pre-scan to identify directories (unsupported) but defer decision to allow partial success semantics.
        foreach (var virtualPath in request.FilePaths)
        {
            try
            {
                var (_, fullCandidatePath, _) = ResolveAndValidatePath(virtualPath, rootFullPathOverride: rootFullPath, treatAsFile: true);
                if (Directory.Exists(fullCandidatePath))
                {
                    directoriesRequested.Add(virtualPath);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Validation failure while pre-scanning delete targets {Path}", virtualPath);
                skipped.Add(virtualPath);
            }
        }
        if (directoriesRequested.Count == request.FilePaths.Count)
        {
            // Retain legacy behavior: if ONLY directories were requested treat as a hard failure.
            return BadRequest($"Cannot delete directories ({string.Join(", ", directoriesRequested)}) – directory deletion is not supported");
        }
        foreach (var virtualPath in request.FilePaths)
        {
            try
            {
                var (_, fullFilePath, _) = ResolveAndValidatePath(virtualPath, rootFullPathOverride: rootFullPath, treatAsFile: true);
                if (Directory.Exists(fullFilePath))
                {
                    failed.Add(virtualPath); // directories not supported in mixed request
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
                logger.LogWarning(ex, "Failed to delete file {Path}", virtualPath);
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
            var (_, fullFilePath, virtualNorm) = ResolveAndValidatePath(path, treatAsFile: true);
            if (!System.IO.File.Exists(fullFilePath))
            {
                return NotFound();
            }

            var info = new FileInfo(fullFilePath);
            var lastWriteUtc = info.LastWriteTimeUtc;
            // Allow opting into weak ETags via env var (set GCODE_WEAK_ETAGS=1) so upstream caches can do
            // semantic equivalence while still letting us change representation details later.
            var useWeak = Environment.GetEnvironmentVariable("GCODE_WEAK_ETAGS") == "1";
            var etag = GenerateEtag(info, useWeak); // uniqueness: mtime + size (sufficient for local FS scenarios)

            // Conditional ETag handling
            var typedHeaders = Request.GetTypedHeaders();
            var ifNoneMatch = typedHeaders.IfNoneMatch;
            if (ifNoneMatch != null && ifNoneMatch.Any(t => string.Equals(t.Tag.ToString(), etag, StringComparison.Ordinal)))
            {
                Response.Headers["ETag"] = etag;
                Response.Headers["Last-Modified"] = lastWriteUtc.ToString("R", CultureInfo.InvariantCulture);
                return StatusCode(304);
            }
            var ifModifiedSince = typedHeaders.IfModifiedSince;
            if (ifModifiedSince.HasValue)
            {
                // Browsers (and HttpClient) serialize Last-Modified with second-level precision (RFC1123).
                // Our filesystem mtime (ticks) may have higher precision so a direct <= comparison can fail
                // even though no modification occurred. Allow a 1s tolerance window.
                var ims = ifModifiedSince.Value.UtcDateTime;
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

            var bytes = System.IO.File.ReadAllBytes(fullFilePath);
            var fileName = Path.GetFileName(virtualNorm);
            return File(bytes, "application/octet-stream", fileName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error downloading G-code file {Path}", path);
            return Problem("Failed to download file", statusCode: 500);
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
        var envOverride = Environment.GetEnvironmentVariable("GCODE_LIBRARY_ROOT");
        string baseRoot;
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            baseRoot = Path.GetFullPath(envOverride);
        }
        else
        {
            baseRoot = env.WebRootPath;
        }
        var root = rootFullPathOverride ?? Path.GetFullPath(Path.Combine(baseRoot, "gcode-library"));
        Directory.CreateDirectory(root); // ensure exists

        // Normalize incoming virtual path
        var vPath = string.IsNullOrWhiteSpace(virtualPath) ? "/" : virtualPath.Trim();
        if (!vPath.StartsWith('/'))
        {
            vPath = "/" + vPath;
        }
        // Collapse .. segments
        var segments = vPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != "." && s != "..")
            .ToArray();
        var safeRel = segments.Length == 0 ? string.Empty : Path.Combine(segments);
        var candidate = Path.GetFullPath(Path.Combine(root, safeRel));
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
    private static string GenerateEtag(FileInfo info, bool weak = false)
    {
        var core = $"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}";
        return weak ? $"W/\"{core}\"" : $"\"{core}\"";
    }
}

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

/// <summary>Request body for bulk deletion of virtual G-code files.</summary>
public sealed class DeleteFilesRequest
{
    [JsonPropertyName("filePaths")] public IList<string> FilePaths { get; init; } = [];
}
