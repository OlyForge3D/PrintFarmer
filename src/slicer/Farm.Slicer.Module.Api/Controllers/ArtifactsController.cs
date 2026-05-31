using System.Security.Claims;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Farm.Slicer.Module.Api.Controllers;

/// <summary>
/// API endpoints for managing slice job artifacts.
/// </summary>
[ApiController]
[Route("api/artifacts")]
[Authorize]
public class ArtifactsController(
    IArtifactsService service,
    ISliceJobRepository jobRepository,
    IOptions<SlicerArtifactStorageSettings> settings) : ControllerBase
{
    private readonly IArtifactsService _service = service;
    private readonly ISliceJobRepository _jobRepository = jobRepository;
    private readonly SlicerArtifactStorageSettings _settings = settings.Value;

    /// <summary>
    /// Uploads an artifact for a slice job.
    /// </summary>
    /// <param name="jobId">The slice job ID.</param>
    /// <param name="file">The uploaded file.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{jobId}")]
    public async Task<IActionResult> UploadAsync(Guid jobId, IFormFile file, CancellationToken ct)
    {
        if (file.Length == 0)
        {
            return BadRequest(new { error = "File is empty." });
        }

        if (file.Length > _settings.MaxFileSizeBytes)
        {
            return BadRequest(new { error = $"File exceeds maximum size of {_settings.MaxFileSizeBytes} bytes." });
        }

        SliceJob? job = await _jobRepository.GetByIdAsync(jobId, ct);
        if (job is null)
        {
            return NotFound(new { error = "Slice job not found." });
        }

        Artifact artifact = await _service.UploadAsync(file, jobId, null, "gcode", ct);
        return Created($"/api/artifacts/{artifact.Id}", new
        {
            id = artifact.Id,
            jobId = artifact.JobId,
            fileName = artifact.FileName,
            contentType = artifact.ContentType,
            sizeBytes = artifact.SizeBytes,
            createdAt = artifact.CreatedAt,
        });
    }

    /// <summary>
    /// Gets an artifact by ID.
    /// </summary>
    /// <param name="id">The artifact ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetAsync(Guid id, CancellationToken ct)
    {
        var result = await _service.GetWithPathAsync(id, ct);
        if (result is null)
        {
            return NotFound();
        }

        var (artifact, filePath) = result.Value;
        return PhysicalFile(filePath, artifact.ContentType ?? "application/octet-stream", artifact.FileName);
    }

    /// <summary>
    /// Lists artifacts for a slice job.
    /// </summary>
    /// <param name="jobId">The slice job ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("job/{jobId}")]
    public async Task<IActionResult> ListByJobAsync(Guid jobId, CancellationToken ct)
    {
        IReadOnlyList<Artifact> artifacts = await _service.ListByJobAsync(jobId, ct);
        var response = artifacts.Select(a => new
        {
            id = a.Id,
            jobId = a.JobId,
            fileName = a.FileName,
            contentType = a.ContentType,
            sizeBytes = a.SizeBytes,
            createdAt = a.CreatedAt,
        }).ToList();

        return Ok(response);
    }

    /// <summary>
    /// Gets artifact metadata by ID including the download URL.
    /// </summary>
    /// <param name="id">The artifact ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{id}/metadata")]
    [ProducesResponseType(typeof(ArtifactMetadataDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetMetadataAsync(Guid id, CancellationToken ct)
    {
        Artifact? artifact = await _service.GetAsync(id, ct);
        if (artifact is null)
        {
            return NotFound();
        }

        SliceJob? job = await _jobRepository.GetByIdAsync(artifact.JobId, ct);
        if (job is null)
        {
            return NotFound();
        }

        string? callerIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        bool isAdmin = User.IsInRole("farm_admin");
        if (!isAdmin && (!Guid.TryParse(callerIdStr, out Guid callerId) || job.UserId != callerId))
        {
            return Forbid();
        }

        string downloadUrl = $"/api/artifacts/{artifact.Id}";
        return Ok(new ArtifactMetadataDto(
            artifact.Id,
            artifact.FileName,
            artifact.ContentType,
            artifact.SizeBytes,
            downloadUrl,
            artifact.CreatedAt,
            artifact.JobId));
    }
}
