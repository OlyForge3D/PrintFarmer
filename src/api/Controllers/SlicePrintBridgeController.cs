using System.Security.Claims;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
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
/// Enables sending completed slice job gcode outputs directly to a target printer,
/// or adding them to the print queue.
/// </summary>
/// <remarks>
/// This controller lives in the API project (not the slicer module) because it needs
/// access to both <see cref="IArtifactsService"/> from the slicer module and
/// infrastructure services such as <see cref="IPrintersService"/> and
/// <see cref="IJobQueueService"/>.
/// When the slicer module is disabled (microservices deployment), the slicer services
/// will be null and the endpoints return 503 Service Unavailable.
/// </remarks>
[ApiController]
[Route("api/slice")]
[Authorize]
public class SlicePrintBridgeController(
    IPrintersService printersService,
    ILogger<SlicePrintBridgeController> logger,
    ISliceJobRepository? jobRepository = null,
    IArtifactsService? artifactsService = null,
    IJobQueueService? jobQueueService = null,
    ISliceGcodeImportService? importService = null,
    ISpoolmanService? spoolmanService = null) : ControllerBase
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
        // NOTE: PrintFarmer is single-tenant — all authenticated users may access all printers.
        // If multi-tenant support is added, add printer access authorization here.
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

    /// <summary>
    /// Add the completed gcode from a slice job to the print queue.
    /// The gcode artifact is imported into the GcodeFile library and a queued print job is created.
    /// </summary>
    /// <param name="id">The ID of the completed slice job.</param>
    /// <param name="request">Queuing options: priority, copies, spool, and compatibility overrides.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created print job ID and queue position.</returns>
    /// <response code="200">Job successfully added to the print queue.</response>
    /// <response code="400">Job is not completed, has no gcode artifact, or no compatible printer available.</response>
    /// <response code="404">Slice job not found.</response>
    /// <response code="503">Slicing module or queue services are not enabled.</response>
    [HttpPost("{id:guid}/add-to-queue")]
    [ProducesResponseType(typeof(AddSliceToQueueResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> AddToQueueAsync(
        Guid id,
        [FromBody] AddSliceToQueueRequest request,
        CancellationToken ct)
    {
        if (jobRepository is null || artifactsService is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "Slicing module is not enabled.", code = "SLICER_DISABLED" });
        }

        if (importService is null || jobQueueService is null)
        {
            logger.LogError("Queue services (ISliceGcodeImportService / IJobQueueService) are null — check DI registration");
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "Queue services are unavailable.", code = "QUEUE_UNAVAILABLE" });
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

        // 4. Resolve the artifact file on disk
        var pathResult = await artifactsService.GetWithPathAsync(gcodeArtifact.Id, ct);
        if (pathResult is null || !System.IO.File.Exists(pathResult.Value.FullPath))
        {
            logger.LogError(
                "Gcode artifact file missing from disk for artifact {ArtifactId}, job {JobId}",
                gcodeArtifact.Id, id);
            return BadRequest(new { error = "Gcode artifact file is missing from storage.", artifactId = gcodeArtifact.Id });
        }

        // 5. Import the gcode into the GcodeFile library
        Guid gcodeFileId = await importService.ImportAsync(
            pathResult.Value.Artifact.FileName,
            pathResult.Value.FullPath,
            ct);

        logger.LogInformation(
            "Imported slice gcode from job {JobId} as GcodeFile {GcodeFileId}",
            id, gcodeFileId);

        // 6. Optionally resolve Spoolman spool into denormalized filament fields
        int? spoolmanFilamentId = null;
        string? filamentName = null;
        string? filamentVendor = null;
        string? filamentColor = null;

        if (request.SpoolId.HasValue && spoolmanService is not null)
        {
            try
            {
                SpoolmanSpoolDto? spool = await spoolmanService.GetSpoolByIdAsync(request.SpoolId.Value, ct);
                if (spool is not null)
                {
                    spoolmanFilamentId = spool.FilamentId;
                    filamentName = spool.FilamentName;
                    filamentVendor = spool.Vendor;
                    filamentColor = spool.ColorHex;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to resolve spool {SpoolId} from Spoolman; proceeding without spool info",
                    request.SpoolId.Value);
            }
        }

        // 7. Build queue request and enqueue
        // AddJobToQueueAsync merges these request values with GcodeFile metadata as fallback,
        // so we only need to forward what the caller explicitly provided.
        var queueDto = new QueuePrintJobDto
        {
            GcodeFileId = gcodeFileId,
            AssignedPrinterId = null, // auto-dispatch
            Priority = request.Priority ?? PrintJobPriority.Normal,
            Copies = request.Copies ?? 1,
            RequiredPrinterModel = request.RequiredPrinterModel,
            RequiredMaterialType = request.RequiredMaterialType,
            RequiredNozzleDiameter = request.RequiredNozzleDiameter,
            SpoolmanFilamentId = spoolmanFilamentId,
            FilamentName = filamentName,
            FilamentVendor = filamentVendor,
            FilamentColor = filamentColor,
        };

        JobQueuePrintJobDto? printJob = await jobQueueService.AddJobToQueueAsync(queueDto, userId, ct);
        if (printJob is null)
        {
            return BadRequest(new
            {
                error = "No compatible printer is available for this job. Adjust the compatibility requirements or add a suitable printer.",
                jobId = id,
                gcodeFileId
            });
        }

        logger.LogInformation(
            "Slice job {SliceJobId} queued as print job {PrintJobId} at position {QueuePosition}",
            id, printJob.Id, printJob.QueuePosition);

        return Ok(new AddSliceToQueueResponse
        {
            PrintJobId = printJob.Id,
            QueuePosition = printJob.QueuePosition,
            Message = "Gcode added to the print queue successfully."
        });
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
