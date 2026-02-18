using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Controllers.Slicing;

/// <summary>
/// Legacy slicing jobs controller (pre-queue system).
/// Provides endpoints for direct slicing job management via the orchestrator.
/// </summary>
[ApiController]
[Route("api/slicer")]
[Tags("Slicer Jobs")]
public class SlicingJobsController(
    ILogger<SlicingJobsController> logger,
    ISlicerTempPathProvider tempPathProvider,
    ISlicerOrchestrator orchestrator,
    ISlicerFileManagementService fileManagementService,
    ISlicerStoredFileOpsService fileOperations) : ControllerBase
{
    private readonly ISlicerTempPathProvider _tempPathProvider = tempPathProvider;
    private readonly ISlicerOrchestrator _orchestrator = orchestrator;

    /// <summary>
    /// Submits a new slicing job.
    /// </summary>
    /// <param name="file">The model file to slice.</param>
    /// <param name="profileJson">Slicer profile configuration JSON.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("jobs")]
    public async Task<IActionResult> SubmitAsync(IFormFile file, [FromForm] string? profileJson, CancellationToken ct)
    {
        if (file.Length == 0)
        {
            return BadRequest(new { error = "File is empty." });
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
    public IActionResult List()
    {
        var jobs = SlicingJobStore.GetAll();
        return Ok(jobs);
    }

    /// <summary>
    /// Gets a specific slicing job by ID.
    /// </summary>
    /// <param name="id">The job ID.</param>
    [HttpGet("jobs/{id}")]
    public IActionResult Get(Guid id)
    {
        SlicingJobDto? job = SlicingJobStore.Get(id);
        return job is null ? NotFound() : Ok(job);
    }

    /// <summary>
    /// Gets the status of a slicing job.
    /// </summary>
    /// <param name="id">The job ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("jobs/{id}/status")]
    public async Task<IActionResult> GetStatusAsync(Guid id, CancellationToken ct)
    {
        SlicingJobStatusResponse? status = await _orchestrator.GetJobStatusAsync(id, ct);
        return status is null ? NotFound() : Ok(status);
    }

    /// <summary>
    /// Cancels a slicing job.
    /// </summary>
    /// <param name="id">The job ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("jobs/{id}/cancel")]
    public async Task<IActionResult> CancelAsync(Guid id, CancellationToken ct)
    {
        bool cancelled = await _orchestrator.CancelJobAsync(id, ct);
        return cancelled ? NoContent() : NotFound();
    }
}
