using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Api.Authorization;
using Farm.Slicer.Module.Api.Filters;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Slicer.Module.Api.Controllers.Slicing;

/// <summary>
/// Legacy slicing jobs controller (pre-queue system).
/// Provides endpoints for direct slicing job management via the orchestrator.
/// </summary>
/// <remarks>
/// Superseded by <c>POST /api/slice</c>. Retained for existing non-calibration callers only; the
/// in-memory job store behind it is not durable and is never used for calibration work.
/// </remarks>
[ApiController]
[Route("api/slicer")]
[Tags("Slicer Jobs")]
[DeprecatedSliceRoute(
    SlicingSubmissionController.CanonicalSliceRoute,
    SlicingSubmissionController.CanonicalSliceRouteSunset)]
public class SlicingJobsController(
    ISlicerTempPathProvider tempPathProvider,
    ISlicerOrchestrator orchestrator,
    ISlicerResourceAccessAuthorizer? resourceAccess = null,
    IPrinterAccessValidator? printerAccess = null) : ControllerBase
{
    private readonly ISlicerTempPathProvider _tempPathProvider = tempPathProvider;
    private readonly ISlicerOrchestrator _orchestrator = orchestrator;
    private readonly ISlicerResourceAccessAuthorizer? _resourceAccess = resourceAccess;
    private readonly IPrinterAccessValidator? _printerAccess = printerAccess;

    /// <summary>
    /// Submits a new slicing job.
    /// </summary>
    /// <param name="file">The model file to slice.</param>
    /// <param name="printerId">Optional enabled printer that will use the generated G-code.</param>
    /// <param name="profileJson">Slicer profile configuration JSON.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("jobs")]
    [Authorize]
    [RequirePermission(PrintFarmerPermissions.Slicing.Submit)]
    public async Task<IActionResult> SubmitAsync(
        IFormFile file,
        [FromForm] Guid? printerId,
        [FromForm] string? profileJson,
        CancellationToken ct)
    {
        if (file.Length == 0)
        {
            return BadRequest(new { error = "File is empty." });
        }

        if (!PrintFarmerPermissions.TryGetUserId(User, out Guid userId) ||
            (_printerAccess is not null &&
             !await _printerAccess.IsEnabledAsync(printerId, ct)))
        {
            return SlicerApiProblems.ResourceForbidden(this);
        }

        // Store the uploaded file
        string tempPath = _tempPathProvider.GetTempFilePath(Path.GetExtension(file.FileName));
        await using (var stream = new FileStream(tempPath, FileMode.Create))
        {
            await file.CopyToAsync(stream, ct);
        }

        SlicerProfileDto? profile = null;
        if (!string.IsNullOrWhiteSpace(profileJson))
        {
            profile = System.Text.Json.JsonSerializer.Deserialize<SlicerProfileDto>(profileJson);
        }

        var request = new SlicingJobRequest
        {
            ModelFileUrl = new Uri(tempPath),
            ModelFileName = file.FileName,
            PrinterId = printerId ?? Guid.Empty,
            UserId = userId,
            SlicerProfile = profile ?? new SlicerProfileDto(),
        };

        SlicingJobResponse response = await _orchestrator.SubmitJobAsync(request, ct);

        return Ok(new
        {
            jobId = response.JobId,
            status = response.Status.ToString(),
        });
    }

    /// <summary>
    /// Gets all slicing jobs.
    /// </summary>
    [HttpGet("jobs")]
    [Authorize]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    public IActionResult List()
    {
        IEnumerable<SlicingJobDto> jobs = SlicingJobStore.GetAll();
        if (!PrintFarmerPermissions.IsFarmAdmin(User))
        {
            if (!PrintFarmerPermissions.TryGetUserId(User, out Guid userId))
            {
                return SlicerApiProblems.ResourceForbidden(this);
            }

            jobs = jobs.Where(job => job.UserId == userId);
        }

        return Ok(jobs.Select(MapPublicJob));
    }

    /// <summary>
    /// Gets a specific slicing job by ID.
    /// </summary>
    /// <param name="id">The job ID.</param>
    [HttpGet("jobs/{id}")]
    [Authorize]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    public IActionResult Get(Guid id)
    {
        SlicingJobDto? job = SlicingJobStore.Get(id);
        if (job is null)
        {
            return SlicerApiProblems.ResourceNotFound(this);
        }

        return CanAccess(job)
            ? Ok(MapPublicJob(job))
            : SlicerApiProblems.ResourceForbidden(this);
    }

    /// <summary>
    /// Gets the status of a slicing job.
    /// </summary>
    /// <param name="id">The job ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("jobs/{id}/status")]
    [Authorize]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    public async Task<IActionResult> GetStatusAsync(Guid id, CancellationToken ct)
    {
        SlicingJobDto? job = SlicingJobStore.Get(id);
        if (job is null)
        {
            return SlicerApiProblems.ResourceNotFound(this);
        }

        if (!CanAccess(job))
        {
            return SlicerApiProblems.ResourceForbidden(this);
        }

        SlicingJobStatusResponse? status = await _orchestrator.GetJobStatusAsync(id, ct);
        return status is null ? NotFound() : Ok(status);
    }

    /// <summary>
    /// Cancels a slicing job.
    /// </summary>
    /// <param name="id">The job ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("jobs/{id}/cancel")]
    [Authorize]
    [RequirePermission(PrintFarmerPermissions.Queue.Cancel)]
    public async Task<IActionResult> CancelAsync(Guid id, CancellationToken ct)
    {
        SlicingJobDto? job = SlicingJobStore.Get(id);
        if (job is null)
        {
            return SlicerApiProblems.ResourceNotFound(this);
        }

        if (!CanAccess(job))
        {
            return SlicerApiProblems.ResourceForbidden(this);
        }

        bool cancelled = await _orchestrator.CancelJobAsync(id, ct);
        return cancelled ? NoContent() : NotFound();
    }

    private bool CanAccess(SlicingJobDto job)
    {
        if (_resourceAccess is not null)
        {
            return _resourceAccess.CanAccess(User, job.UserId, "legacy-slice-job", Guid.TryParse(job.JobId, out Guid id) ? id : Guid.Empty);
        }

        return PrintFarmerPermissions.IsFarmAdmin(User) ||
               (PrintFarmerPermissions.TryGetUserId(User, out Guid userId) &&
                job.UserId == userId);
    }

    private static object MapPublicJob(SlicingJobDto job) => new
    {
        job.JobId,
        job.Status,
        job.Progress,
        job.Message,
        job.SlicerEngine,
        job.PrinterId,
        ModelFileName = Path.GetFileName(job.ModelFilePath),
        job.CreatedAt,
        job.CompletedAt,
        job.EstimatedPrintTime,
        job.EstimatedFilamentUsed,
        job.LayerCount,
    };
}
