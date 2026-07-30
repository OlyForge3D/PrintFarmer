using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Api.Authorization;
using Farm.Slicer.Module.Api.Filters;
using Farm.Slicer.Module.Dtos;
using Microsoft.AspNetCore.Authorization;
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
    [Authorize]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    public IActionResult GetAll()
    {
        IEnumerable<SlicingJobDto> accessibleJobs = SlicingJobStore.GetAll();
        if (!PrintFarmerPermissions.IsFarmAdmin(User))
        {
            if (!PrintFarmerPermissions.TryGetUserId(User, out Guid userId))
            {
                return SlicerApiProblems.ResourceForbidden(this);
            }

            accessibleJobs = accessibleJobs.Where(job => job.UserId == userId);
        }

        var jobs = accessibleJobs
            .Where(j => j.Status == SlicingJobStatus.Slicing)
            .Select(j => new
            {
                jobId = j.JobId,
                fileName = Path.GetFileName(j.ModelFilePath),
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
    [Authorize]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    public IActionResult Get(Guid id)
    {
        SlicingJobDto? job = SlicingJobStore.Get(id);
        if (job is null)
        {
            return NotFound();
        }

        if (!PrintFarmerPermissions.IsFarmAdmin(User) &&
            (!PrintFarmerPermissions.TryGetUserId(User, out Guid userId) || job.UserId != userId))
        {
            return SlicerApiProblems.ResourceForbidden(this);
        }

        return Ok(new
        {
            jobId = job.JobId,
            fileName = Path.GetFileName(job.ModelFilePath),
            status = job.Status.ToString(),
            progress = job.Progress,
            message = job.Message,
        });
    }
}
