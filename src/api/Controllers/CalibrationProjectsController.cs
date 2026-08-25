using System.Security.Claims;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Security;
using Farm.Web.Api.Contracts;
using Farm.Web.Api.Infrastructure;
using Farm.Web.Api.Services.Calibration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Farm.Web.Api.Controllers;

/// <summary>Provides authenticated persistence and history APIs for calibration projects.</summary>
[ApiController]
[Route("api/calibration-projects")]
[Authorize]
[CalibrationApiContract]
public sealed class CalibrationProjectsController(ICalibrationProjectService calibrationService)
    : CalibrationControllerBase
{
    private readonly ICalibrationProjectService _calibrationService =
        calibrationService ?? throw new ArgumentNullException(nameof(calibrationService));

    /// <summary>Lists calibration projects visible to the authenticated caller.</summary>
    [HttpGet]
    [RequirePermission(PrintFarmerPermissions.Calibration.Read)]
    public async Task<IActionResult> GetProjectsAsync(
        [FromQuery] bool includeDeleted,
        CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        return Ok(await _calibrationService.GetProjectsAsync(actor, includeDeleted, cancellationToken));
    }

    /// <summary>Gets one calibration project with a strong revision ETag.</summary>
    [HttpGet("{projectId:guid}")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Read)]
    public async Task<IActionResult> GetProjectAsync(
        Guid projectId,
        [FromQuery] bool includeDeleted,
        CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        CalibrationApiResult<CalibrationProjectDto> result = await _calibrationService.GetProjectAsync(
            projectId,
            actor,
            includeDeleted,
            cancellationToken);
        return ProjectResult(result);
    }

    /// <summary>Creates an idempotent project after server-side printer-context capture.</summary>
    [HttpPost]
    [RequirePermission(PrintFarmerPermissions.Calibration.Create)]
    public async Task<IActionResult> CreateProjectAsync(
        [FromBody] CalibrationProjectCreateRequest request,
        CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        CalibrationApiResult<CalibrationProjectDto> result = await _calibrationService.CreateProjectAsync(
            request,
            actor,
            cancellationToken);
        return ProjectResult(result);
    }

    /// <summary>Updates editable project state only when the supplied revision preconditions match.</summary>
    [HttpPatch("{projectId:guid}")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Update)]
    public async Task<IActionResult> UpdateProjectAsync(
        Guid projectId,
        [FromBody] CalibrationProjectUpdateRequest request,
        CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        CalibrationApiResult<CalibrationProjectDto> result = await _calibrationService.UpdateProjectAsync(
            projectId,
            request,
            Request.Headers.IfMatch.ToString(),
            actor,
            cancellationToken);
        return ProjectResult(result);
    }

    /// <summary>Soft-deletes a project and emits a synchronization tombstone.</summary>
    [HttpDelete("{projectId:guid}")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Delete)]
    public async Task<IActionResult> DeleteProjectAsync(
        Guid projectId,
        [FromQuery] long? baseRevision,
        CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        CalibrationApiResult<CalibrationProjectDto> result = await _calibrationService.DeleteProjectAsync(
            projectId,
            baseRevision,
            Request.Headers.IfMatch.ToString(),
            actor,
            cancellationToken);
        return ProjectResult(result);
    }

    /// <summary>Creates or updates a device-lineage draft using explicit optimistic concurrency.</summary>
    [HttpPut("{projectId:guid}/drafts/{stepId}")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Update)]
    public async Task<IActionResult> UpsertDraftAsync(
        Guid projectId,
        string stepId,
        [FromBody] CalibrationDraftUpsertRequest request,
        CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        CalibrationApiResult<CalibrationDraftDto> result = await _calibrationService.UpsertDraftAsync(
            projectId,
            stepId,
            request,
            Request.Headers.IfMatch.ToString(),
            actor,
            cancellationToken);
        return DraftResult(result);
    }

    /// <summary>Soft-deletes a draft and emits a tombstone for offline devices.</summary>
    [HttpDelete("{projectId:guid}/drafts/{stepId}")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Delete)]
    public async Task<IActionResult> DeleteDraftAsync(
        Guid projectId,
        string stepId,
        [FromQuery] string deviceLineageId,
        [FromQuery] long? baseRevision,
        CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        CalibrationApiResult<CalibrationDraftDto> result = await _calibrationService.DeleteDraftAsync(
            projectId,
            stepId,
            deviceLineageId,
            baseRevision,
            Request.Headers.IfMatch.ToString(),
            actor,
            cancellationToken);
        return DraftResult(result);
    }

    /// <summary>Lists immutable plans belonging to a project.</summary>
    [HttpGet("{projectId:guid}/attempts")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Read)]
    public async Task<IActionResult> GetAttemptsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        return Ok(await _calibrationService.GetAttemptsAsync(projectId, actor, cancellationToken));
    }

    /// <summary>Appends an immutable attempt plan and initial durable orchestration checkpoint.</summary>
    [HttpPost("{projectId:guid}/attempts")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Create)]
    public async Task<IActionResult> CreateAttemptAsync(
        Guid projectId,
        [FromBody] CalibrationAttemptCreateRequest request,
        CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        CalibrationApiResult<CalibrationAttemptDto> result = await _calibrationService.CreateAttemptAsync(
            projectId,
            request,
            actor,
            cancellationToken);
        return AttemptResult(result);
    }
}

