using System.Security.Claims;
using Farm.Infrastructure;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Modules.Calibration.Services.Calibration;
using Farm.Modules.Gcode.Services.Gcode;
using Farm.Modules.PrintQueue.Controllers.Requests;
using Farm.Modules.PrintQueue.Controllers.Responses;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Web.Api.Services.Gcode.Safety;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Modules.PrintQueue.Controllers;

/// <summary>
/// Bridges the slicer artifact storage and the printer backend upload capability.
/// Enables sending completed slice job gcode outputs directly to a target printer,
/// or adding them to the print queue.
/// </summary>
/// <remarks>
/// This controller lives in Farm.Modules.PrintQueue (not the slicer module) because it needs
/// access to both slicer job metadata and the durable G-code library, plus infrastructure
/// services such as <see cref="IPrintersService"/> and <see cref="IJobQueueService"/>.
/// </remarks>
[ApiController]
[Route("api/slice")]
[Authorize]
public class SlicePrintBridgeController(
    IPrintersService printersService,
    ILogger<SlicePrintBridgeController> logger,
    IGcodeSafetyValidator gcodeSafetyValidator,
    ISliceJobRepository? jobRepository = null,
    IJobQueueService? jobQueueService = null,
    ISpoolmanService? spoolmanService = null,
    ISliceArtifactLibraryService? sliceArtifactLibraryService = null,
    IGcodeFilesService? gcodeFilesService = null,
    IDispatchClaimService? dispatchClaimService = null,
    IQueueResourceAuthorizationService? resourceAuthorization = null) : ControllerBase
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
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
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
        if (jobRepository is null || sliceArtifactLibraryService is null || gcodeFilesService is null)
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

        if (IsCalibrationSlice(job))
        {
            return CalibrationSliceRequiresPrimaryQueue();
        }

        if (resourceAuthorization is not null &&
            !await resourceAuthorization.CanAccessPrinterAsync(
                User,
                request.PrinterId,
                PrinterGroupAccessLevel.Submit,
                ct))
        {
            return NotFound(new { error = "Printer not found.", printerId = request.PrinterId });
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

        // 3. Validate the target printer exists after the resource-scope check above.
        var printer = await printersService.FindByIdWithIncludesAsync(request.PrinterId, ct);
        if (printer is null)
        {
            return NotFound(new { error = "Printer not found.", printerId = request.PrinterId });
        }

        // 4. Commit the staged artifact to the durable library before any printer-side effect.
        var actor = new CalibrationActor(
            userId,
            QueueActorIdentity.Resolve(User),
            PrintFarmerPermissions.IsFarmAdmin(User));
        CalibrationApiResult<SliceArtifactLibraryResult> promotion =
            await sliceArtifactLibraryService.PromoteAsync(id, artifactId: null, actor, ct);
        if (!promotion.IsSuccess || promotion.Value is null)
        {
            return PromotionFailure(promotion, id);
        }

        // 5. Read the exact durable bytes that will be validated and uploaded.
        byte[]? gcodeBytes;
        try
        {
            gcodeBytes = await gcodeFilesService.ReadFileBytesAsync(promotion.Value.GcodeFileId, ct);
        }
        catch (IOException ex)
        {
            logger.LogWarning(
                ex,
                "Could not read durable GcodeFile {GcodeFileId} for slice job {JobId}",
                promotion.Value.GcodeFileId,
                id);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new
                {
                    error = "The durable G-code file is temporarily unavailable.",
                    code = "PROMOTED_GCODE_UNAVAILABLE",
                    gcodeFileId = promotion.Value.GcodeFileId,
                });
        }

        if (gcodeBytes is null)
        {
            logger.LogError(
                "Promoted GcodeFile {GcodeFileId} is missing from durable storage for slice job {JobId}",
                promotion.Value.GcodeFileId,
                id);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new
                {
                    error = "The durable G-code file is unavailable.",
                    code = "PROMOTED_GCODE_UNAVAILABLE",
                    gcodeFileId = promotion.Value.GcodeFileId,
                });
        }

        string fileName = promotion.Value.Name;

        // 6. Validate and upload from the exact byte content just read. Reading once (instead of
        // validating the file, then reopening it for upload) closes a time-of-check/time-of-use
        // gap: a file swapped on disk between validation and upload could otherwise reach the
        // printer without ever being validated.

        // File.ReadAllTextAsync (used previously) strips a leading UTF-8 byte-order-mark via
        // StreamReader's BOM detection. Decoding the raw bytes directly does not: a BOM byte
        // sequence decodes to a literal U+FEFF character, which would prepend the first g-code
        // command and let it silently evade the safety interpreter's line parsing. Trim it so
        // validation behaves identically to a BOM-stripped read.
        string gcodeText = System.Text.Encoding.UTF8.GetString(gcodeBytes).TrimStart('\uFEFF');

        // 7. Run the general g-code safety pass before this program is ever streamed to a
        // physical printer. This is not calibration-scoped: any command is allowed
        // (AllowedCommands: null), and the machine envelope is sourced from the printer/
        // toolhead domain fields, never from Printer.Calibration* columns.
        GcodeSafetyLimits safetyLimits;
        try
        {
            safetyLimits = BuildSafetyLimits(printer);
        }
        catch (InvalidSafetyGeometryException ex)
        {
            logger.LogError(
                ex,
                "Printer {PrinterId} has invalid printable-polygon/excluded-region configuration; refusing to send gcode without a trustworthy safety envelope",
                printer.Id);
            return BadRequest(new
            {
                error = "Printer geometry configuration (printable polygon or excluded regions) is invalid and cannot be safely enforced.",
                printerId = printer.Id,
                detail = ex.Message,
            });
        }

        GcodeSafetyResult<GcodeSafetyReport> safetyResult = gcodeSafetyValidator.Validate(
            new GcodeSafetyRequest(
                safetyLimits,
                gcodeText,
                GcodeSafetyCheckpoint.BeforeSendToPrinter));

        if (!safetyResult.IsValid)
        {
            logger.LogWarning(
                "Gcode safety validation rejected artifact {ArtifactId} for job {JobId} before send-to-printer: {Problems}",
                promotion.Value.SourceArtifactId,
                id,
                string.Join("; ", safetyResult.Problems.Select(p => $"{p.Code}: {p.Message}")));
            return BadRequest(new
            {
                error = "Gcode failed safety validation and was not sent to the printer.",
                jobId = id,
                problems = safetyResult.Problems,
            });
        }

        logger.LogInformation(
            "Sending gcode {FileName} from slice job {JobId} to printer {PrinterId} (startPrint={StartPrint})",
            fileName, id, request.PrinterId, request.StartPrint);

        // 8. Upload to printer (and optionally start print) using the same bytes that were
        // validated above, not a fresh read of the file on disk.
        await using MemoryStream uploadStream = new(gcodeBytes, writable: false);

        if (request.StartPrint)
        {
            return await UploadAndStartPrintAsync(id, request.PrinterId, fileName, uploadStream, ct);
        }

        return await UploadOnlyAsync(id, request.PrinterId, fileName, uploadStream, ct);
    }

    /// <summary>
    /// Builds the authoritative g-code safety envelope for <paramref name="printer"/> from its
    /// own domain fields and primary toolhead. Deliberately does not read any
    /// <c>Printer.Calibration*</c> column: those are calibration-scoped ceilings slated for
    /// removal and must never gate the general send-to-printer safety pass.
    /// </summary>
    /// <exception cref="InvalidSafetyGeometryException">
    /// The printer has a configured (non-null) <c>PrintablePolygonJson</c> or
    /// <c>ExcludedRegionsJson</c> value that is malformed or geometrically invalid. Callers must
    /// treat this as a request failure, not fall back to an unguarded envelope.
    /// </exception>
    private static GcodeSafetyLimits BuildSafetyLimits(Farm.Infrastructure.Domain.Printer printer)
    {
        Farm.Infrastructure.Domain.Toolhead? toolhead =
            printer.Toolheads?.FirstOrDefault(t => t.IsPrimary) ??
            printer.Toolheads?.FirstOrDefault();

        var toolheadLimits = new GcodeSafetyToolheadLimits(
            toolhead?.NozzleMaxTemperature,
            toolhead?.HotendMaxTemperature ?? toolhead?.HotendModel?.MaxTemp,
            toolhead?.IsDirectDrive);

        var bedLimits = new GcodeSafetyBedLimits(
            ToFiniteDecimalOrThrow(printer.MaxBuildVolumeX, nameof(printer.MaxBuildVolumeX)),
            ToFiniteDecimalOrThrow(printer.MaxBuildVolumeY, nameof(printer.MaxBuildVolumeY)),
            ToFiniteDecimalOrThrow(printer.MaxBuildVolumeZ, nameof(printer.MaxBuildVolumeZ)),
            ToFiniteDecimalOrThrow(printer.BedOriginX, nameof(printer.BedOriginX)),
            ToFiniteDecimalOrThrow(printer.BedOriginY, nameof(printer.BedOriginY)),
            ParsePrintablePolygon(printer.PrintablePolygonJson),
            ParseExcludedRegions(printer.ExcludedRegionsJson));

        var machineLimits = new GcodeSafetyMachineLimits(
            printer.MaxBedTemp,
            printer.HasHeatedChamber,
            printer.MaxChamberTemp,
            printer.MaxPrintSpeed,
            printer.MaxTravelSpeed,
            printer.MaxAcceleration);

        // Filament diameter is not attached to the printer/toolhead configuration - it lives on
        // whichever spool is currently loaded (Spoolman), which would require a live external
        // lookup from this hot request path. Volumetric-flow checking is therefore left disabled
        // here (GcodeSafetyPrintLimits.Empty), same as it is unavailable to the general validator
        // whenever a caller cannot resolve it; this does not affect any other check.
        return new GcodeSafetyLimits(
            toolheadLimits,
            bedLimits,
            machineLimits,
            GcodeSafetyPrintLimits.Empty);
    }

    /// <summary>
    /// Converts a nullable printer/bed dimension to <see cref="decimal"/>, treating a
    /// non-finite value (<see cref="double.PositiveInfinity"/>, <see cref="double.NegativeInfinity"/>,
    /// or <see cref="double.NaN"/>) as configured-but-invalid data rather than letting an unguarded
    /// cast throw <see cref="OverflowException"/>. A stored printer dimension should never be
    /// non-finite, but this guards the same class of bug already fixed for polygon coordinates:
    /// the cast must fail closed (<see cref="InvalidSafetyGeometryException"/>, caught -> 400),
    /// not fail open with an unhandled exception (500).
    /// </summary>
    private static decimal? ToFiniteDecimalOrThrow(double? value, string fieldName)
    {
        if (value is not { } v)
        {
            return null;
        }

        if (!double.IsFinite(v))
        {
            throw new InvalidSafetyGeometryException($"Printer field '{fieldName}' is not a finite value.");
        }

        return (decimal)v;
    }

    private sealed record SafetyPolygonPointDto(
        [property: System.Text.Json.Serialization.JsonPropertyName("x"), System.Text.Json.Serialization.JsonRequired] double X,
        [property: System.Text.Json.Serialization.JsonPropertyName("y"), System.Text.Json.Serialization.JsonRequired] double Y);

    private sealed record SafetyExcludedRegionDto(
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("polygon"), System.Text.Json.Serialization.JsonRequired]
        IReadOnlyList<SafetyPolygonPointDto> Polygon);

    /// <summary>
    /// Parses <see cref="Farm.Infrastructure.Domain.Printer.PrintablePolygonJson"/> into safety-pass
    /// points. Absent (<see langword="null"/>/blank) JSON means "no printable polygon configured"
    /// and safely returns an empty list (the polygon check is then skipped). Configured-but-invalid
    /// JSON — malformed, missing x/y, or fewer than three points — throws
    /// <see cref="InvalidSafetyGeometryException"/> so the caller fails the request closed instead
    /// of silently disabling the guard.
    /// </summary>
    private static List<GcodeSafetyPoint> ParsePrintablePolygon(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        List<SafetyPolygonPointDto>? points;
        try
        {
            points = System.Text.Json.JsonSerializer.Deserialize<List<SafetyPolygonPointDto>>(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidSafetyGeometryException($"PrintablePolygonJson is not valid JSON: {ex.Message}");
        }

        List<GcodeSafetyPoint> result = [];
        foreach (SafetyPolygonPointDto? point in points ?? [])
        {
            if (point is null)
            {
                throw new InvalidSafetyGeometryException(
                    "PrintablePolygonJson is configured but contains a null point.");
            }

            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                throw new InvalidSafetyGeometryException(
                    "PrintablePolygonJson is configured but contains a non-finite coordinate.");
            }

            result.Add(new GcodeSafetyPoint((decimal)point.X, (decimal)point.Y));
        }

        if (result.Count < 3)
        {
            throw new InvalidSafetyGeometryException(
                "PrintablePolygonJson is configured but does not describe a valid polygon (at least three points are required).");
        }

        return result;
    }

    /// <summary>
    /// Parses <see cref="Farm.Infrastructure.Domain.Printer.ExcludedRegionsJson"/> into safety-pass
    /// regions. Same "absent is fine, configured-but-invalid fails closed" rationale as
    /// <see cref="ParsePrintablePolygon"/>. An empty list of regions (<c>[]</c>) is valid — it means
    /// no excluded regions are configured — but any individual region must have at least three
    /// polygon points.
    /// </summary>
    private static List<GcodeSafetyExcludedRegion> ParseExcludedRegions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        List<SafetyExcludedRegionDto>? regions;
        try
        {
            regions = System.Text.Json.JsonSerializer.Deserialize<List<SafetyExcludedRegionDto>>(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidSafetyGeometryException($"ExcludedRegionsJson is not valid JSON: {ex.Message}");
        }

        if (regions is null)
        {
            return [];
        }

        var result = new List<GcodeSafetyExcludedRegion>(regions.Count);
        foreach (SafetyExcludedRegionDto? region in regions)
        {
            if (region is null)
            {
                throw new InvalidSafetyGeometryException(
                    "ExcludedRegionsJson is configured but contains a null region.");
            }

            if (region.Polygon is null)
            {
                throw new InvalidSafetyGeometryException(
                    "ExcludedRegionsJson is configured but a region has a null polygon.");
            }

            var polygon = new List<GcodeSafetyPoint>(region.Polygon.Count);
            foreach (SafetyPolygonPointDto? point in region.Polygon)
            {
                if (point is null)
                {
                    throw new InvalidSafetyGeometryException(
                        "ExcludedRegionsJson is configured but a region's polygon contains a null point.");
                }

                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                {
                    throw new InvalidSafetyGeometryException(
                        "ExcludedRegionsJson is configured but a region's polygon contains a non-finite coordinate.");
                }

                polygon.Add(new GcodeSafetyPoint((decimal)point.X, (decimal)point.Y));
            }

            if (polygon.Count < 3)
            {
                throw new InvalidSafetyGeometryException(
                    "ExcludedRegionsJson is configured but contains a region with fewer than three polygon points.");
            }

            result.Add(new GcodeSafetyExcludedRegion(region.Name ?? string.Empty, polygon));
        }

        return result;
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
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
    [ProducesResponseType(typeof(AddSliceToQueueResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> AddToQueueAsync(
        Guid id,
        [FromBody] AddSliceToQueueRequest request,
        CancellationToken ct)
    {
        if (jobRepository is null || sliceArtifactLibraryService is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "Slicing module is not enabled.", code = "SLICER_DISABLED" });
        }

        if (jobQueueService is null)
        {
            logger.LogError("Queue service IJobQueueService is null — check DI registration");
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "Queue services are unavailable.", code = "QUEUE_UNAVAILABLE" });
        }

        // Validate copies range before performing any expensive operations.
        if (request.Copies is < 1 or > 99)
        {
            return BadRequest(new
            {
                error = "Copies must be between 1 and 99.",
                jobId = id
            });
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

        if (IsCalibrationSlice(job))
        {
            return CalibrationSliceRequiresPrimaryQueue();
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

        // 3. Commit the staged artifact through the same durable promotion path used by Save.
        var actor = new CalibrationActor(
            userId,
            QueueActorIdentity.Resolve(User),
            PrintFarmerPermissions.IsFarmAdmin(User));
        CalibrationApiResult<SliceArtifactLibraryResult> promotion =
            await sliceArtifactLibraryService.PromoteAsync(id, artifactId: null, actor, ct);
        if (!promotion.IsSuccess || promotion.Value is null)
        {
            return PromotionFailure(promotion, id);
        }

        logger.LogInformation(
            "Resolved slice job {JobId} to durable GcodeFile {GcodeFileId} (createdNew={CreatedNew})",
            id,
            promotion.Value.GcodeFileId,
            promotion.Value.CreatedNew);

        // 4. Optionally resolve Spoolman spool into denormalized filament fields
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

        // 5. Build queue request and enqueue
        // AddJobToQueueAsync merges these request values with GcodeFile metadata as fallback,
        // so we only need to forward what the caller explicitly provided.
        var queueDto = new QueuePrintJobDto
        {
            GcodeFileId = promotion.Value.GcodeFileId,
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
            const string noCompatiblePrinterError =
                "No compatible printer is available for this job. Adjust the compatibility requirements or add a suitable printer.";
            return BadRequest(new
            {
                error = noCompatiblePrinterError,
                jobId = id,
                gcodeFileId = promotion.Value.GcodeFileId
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

    private ObjectResult PromotionFailure(
        CalibrationApiResult<SliceArtifactLibraryResult> promotion,
        Guid sliceJobId) =>
        StatusCode(
            promotion.StatusCode,
            new
            {
                error = "The slice artifact could not be saved to the G-code library.",
                code = promotion.Code ?? "promotion_operation_failed",
                jobId = sliceJobId,
            });

    private async Task<IActionResult> UploadAndStartPrintAsync(
        Guid jobId, Guid printerId, string fileName, Stream stream, CancellationToken ct)
    {
        // =====================================================================
        // Every start path — including the slice→print bridge — must acquire the
        // shared dispatch claim BEFORE touching an adapter (issue #900, defect 5).
        // The claim enforces the printer gates (enabled/available/not in maintenance/
        // no active lease/telemetry not printing) and writes a durable attempt row so
        // an unknown outcome is reconcilable instead of invisible.
        // =====================================================================
        if (dispatchClaimService is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "Dispatch claim service is not available.", code = "DISPATCH_UNAVAILABLE" });
        }

        string actorSubject = QueueActorIdentity.Resolve(User);

        DispatchClaimResult claim = await dispatchClaimService.AcquireAdHocClaimAsync(
            new AdHocDispatchClaimRequest(printerId, actorSubject, "SliceBridge", fileName), ct);

        if (!claim.Success || claim.Attempt is null)
        {
            logger.LogWarning(
                "Slice-bridge start denied for job {JobId} on printer {PrinterId}: {Code}",
                jobId, printerId, claim.ErrorCode);

            return Conflict(new
            {
                error = "Printer cannot accept a new print right now.",
                code = claim.ErrorCode,
                detail = claim.ErrorDetail,
            });
        }

        Guid attemptId = claim.Attempt.Id;
        string backendFileName = claim.Attempt.BackendFileName
            ?? throw new InvalidOperationException("Dispatch claim did not persist a backend file identity.");

        UploadAndPrintResult result;
        try
        {
            if (!await dispatchClaimService.RecordBackendCallStartedAsync(
                    attemptId,
                    ct))
            {
                return Conflict(new
                {
                    error = "The dispatch attempt no longer owns the printer.",
                    code = "attempt_superseded",
                });
            }

            result = await printersService.UploadAndStartPrintAsync(
                printerId, backendFileName, stream, progress: null, ct);
        }
        catch (Exception ex)
        {
            // Unknown outcome: the command may have been delivered. Never release the
            // lease here — reconciliation owns the resolution.
            bool applied = await dispatchClaimService.RecordUnknownOutcomeAsync(
                attemptId,
                ex.Message,
                CancellationToken.None);
            if (!applied)
            {
                return Conflict(new
                {
                    error = "The dispatch attempt no longer owns the printer.",
                    code = "attempt_superseded",
                });
            }

            logger.LogError(
                ex,
                "Slice-bridge start produced an unknown outcome for job {JobId} on printer {PrinterId}",
                jobId, printerId);

            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "The printer start outcome could not be determined; reconciliation is required.",
                code = "backend_outcome_unknown",
            });
        }

        if (!result.Success && result.Outcome == UploadAndPrintOutcome.Unknown)
        {
            bool applied = await dispatchClaimService.RecordUnknownOutcomeAsync(
                attemptId,
                result.ErrorMessage ?? "The backend response was lost after the start-capable request.",
                ct);
            if (!applied)
            {
                return Conflict(new
                {
                    error = "The dispatch attempt no longer owns the printer.",
                    code = "attempt_superseded",
                });
            }

            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "The printer start outcome could not be determined; reconciliation is required.",
                code = "backend_outcome_unknown",
            });
        }

        if (!result.Success)
        {
            bool applied = await dispatchClaimService.ReleaseClaimOnKnownFailureAsync(
                attemptId,
                "backend_rejected",
                result.ErrorMessage ?? "The printer rejected the start command.",
                ct);
            if (!applied)
            {
                return Conflict(new
                {
                    error = "The dispatch attempt no longer owns the printer.",
                    code = "attempt_superseded",
                });
            }

            logger.LogWarning(
                "Upload-and-print failed for job {JobId} to printer {PrinterId}: stage={Stage}",
                jobId, printerId, result.FailedStage);

            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "Failed to upload and start print on the target printer.",
                failedStage = result.FailedStage.ToString(),
                detail = result.ErrorMessage
            });
        }

        if (!await dispatchClaimService.RecordBackendAcceptedAsync(
                attemptId,
                result.BackendJobId,
                ct))
        {
            return Conflict(new
            {
                error = "The dispatch attempt no longer owns the printer.",
                code = "attempt_superseded",
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

    private static bool IsCalibrationSlice(SliceJob job) =>
        job.CalibrationProjectId.HasValue ||
        job.CalibrationAttemptId.HasValue ||
        job.CalibrationOrchestrationId.HasValue;

    private UnprocessableEntityObjectResult CalibrationSliceRequiresPrimaryQueue() =>
        UnprocessableEntity(new
        {
            error = "calibration_primary_queue_required",
            detail =
                "Calibration slice output must be promoted as an immutable G-code artifact and " +
                "created through POST /api/job-queue. Direct send and generic slice import are not allowed.",
        });
}

/// <summary>
/// Thrown by <see cref="SlicePrintBridgeController"/> when a printer's configured (non-null)
/// printable-polygon or excluded-region JSON is present but invalid — malformed JSON, missing
/// required coordinates, or fewer than three points in a polygon. Unlike an unconfigured
/// (<see langword="null"/>) field, which means "no geometry guard was ever set up" and safely
/// skips that check, a configured-but-broken value is a data-integrity problem on an
/// authoritative safety envelope and must fail closed rather than silently disable the guard.
/// </summary>
public sealed class InvalidSafetyGeometryException : Exception
{
    public InvalidSafetyGeometryException()
    {
    }

    public InvalidSafetyGeometryException(string message)
        : base(message)
    {
    }

    public InvalidSafetyGeometryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
