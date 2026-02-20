using Farm.Slicer.Module.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Slicer.Module.Api.Controllers.Slicing;

/// <summary>
/// Provides real-time slicing progress polling endpoints.
/// </summary>
[ApiController]
[Route("api/slicer")]
[Tags("Slicer Progress")]
public class SlicingProgressController : ControllerBase
{
    /// <summary>
    /// Gets the current progress of all active slicing jobs.
    /// </summary>
    [HttpGet("progress")]
    public IActionResult GetAll()
    {
        var jobs = SlicingJobStore.GetAll()
            .Where(j => j.Status == SlicingJobStatus.Slicing)
            .Select(j => new
            {
                jobId = j.JobId,
                fileName = j.ModelFilePath,
                status = j.Status.ToString(),
                progress = j.Progress,
                message = j.Message,
            })
            .ToList();

        return Ok(jobs);
    }

    /// <summary>
    /// Gets the progress of a specific slicing job.
    /// </summary>
    /// <param name="id">The job ID.</param>
    [HttpGet("progress/{id}")]
    public IActionResult Get(Guid id)
    {
        SlicingJobDto? job = SlicingJobStore.Get(id);
        if (job is null)
        {
            return NotFound();
        }

        return Ok(new
        {
            jobId = job.JobId,
            fileName = job.ModelFilePath,
            status = job.Status.ToString(),
            progress = job.Progress,
            message = job.Message,
        });
    }
}