/// <summary>Provides authenticated append-only attempt, observation, and photo APIs.</summary>
[ApiController]
[Route("api/calibration-attempts")]
[Authorize]
[CalibrationApiContract]
public sealed class CalibrationAttemptsController(ICalibrationProjectService calibrationService)
    : CalibrationControllerBase
{
    private readonly ICalibrationProjectService _calibrationService =
        calibrationService ?? throw new ArgumentNullException(nameof(calibrationService));

    /// <summary>Gets an immutable attempt plan and deterministic projected lifecycle status.</summary>
    [HttpGet("{attemptId:guid}")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Read)]
    public async Task<IActionResult> GetAttemptAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        return AttemptResult(await _calibrationService.GetAttemptAsync(attemptId, actor, cancellationToken));
    }

    /// <summary>Appends an idempotent lifecycle fact to an attempt.</summary>
    [HttpPost("{attemptId:guid}/events")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Update)]
    public async Task<IActionResult> AppendEventAsync(
        Guid attemptId,
        [FromBody] CalibrationAttemptEventCreateRequest request,
        CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        return EventResult(await _calibrationService.AppendAttemptEventAsync(
            attemptId,
            request,
            actor,
            cancellationToken));
    }

    /// <summary>Appends an idempotent immutable measurement or operator observation.</summary>
    [HttpPost("{attemptId:guid}/observations")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Update)]
    public async Task<IActionResult> AppendObservationAsync(
        Guid attemptId,
        [FromBody] CalibrationObservationCreateRequest request,
        CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        return ObservationResult(await _calibrationService.AppendObservationAsync(
            attemptId,
            request,
            actor,
            cancellationToken));
    }

    /// <summary>Lists private photo metadata for an attempt.</summary>
    [HttpGet("{attemptId:guid}/photos")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Read)]
    public async Task<IActionResult> GetPhotosAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        return Ok(await _calibrationService.GetPhotosAsync(attemptId, actor, cancellationToken));
    }

    /// <summary>Uploads, validates, strips, and privately stores a calibration photo.</summary>
    [HttpPost("{attemptId:guid}/photos")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Update)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadPhotoAsync(
        Guid attemptId,
        IFormFile file,
        [FromForm] string clientUploadId,
        [FromForm] DateTime? capturedAtUtc,
        [FromForm] string? caption,
        [FromForm] int sortOrder,
        CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        if (file is null)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "photo_file_missing");
        }

        await using Stream content = file.OpenReadStream();
        return PhotoResult(await _calibrationService.UploadPhotoAsync(
            attemptId,
            clientUploadId,
            file.FileName,
            file.ContentType,
            capturedAtUtc,
            caption,
            sortOrder,
            content,
            actor,
            cancellationToken));
    }
}

