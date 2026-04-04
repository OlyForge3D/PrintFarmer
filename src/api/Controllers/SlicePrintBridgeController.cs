using System.Security.Claims;
using Farm.Infrastructure.Services.Printers;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.Controllers.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Bridges the slicer artifact storage and the printer backend upload capability.
/// Enables sending completed slice job gcode outputs directly to a target printer.
/// </summary>
/// <remarks>
/// This controller lives in the API project (not the slicer module) because it needs
/// access to both <see cref="IArtifactsService"/> from the slicer module and
/// <see cref="IPrintersService"/> from the infrastructure layer.
/// When the slicer module is disabled (microservices deployment), the slicer services
/// will be null and the endpoint returns 503 Service Unavailable.
/// </remarks>
[ApiController]
[Route("api/slice")]
[Authorize]
public class SlicePrintBridgeController(
    IPrintersService printersService,
    ILogger<SlicePrintBridgeController> logger,
    ISliceJobRepository? jobRepository = null,
    IArtifactsService? artifactsService = null) : ControllerBase
{
    /// <summary>
    /// Send the completed gcode from a slice job to a printer.
    /// Optionally starts the print immediately after upload.
    /// </summary>
    /// <param name="id">The ID of the completed slice job.</param>
    /// <param name="request">Target printer and print options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Details of the send operation including upload success and print status.</returns>
    /// <response code="200">Gcode successfully sent to printer.</response>
    /// <response code="400">Job is not completed or has no gcode artifacts.</response>
    /// <response code="404">Job or printer not found.</response>
    /// <response code="502">Upload to printer backend failed.</response>
    /// <response code="503">Slicing module is not enabled.</response>
    [HttpPost("{id:guid}/send-to-printer")]
    [ProducesResponseType(typeof(SendToPrinterResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SendToPrinterAsync(
        Guid id,
        [FromBody] SendToPrinterRequest request,
        CancellationToken ct)
    {
        if (jobRepository is null || artifactsService is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "Slicing module is not enabled.", code = "SLICER_DISABLED" });
        }

        // 1. Validate the slice job exists and belongs to the current user
        SliceJob? job = await jobRepository.GetByIdAsync(id, ct);
        if (job is null)
        {
            return NotFound(new { error = "Slice job not found.", jobId = id });
        }

        string currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        if (!Guid.TryParse(currentUserId, out Guid userId) || job.UserId != userId)
        {
            return Forbid();
        }

        // 2. Validate the job is completed
        if (job.Status != SliceJobStatus.Completed)
        {
            return BadRequest(new
            {
                error = $"Slice job is not completed. Current status: {job.Status}.",
                jobId = id
            });
        }

        // 3. Find the gcode artifact
        IReadOnlyList<Artifact> artifacts = await artifactsService.ListByJobAsync(id, ct);
        Artifact? gcodeArtifact = artifacts.FirstOrDefault(a =>
            string.Equals(a.Kind, "gcode", StringComparison.OrdinalIgnoreCase));

        if (gcodeArtifact is null)
        {
            return BadRequest(new { error = "Slice job has no gcode artifact.", jobId = id });
        }

        // 4. Validate the target printer exists
        var printer = await printersService.FindByIdAsync(request.PrinterId, ct);
        if (printer is null)
        {
            return NotFound(new { error = "Printer not found.", printerId = request.PrinterId });
        }

        // 5. Resolve the artifact file on disk
        var pathResult = await artifactsService.GetWithPathAsync(gcodeArtifact.Id, ct);
        if (pathResult is null || !System.IO.File.Exists(pathResult.Value.FullPath))
        {
            logger.LogError(
                "Gcode artifact file missing from disk for artifact {ArtifactId}, job {JobId}",
                gcodeArtifact.Id, id);
            return BadRequest(new { error = "Gcode artifact file is missing from storage.", artifactId = gcodeArtifact.Id });
        }

        string fullPath = pathResult.Value.FullPath;
        string fileName = pathResult.Value.Artifact.FileName;

        logger.LogInformation(
            "Sending gcode {FileName} from slice job {JobId} to printer {PrinterId} (startPrint={StartPrint})",
            fileName, id, request.PrinterId, request.StartPrint);

        // 6. Upload to printer (and optionally start print)
        await using FileStream fileStream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        if (request.StartPrint)
        {
            return await UploadAndStartPrintAsync(id, request.PrinterId, fileName, fileStream, ct);
        }

        return await UploadOnlyAsync(id, request.PrinterId, fileName, fileStream, ct);
    }

    private async Task<IActionResult> UploadAndStartPrintAsync(
        Guid jobId, Guid printerId, string fileName, Stream stream, CancellationToken ct)
    {
        UploadAndPrintResult result = await printersService.UploadAndStartPrintAsync(
            printerId, fileName, stream, progress: null, ct);

        if (!result.Success)
        {
            logger.LogWarning(
                "Upload-and-print failed for job {JobId} to printer {PrinterId}: stage={Stage}, error={Error}",
                jobId, printerId, result.FailedStage, result.ErrorMessage);

            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "Failed to upload and start print on the target printer.",
                failedStage = result.FailedStage.ToString(),
                detail = result.ErrorMessage
            });
        }

        return Ok(new SendToPrinterResponse
        {
            JobId = jobId,
            PrinterId = printerId,
            FileName = fileName,
            PrintStarted = true,
            Message = "Gcode uploaded and print started successfully."
        });
    }

    private async Task<IActionResult> UploadOnlyAsync(
        Guid jobId, Guid printerId, string fileName, Stream stream, CancellationToken ct)
    {
        bool uploaded = await printersService.UploadGcodeAsync(printerId, fileName, stream, ct);

        if (!uploaded)
        {
            logger.LogWarning("Gcode upload failed for job {JobId} to printer {PrinterId}", jobId, printerId);

            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "Failed to upload gcode to the target printer."
            });
        }

        return Ok(new SendToPrinterResponse
        {
            JobId = jobId,
            PrinterId = printerId,
            FileName = fileName,
            PrintStarted = false,
            Message = "Gcode uploaded successfully."
        });
    }
}
