using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Api.Authorization;
using Farm.Slicer.Module.Api.Filters;
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
    IOptions<SlicerArtifactStorageSettings> settings,
    ISlicerResourceAccessAuthorizer? resourceAccess = null) : ControllerBase
{
    private readonly IArtifactsService _service = service;
    private readonly ISliceJobRepository _jobRepository = jobRepository;
    private readonly SlicerArtifactStorageSettings _settings = settings.Value;
    private readonly ISlicerResourceAccessAuthorizer? _resourceAccess = resourceAccess;

    /// <summary>
    /// Uploads an artifact for a slice job.
    /// </summary>
    /// <param name="jobId">The slice job ID.</param>
    /// <param name="file">The uploaded file.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{jobId}")]
    [RequirePermission(PrintFarmerPermissions.Slicing.Submit)]
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

        if (!CanAccess(job))
        {
            return SlicerApiProblems.ResourceForbidden(this);
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
    [RequirePermission(PrintFarmerPermissions.Slicing.ReadArtifact)]
    [ProducesResponseType(typeof(IReadOnlyList<ArtifactListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAsync(Guid id, CancellationToken ct)
    {
        var result = await _service.GetWithPathAsync(id, ct);
        if (result is null)
        {
            return NotFound();
        }

        var (artifact, filePath) = result.Value;

        SliceJob? job = await _jobRepository.GetByIdAsync(artifact.JobId, ct);
        if (job is null)
        {
            return SlicerApiProblems.ResourceNotFound(this);
        }

        if (!CanAccess(job))
        {
            return SlicerApiProblems.ResourceForbidden(this);
        }

        return PhysicalFile(filePath, artifact.ContentType ?? "application/octet-stream", artifact.FileName);
    }

    /// <summary>
    /// Lists artifacts for a slice job.
    /// </summary>
    /// <param name="jobId">The slice job ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("job/{jobId}")]
    [RequirePermission(PrintFarmerPermissions.Slicing.ReadArtifact)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListByJobAsync(Guid jobId, CancellationToken ct)
    {
        SliceJob? job = await _jobRepository.GetByIdAsync(jobId, ct);
        if (job is null)
        {
            return SlicerApiProblems.ResourceNotFound(this);
        }

        if (!CanAccess(job))
        {
            return SlicerApiProblems.ResourceForbidden(this);
        }

        IReadOnlyList<Artifact> artifacts = await _service.ListByJobAsync(jobId, ct);
        Guid? primaryArtifactId = TryGetPrimaryArtifactId(job.ResultFileUrl);
        bool hasValidPrimaryGcode = primaryArtifactId.HasValue &&
            artifacts.Count(artifact =>
                artifact.Id == primaryArtifactId.Value &&
                string.Equals(artifact.Kind, SlicerArtifactKinds.Gcode, StringComparison.OrdinalIgnoreCase)) == 1;
        List<ArtifactListItemDto> response = artifacts.Select(artifact => new ArtifactListItemDto(
            artifact.Id,
            artifact.JobId,
            artifact.FileName,
            artifact.ContentType,
            artifact.SizeBytes,
            $"/api/artifacts/{artifact.Id}",
            artifact.CreatedAt,
            hasValidPrimaryGcode && artifact.Id == primaryArtifactId)).ToList();

        return Ok(response);
    }

    private static Guid? TryGetPrimaryArtifactId(string? resultFileUrl)
    {
        if (string.IsNullOrWhiteSpace(resultFileUrl))
        {
            return null;
        }

        string path = Uri.TryCreate(resultFileUrl, UriKind.Absolute, out Uri? absoluteUri)
            ? absoluteUri.AbsolutePath
            : resultFileUrl.Split(['?', '#'], 2)[0];
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < segments.Length - 1; index++)
        {
            if (string.Equals(segments[index], "artifacts", StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParse(segments[index + 1], out Guid artifactId))
            {
                return artifactId;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets artifact metadata by ID including the download URL.
    /// </summary>
    /// <param name="id">The artifact ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{id}/metadata")]
    [RequirePermission(PrintFarmerPermissions.Slicing.ReadArtifact)]
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
            return SlicerApiProblems.ResourceNotFound(this);
        }

        if (!CanAccess(job))
        {
            return SlicerApiProblems.ResourceForbidden(this);
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

    private bool CanAccess(SliceJob job)
    {
        if (_resourceAccess is not null)
        {
            return _resourceAccess.CanAccess(User, job.UserId, "slice-job-artifact", job.Id);
        }

        return PrintFarmerPermissions.IsFarmAdmin(User) ||
               (PrintFarmerPermissions.TryGetUserId(User, out Guid userId) &&
                userId == job.UserId);
    }
}