/// <summary>Provides owner-authorized private photo access without exposing storage details.</summary>
[ApiController]
[Route("api/calibration-photos")]
[Authorize]
[CalibrationApiContract]
public sealed class CalibrationPhotosController(ICalibrationProjectService calibrationService)
    : CalibrationControllerBase
{
    private readonly ICalibrationProjectService _calibrationService =
        calibrationService ?? throw new ArgumentNullException(nameof(calibrationService));

    /// <summary>Gets safe private-photo metadata.</summary>
    [HttpGet("{photoId:guid}")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Read)]
    public async Task<IActionResult> GetPhotoAsync(Guid photoId, CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        return PhotoResult(await _calibrationService.GetPhotoAsync(
            photoId,
            actor,
            false,
            cancellationToken));
    }

    /// <summary>Streams authorized photo bytes through an authenticated endpoint.</summary>
    [HttpGet("{photoId:guid}/content")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Read)]
    public async Task<IActionResult> GetPhotoContentAsync(Guid photoId, CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        CalibrationApiResult<CalibrationPhotoDto> metadata = await _calibrationService.GetPhotoAsync(
            photoId,
            actor,
            false,
            cancellationToken);
        if (!metadata.IsSuccess || metadata.Value is null)
        {
            return PhotoResult(metadata);
        }

        try
        {
            Stream content = await _calibrationService.OpenPhotoAsync(photoId, actor, cancellationToken);
            Response.Headers.ETag = $"\"{metadata.Value.Sha256}\"";
            string extension = metadata.Value.ContentType switch
            {
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                "image/webp" => ".webp",
                _ => string.Empty,
            };
            return File(
                content,
                metadata.Value.ContentType,
                $"calibration-photo-{metadata.Value.Id:N}{extension}",
                enableRangeProcessing: true);
        }
        catch (FileNotFoundException)
        {
            return Problem(StatusCodes.Status404NotFound, "photo_content_not_found");
        }
    }

    /// <summary>Updates presentation metadata with a strong photo ETag precondition.</summary>
    [HttpPatch("{photoId:guid}")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Update)]
    public async Task<IActionResult> UpdatePhotoAsync(
        Guid photoId,
        [FromBody] CalibrationPhotoUpdateRequest request,
        CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        return PhotoResult(await _calibrationService.UpdatePhotoAsync(
            photoId,
            request,
            Request.Headers.IfMatch.ToString(),
            actor,
            cancellationToken));
    }

    /// <summary>Marks a photo for two-phase deletion and reconciles bytes when possible.</summary>
    [HttpDelete("{photoId:guid}")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Delete)]
    public async Task<IActionResult> DeletePhotoAsync(
        Guid photoId,
        [FromQuery] long? baseRevision,
        CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        return PhotoResult(await _calibrationService.DeletePhotoAsync(
            photoId,
            baseRevision,
            Request.Headers.IfMatch.ToString(),
            actor,
            cancellationToken));
    }
}

/// <summary>Provides the authoritative cursor-based calibration synchronization protocol.</summary>
[ApiController]
[Route("api/calibration-sync")]
[Authorize]
[CalibrationApiContract]
public sealed class CalibrationSyncController(ICalibrationProjectService calibrationService)
    : CalibrationControllerBase
{
    private readonly ICalibrationProjectService _calibrationService =
        calibrationService ?? throw new ArgumentNullException(nameof(calibrationService));

    /// <summary>Gets a strictly ordered, scope-isolated calibration change page.</summary>
    [HttpGet("changes")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Read)]
    public async Task<IActionResult> GetChangesAsync(
        [FromQuery] string? after,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        return ChangesResult(await _calibrationService.GetChangesAsync(
            after,
            limit,
            actor,
            cancellationToken));
    }

    /// <summary>Applies ordered client mutations and returns a result for every item.</summary>
    [HttpPost("apply")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Update)]
    public async Task<IActionResult> ApplyAsync(
        [FromBody] CalibrationSyncApplyRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Mutations.Any(mutation =>
                string.Equals(mutation.OperationType?.Trim(), "project.delete", StringComparison.Ordinal)) &&
            !PrintFarmerPermissions.HasPermission(User, PrintFarmerPermissions.Calibration.Delete))
        {
            return Problem(StatusCodes.Status403Forbidden, "permission_denied");
        }

        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        return Ok(await _calibrationService.ApplyChangesAsync(request, actor, cancellationToken));
    }
}

/// <summary>Provides preview-first, transactional legacy-v4 import contracts.</summary>
[ApiController]
[Route("api/calibration-imports")]
[Authorize]
[CalibrationApiContract]
public sealed class CalibrationImportsController(ICalibrationProjectService calibrationService)
    : CalibrationControllerBase
{
    private readonly ICalibrationProjectService _calibrationService =
        calibrationService ?? throw new ArgumentNullException(nameof(calibrationService));

    /// <summary>Previews or commits a validated legacy calibration import.</summary>
    [HttpPost("legacy-v4")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Create)]
    public async Task<IActionResult> ImportLegacyV4Async(
        [FromBody] LegacyCalibrationImportRequest request,
        CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        return ImportResult(await _calibrationService.ImportLegacyV4Async(
            request,
            actor,
            cancellationToken));
    }
}

