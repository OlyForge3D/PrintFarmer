using System.Text.Json;
using Farm.Infrastructure;
using Farm.Web.Api.Services.FileManagement;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers.Slicing;

[ApiController]
[Route("api/slicer")]
[Tags("Slicer Progress")]
public class SlicingProgressController(IStoredFileOperationsService fileOperations) : ControllerBase
{
    private readonly IStoredFileOperationsService _fileOperations = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));

    [HttpGet("progress/{jobId}")]
    public async Task GetProgressAsync([FromRoute] string jobId)
    {
#pragma warning disable S6932 // Accept header manual inspection for SSE negotiation
        string acceptHeaders = HttpContext.Request.Headers["Accept"].ToString();
#pragma warning restore S6932
        if (!acceptHeaders.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            HttpContext.Response.Headers["Content-Type"] = "text/event-stream";
        }

        HttpContext.Response.Headers["Cache-Control"] = "no-cache";
        HttpContext.Response.Headers["X-Accel-Buffering"] = "no"; // disable buffering for nginx

        bool found = SlicingJobStore.TryGet(jobId, out SlicingJobDto? job);
        if (!found || job == null)
        {
            await HttpContext.Response.WriteAsync("event: error\n");
            await HttpContext.Response.WriteAsync("data: {\"message\":\"Job not found\"}\n\n");
            await HttpContext.Response.Body.FlushAsync();
            return;
        }

        CancellationToken ct = HttpContext.RequestAborted;
        while (!ct.IsCancellationRequested)
        {
            string payload = JsonSerializer.Serialize(new
            {
                jobId = job.JobId,
                status = job.Status.ToString(),
                progress = job.Progress,
                message = job.Message,
                gcodeUrl = string.IsNullOrWhiteSpace(job.GcodeFilePath) ? null : _fileOperations.BuildSlicerJobGcodeUrl(Guid.Parse(job.JobId))
            });
            await HttpContext.Response.WriteAsync($"data: {payload}\n\n");
            await HttpContext.Response.Body.FlushAsync();
            if (job.Status is SlicingJobStatus.Completed or SlicingJobStatus.Error or SlicingJobStatus.Cancelled)
            {
                break;
            }

            try
            {
                await Task.Delay(1000, ct);
            }
            catch
            {
                break;
            }
        }
    }
}
