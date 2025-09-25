using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using Farm.Infrastructure;

namespace Farm.Web.Api.Controllers.Slicing;

[ApiController]
[Route("api/slicer")]
[Tags("Slicer Jobs")]
public class SlicingJobsController : ControllerBase
{
    private readonly IUnifiedLoggingService _logger;
    private readonly Infrastructure.Temp.ITempPathProvider _tempPathProvider;
    private readonly ISlicerOrchestrator _orchestrator;
    private readonly string _tempRoot;

    public SlicingJobsController(IUnifiedLoggingService logger, Infrastructure.Temp.ITempPathProvider tempPathProvider, ISlicerOrchestrator orchestrator)
    {
        _logger = logger;
        _tempPathProvider = tempPathProvider;
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _tempRoot = Path.GetFullPath(_tempPathProvider.GetTempRoot());
        Directory.CreateDirectory(_tempRoot);
    }

    [HttpGet("jobs/{jobId}/status")]
    [ProducesResponseType(typeof(SlicingJobStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJobStatusAsync(Guid jobId)
    {
        SlicingJobStatusResponse? status = await _orchestrator.GetJobStatusAsync(jobId);
        if (status == null)
        {
            return NotFound();
        }

        return Ok(status);
    }

    // Canonical plural route
    [HttpGet("jobs/{jobId}")]
    [ProducesResponseType(typeof(SliceResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetJob(string jobId)
    {
        if (!SlicingJobStore.TryGet(jobId, out SlicingJobDto? job) || job == null)
        {
            return NotFound();
        }
        SlicingJobDto j = job;
        return Ok(new SliceResultDto
        {
            JobId = j.JobId,
            // Prefer plural form in emitted URLs
            GcodeUrl = j.Status == SlicingJobStatus.Completed ? $"/api/slicer/jobs/{j.JobId}/gcode" : string.Empty,
            PrintTime = j.EstimatedPrintTime ?? 0,
            FilamentUsed = j.EstimatedFilamentUsed ?? 0,
            LayerCount = j.LayerCount ?? 0,
            Status = j.Status.ToString(),
            Progress = j.Progress,
            Metadata = new SliceMetadataDto
            {
                SlicerVersion = j.SlicerEngine == "prusaslicer" ? "PrusaSlicer 2.7.0" : "OrcaSlicer 1.8.0",
                ProfileUsed = $"{j.Profile?.Quality} - {j.Profile?.Material}",
                EstimatedCost = 0
            }
        });
    }

    [HttpPost("jobs/{jobId}/cancel")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult CancelJob(string jobId)
    {
        // Accept as string to allow graceful 404 for non-GUID IDs used in tests
        if (!SlicingJobStore.TryGet(jobId, out SlicingJobDto? job) || job == null)
        {
            return NotFound();
        }

        // If job already in a terminal state, return 409 per contract (cannot cancel)
        if (job.Status is SlicingJobStatus.Completed or SlicingJobStatus.Error or SlicingJobStatus.Cancelled)
        {
            return Conflict(new { success = false, message = "Job cannot be cancelled" });
        }

        job.Status = SlicingJobStatus.Cancelled;
        job.Message = "Cancelled by user";
        _logger.LogInformation($"Cancelled slicing job {jobId}");
        return Ok(new { success = true, message = "Job cancelled successfully" });
    }

    // Legacy singular status route – redirect to plural while job exists. Returns 404 if job missing.
    [HttpGet("job/{jobId}")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult LegacyGetJob(string jobId)
    {
        if (!SlicingJobStore.TryGet(jobId, out SlicingJobDto? job) || job == null)
        {
            return NotFound();
        }
        // Add deprecation signalling headers (RFC 8594) before issuing redirect
        DateTime deprecationDate = new(2025, 9, 8, 0, 0, 0, DateTimeKind.Utc);
        DateTime sunsetDate = new(2026, 3, 8, 0, 0, 0, DateTimeKind.Utc); // planned removal 6 months later
        Response.Headers.TryAdd("Deprecation", deprecationDate.ToString("r")); // HTTP-date format
        Response.Headers.TryAdd("Sunset", sunsetDate.ToString("r"));
        // Issue 302 redirect to canonical plural endpoint
        return Redirect($"/api/slicer/jobs/{jobId}");
    }

    // Expose both plural (canonical) and singular (legacy) for G-code retrieval
    [HttpGet("jobs/{jobId}/gcode")]
    [HttpGet("job/{jobId}/gcode")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Path rebuilt from trusted temp root + sanitized filename (.gcode only, traversal rejected, GUID job lookup)")]
    public IActionResult GetGcode(string jobId)
    {
        // Fast reject if not a GUID (we only generate GUID job IDs internally)
        if (!Guid.TryParse(jobId, out _))
        {
            return NotFound();
        }
        if (!SlicingJobStore.TryGet(jobId, out SlicingJobDto? job) || job == null || job.Status != SlicingJobStatus.Completed || string.IsNullOrEmpty(job.GcodeFilePath))
        {
            return NotFound();
        }

        string originalPath = job.GcodeFilePath!;
        string tempRoot = Path.GetFullPath(_tempPathProvider.GetTempRoot());
        // Rebuild path from trusted root to mitigate stored path tampering
        string fileName = Path.GetFileName(originalPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return NotFound();
        }
        if (!fileName.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound();
        }
        if (fileName.Contains("..", StringComparison.Ordinal) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return NotFound();
        }
        string rebuiltPath = Path.Combine(tempRoot, fileName);
        string path = rebuiltPath;
        if (!IsSafePath(path, tempRoot))
        {
            return NotFound();
        }
        // At this point 'path' is reconstructed from a trusted root + sanitized filename (.gcode enforced, traversal rejected).
        // Suppress analyzer warning: path cannot be influenced directly by user input beyond GUID lookup.
        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }

        return PhysicalFile(path, "text/plain; charset=utf-8", $"output_{jobId}.gcode");
    }

    private static bool IsSafePath(string candidatePath, string root)
    {
        try
        {
            string fullRoot = Path.GetFullPath(root);
            string fullCandidate = Path.GetFullPath(candidatePath);
            if (!fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string relative = Path.GetRelativePath(fullRoot, fullCandidate);
            if (relative.Contains(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative.Contains(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                return false;
            }
            return true;
        }
        catch { return false; }
    }
}