/// <summary>Shared authorization, ETag, and safe problem rendering for calibration controllers.</summary>
public abstract class CalibrationControllerBase : ControllerBase
{
    /// <summary>Builds an actor only from authenticated claim values.</summary>
    protected CalibrationActor? GetActor()
    {
        if (!PrintFarmerPermissions.TryGetUserId(User, out Guid userId))
        {
            return null;
        }

        string subject =
            User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            User.FindFirstValue("sub") ??
            userId.ToString();
        return new(userId, subject, PrintFarmerPermissions.IsFarmAdmin(User));
    }

    /// <summary>Renders a consistent authentication problem.</summary>
    protected IActionResult AuthenticationProblem() => Problem(
        StatusCodes.Status401Unauthorized,
        "authentication_required");

    /// <summary>Renders a consistent calibration problem without internal exception details.</summary>
    protected IActionResult Problem(int statusCode, string code, CalibrationRevisionConflictDto? conflict = null)
    {
        ProblemDetails details = new()
        {
            Status = statusCode,
            Title = code.Replace('_', ' '),
            Type = $"https://printfarmer.dev/problems/{code}",
            Instance = HttpContext.Request.Path,
        };
        details.Extensions["code"] = code;
        if (conflict is not null)
        {
            details.Extensions["conflict"] = conflict;
        }

        return StatusCode(statusCode, details);
    }

    /// <summary>Maps a project operation result and emits a strong ETag on success.</summary>
    protected IActionResult ProjectResult(CalibrationApiResult<CalibrationProjectDto> result)
    {
        if (!result.IsSuccess || result.Value is null)
        {
            return Problem(result.StatusCode, result.Code ?? "calibration_operation_failed", result.Conflict);
        }

        SetReplayHeader(result);
        SetETag("project", result.Value.Id, result.Value.Revision);
        return StatusCode(result.StatusCode, result.Value);
    }

    /// <summary>Maps a draft operation result and emits a strong ETag on success.</summary>
    protected IActionResult DraftResult(CalibrationApiResult<CalibrationDraftDto> result)
    {
        if (!result.IsSuccess || result.Value is null)
        {
            return Problem(result.StatusCode, result.Code ?? "calibration_operation_failed", result.Conflict);
        }

        SetReplayHeader(result);
        SetETag("draft", result.Value.Id, result.Value.Revision);
        return StatusCode(result.StatusCode, result.Value);
    }

    /// <summary>Maps an immutable attempt operation result.</summary>
    protected IActionResult AttemptResult(CalibrationApiResult<CalibrationAttemptDto> result) =>
        Result(result);

    /// <summary>Maps an immutable lifecycle-event operation result.</summary>
    protected IActionResult EventResult(CalibrationApiResult<CalibrationAttemptEventDto> result) =>
        Result(result);

    /// <summary>Maps an immutable observation operation result.</summary>
    protected IActionResult ObservationResult(CalibrationApiResult<CalibrationObservationDto> result) =>
        Result(result);

    /// <summary>Maps a photo operation result and emits a strong ETag on success.</summary>
    protected IActionResult PhotoResult(CalibrationApiResult<CalibrationPhotoDto> result)
    {
        if (!result.IsSuccess || result.Value is null)
        {
            return Problem(result.StatusCode, result.Code ?? "calibration_operation_failed", result.Conflict);
        }

        SetReplayHeader(result);
        SetETag("photo", result.Value.Id, result.Value.Revision);
        return StatusCode(result.StatusCode, result.Value);
    }

    /// <summary>Maps change feed responses.</summary>
    protected IActionResult ChangesResult(CalibrationApiResult<CalibrationChangesResponse> result) =>
        Result(result);

    /// <summary>Maps legacy import results.</summary>
    protected IActionResult ImportResult(CalibrationApiResult<LegacyCalibrationImportResultDto> result) =>
        Result(result);

    /// <summary>Maps a filament-calibration saga orchestration operation result.</summary>
    protected IActionResult OrchestrationResult(CalibrationApiResult<CalibrationOrchestrationDto> result) =>
        Result(result);

    private IActionResult Result<T>(CalibrationApiResult<T> result)
    {
        if (!result.IsSuccess || result.Value is null)
        {
            return Problem(result.StatusCode, result.Code ?? "calibration_operation_failed", result.Conflict);
        }

        SetReplayHeader(result);
        return StatusCode(result.StatusCode, result.Value);
    }

    private void SetETag(string resourceType, Guid id, long revision) =>
        Response.Headers.ETag = $"\"calibration-{resourceType}-{id:N}-{revision}\"";

    private void SetReplayHeader<T>(CalibrationApiResult<T> result)
    {
        if (result.Replayed)
        {
            Response.Headers["X-Calibration-Replayed"] = "true";
        }
    }
}
