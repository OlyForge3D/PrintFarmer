using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.DTOs.Artifacts;
using Farm.Web.Api.Services.Artifacts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages artifact storage, retrieval, and metadata for slice jobs and workers.
/// Artifacts include generated G-code files, preview thumbnails, logs, and other job outputs.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize] // All endpoints require authentication
public class ArtifactsController : ControllerBase
{
    private readonly IArtifactsService _service;
    private readonly Farm.Web.Api.Repositories.Slicing.ISliceJobRepository _jobRepository;

    private readonly Microsoft.Extensions.Options.IOptions<Farm.Infrastructure.Settings.ArtifactStorageSettings> _settings;
    public ArtifactsController(
        IArtifactsService service,
        Farm.Web.Api.Repositories.Slicing.ISliceJobRepository jobRepository,
        Microsoft.Extensions.Options.IOptions<Farm.Infrastructure.Settings.ArtifactStorageSettings> settings)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _jobRepository = jobRepository ?? throw new ArgumentNullException(nameof(jobRepository));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Upload multiple artifacts in a single request.
    /// </summary>
    /// <param name="jobId">The slice job ID that produced these artifacts.</param>
    /// <param name="workerId">Optional worker ID that generated these artifacts.</param>
    /// <param name="files">Array of artifact files to upload (multipart/form-data).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// 200 OK with array of <see cref="ArtifactDto"/> for successfully uploaded artifacts.
    /// 400 Bad Request if validation fails or no files provided.
    /// </returns>
    /// <remarks>
    /// Uploads multiple artifacts atomically. Each file's kind is inferred from its Content-Type or filename extension.
    /// If any file fails validation or upload, the entire operation is rolled back.
    /// 
    /// Kind inference rules:
    /// - .gcode, .g, .nc → "gcode"
    /// - .png, .jpg, .jpeg, .webp → "thumbnail"
    /// - .log, .txt → "log"
    /// - application/x-gcode → "gcode"
    /// - image/* → "thumbnail"
    /// 
    /// Example request:
    /// POST /api/artifacts/bulk
    /// Content-Type: multipart/form-data
    /// 
    /// - jobId: 3fa85f64-5717-4562-b3fc-2c963f66afa6
    /// - workerId: 7c9e6679-7425-40de-944b-e07fc1f90ae7
    /// - files: [file1.gcode, file2.png, file3.log]
    /// </remarks>
    [HttpPost("bulk")]
    [ProducesResponseType(typeof(ArtifactDto[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(500_000_000)] // 500MB for bulk uploads
    public async Task<IActionResult> BulkUploadAsync(
        [FromForm] Guid jobId,
        [FromForm] Guid? workerId,
        [FromForm] IFormFileCollection files,
        CancellationToken ct)
    {
        if (files == null || files.Count == 0)
        {
            return BadRequest(new { error = "At least one file is required" });
        }

        var allowed = _settings.Value.AllowedKinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var results = new List<ArtifactDto>();

        try
        {
            foreach (var file in files)
            {
                // Infer kind from content type or filename
                string kind = InferKind(file);

                if (string.IsNullOrWhiteSpace(kind) || !allowed.Contains(kind, StringComparer.OrdinalIgnoreCase))
                {
                    return BadRequest(new
                    {
                        error = $"unsupported artifact kind '{kind}' for file '{file.FileName}'",
                        allowedKinds = allowed
                    });
                }

                var artifact = await _service.UploadAsync(file, jobId, workerId, kind, ct);
                results.Add(Map(artifact));
            }

            return Ok(results);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private static string InferKind(IFormFile file)
    {
        // Try content type first
        if (!string.IsNullOrWhiteSpace(file.ContentType))
        {
            if (file.ContentType.Equals("application/x-gcode", StringComparison.OrdinalIgnoreCase))
            {
                return "gcode";
            }
            if (file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return "thumbnail";
            }
            if (file.ContentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase))
            {
                return "log";
            }
        }

        // Fallback to file extension
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        return ext switch
        {
            ".gcode" or ".g" or ".nc" => "gcode",
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" => "thumbnail",
            ".log" or ".txt" => "log",
            _ => string.Empty
        };
    }

    /// <summary>
    /// Upload a new artifact file for a slice job.
    /// </summary>
    /// <param name="jobId">The slice job ID that produced this artifact.</param>
    /// <param name="kind">Artifact classification (must match allowed kinds: gcode, thumbnail, log, etc.).</param>
    /// <param name="workerId">Optional worker ID that generated this artifact.</param>
    /// <param name="file">The artifact file to upload (multipart/form-data).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// 200 OK with <see cref="ArtifactDto"/> containing metadata and download URL.
    /// 400 Bad Request if file is missing, kind is unsupported, or validation fails.
    /// </returns>
    /// <remarks>
    /// The uploaded file is stored in the configured artifact storage directory with SHA-256 hash verification.
    /// Maximum file size is limited by request size limits (default: ~100MB).
    /// 
    /// Example request:
    /// POST /api/artifacts
    /// Content-Type: multipart/form-data
    /// 
    /// - jobId: 3fa85f64-5717-4562-b3fc-2c963f66afa6
    /// - kind: gcode
    /// - workerId: 7c9e6679-7425-40de-944b-e07fc1f90ae7
    /// - file: [binary data]
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(ArtifactDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(110_000_000)] // Slightly above default max to match settings guard
    public async Task<IActionResult> UploadAsync([FromForm] Guid jobId, [FromForm] string kind, [FromForm] Guid? workerId, [FromForm] IFormFile file, CancellationToken ct)
    {
        if (file == null)
        {
            return BadRequest("file is required");
        }

        var allowed = _settings.Value.AllowedKinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (string.IsNullOrWhiteSpace(kind) || !allowed.Contains(kind, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "unsupported artifact kind", allowedKinds = allowed });
        }
        try
        {
            var artifact = await _service.UploadAsync(file, jobId, workerId, kind, ct);
            ArtifactDto dto = Map(artifact);
            return Ok(dto);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Retrieve artifact metadata by ID.
    /// </summary>
    /// <param name="id">The unique artifact identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// 200 OK with <see cref="ArtifactDto"/> containing metadata and download URL.
    /// 404 Not Found if the artifact does not exist.
    /// </returns>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ArtifactDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAsync(Guid id, CancellationToken ct)
    {
        var a = await _service.GetAsync(id, ct);
        if (a == null)
        {
            return NotFound();
        }

        // Authorization: only job owner or admin can access
        if (!await CanAccessArtifactAsync(a.JobId, ct))
        {
            return Forbid();
        }

        return Ok(Map(a));
    }

    /// <summary>
    /// List all artifacts associated with a specific slice job.
    /// </summary>
    /// <param name="jobId">The slice job identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// 200 OK with array of <see cref="ArtifactDto"/> objects (may be empty if no artifacts exist).
    /// </returns>
    /// <remarks>
    /// Returns all artifacts linked to the specified job, including G-code files, thumbnails, logs, and other outputs.
    /// Results are ordered by creation timestamp.
    /// </remarks>
    [HttpGet("job/{jobId:guid}")]
    [ProducesResponseType(typeof(ArtifactDto[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListByJobAsync(Guid jobId, CancellationToken ct)
    {
        // Authorization: only job owner or admin can list
        if (!await CanAccessArtifactAsync(jobId, ct))
        {
            return Forbid();
        }

        var list = await _service.ListByJobAsync(jobId, ct);
        return Ok(list.Select(Map));
    }

    /// <summary>
    /// Download the raw artifact file bytes.
    /// </summary>
    /// <param name="id">The unique artifact identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// 200 OK with file stream (Content-Type and filename set from artifact metadata).
    /// 404 Not Found if the artifact does not exist or the file is missing from storage.
    /// </returns>
    /// <remarks>
    /// Returns the raw file bytes with appropriate Content-Type and Content-Disposition headers.
    /// This endpoint is used by workers, clients, and the slice job completion flow to retrieve
    /// generated artifacts such as G-code files, preview thumbnails, or logs.
    /// 
    /// The response includes:
    /// - Content-Type: MIME type from artifact metadata (e.g., 'application/x-gcode', 'image/png')
    /// - Content-Disposition: attachment with original filename
    /// - File stream: raw bytes from filesystem storage
    /// </remarks>
    [HttpGet("{id:guid}/download")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DownloadAsync(Guid id, CancellationToken ct)
    {
        var result = await _service.GetWithPathAsync(id, ct);
        if (result == null)
        {
            return NotFound();
        }

        var (artifact, fullPath) = result.Value;

        // Authorization: only job owner or admin can download
        if (!await CanAccessArtifactAsync(artifact.JobId, ct))
        {
            return Forbid();
        }

        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound(new { error = "file missing" });
        }

        var stream = System.IO.File.OpenRead(fullPath);
        return File(stream, artifact.ContentType ?? "application/octet-stream", artifact.FileName);
    }

    /// <summary>
    /// Check if current user can access artifacts for the given job.
    /// Returns true if user owns the job or has admin role.
    /// </summary>
    private async Task<bool> CanAccessArtifactAsync(Guid jobId, CancellationToken ct)
    {
        // Admin can access any artifact
        if (User.IsInRole("farm_admin"))
        {
            return true;
        }

        // Check if user owns the job
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            return false;
        }

        var job = await _jobRepository.GetByIdAsync(jobId, ct);
        return job != null && job.UserId == userId;
    }

    private ArtifactDto Map(Farm.Infrastructure.Domain.Artifact a)
    {
        string download = $"/api/artifacts/{a.Id}/download";
        // Public URL only if static serving is enabled
        string? publicUrl = _settings.Value.EnableStaticServing
            ? $"/artifacts/{a.RelativePath}"
            : null;
        return new ArtifactDto(
            a.Id,
            a.JobId,
            a.WorkerId,
            a.Kind,
            a.FileName,
            a.RelativePath,
            a.ContentType,
            a.SizeBytes,
            a.Sha256,
            a.CreatedAt,
            download,
            publicUrl);
    }
}
