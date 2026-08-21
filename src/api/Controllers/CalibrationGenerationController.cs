using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Security;
using Farm.Web.Api.Contracts;
using Farm.Web.Api.Infrastructure;
using Farm.Web.Api.Services.Calibration;
using Farm.Web.Api.Services.Calibration.Generation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Starts the durable calibration generation saga of an immutable attempt.
/// </summary>
/// <remarks>
/// The route accepts nothing but versioned typed method options. It never accepts a command line, a
/// G-code fragment, a slicer setting, a path, a URL, a renderer, an archive or a mesh, and it never
/// returns one either.
/// </remarks>
[ApiController]
[Route("api/calibration-projects")]
[Authorize]
[CalibrationApiContract]
public sealed class CalibrationGenerationController(ICalibrationGenerationSaga saga)
    : CalibrationControllerBase
{
    private readonly ICalibrationGenerationSaga _saga = saga ?? throw new ArgumentNullException(nameof(saga));

    /// <summary>
    /// Starts, resumes or replays the generation run of one immutable attempt.
    /// </summary>
    /// <param name="projectId">Owning calibration project.</param>
    /// <param name="attemptId">Immutable calibration attempt.</param>
    /// <param name="request">The typed generation request.</param>
    /// <param name="idempotencyKey">The operation key supplied through the <c>Idempotency-Key</c> header.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>202</c> with the durable status route for a new or resumed run, <c>200</c> for an exact
    /// replay, <c>409</c>, <c>412</c>, <c>422</c> or <c>503</c> as described by the saga contract.
    /// </returns>
    [HttpPost("{projectId:guid}/attempts/{attemptId:guid}/generate-job")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Generate)]
    [RequirePermission(PrintFarmerPermissions.Slicing.Submit)]
    public async Task<IActionResult> GenerateJobAsync(
        Guid projectId,
        Guid attemptId,
        [FromBody] CalibrationGenerateJobRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        // Final-verification options reference an existing library model by ID (issue #1770 follow-up:
        // Model3DStorageResolver no longer enforces uploader ownership, so any authenticated caller can
        // resolve any stored model). A Desktop-exchange token that only carries Calibration.Generate +
        // Slicing.Submit scope must not be able to use that as a back door to reference a library model
        // it was never granted ModelRead/LibrarySync access to - the same guard applied to POST
        // /api/slice and the legacy /api/slicer/slice-model/{modelId} route.
        if (request.Options?.Model3DId is Guid &&
            DesktopScopeClaims.IsMissingModelScope(User))
        {
            return Problem(StatusCodes.Status403Forbidden, "resource_forbidden");
        }

        CalibrationApiResult<CalibrationOrchestrationStatusDto> result = await _saga.CreateOrResumeAsync(
            projectId,
            attemptId,
            idempotencyKey,
            request,
            actor,
            cancellationToken);
        return OrchestrationResult(result);
    }
}

/// <summary>
/// Reports the durable, redacted status of a calibration generation orchestration.
/// </summary>
/// <remarks>
/// The document contains identifiers, digests, counters and timestamps only. Storage paths, worker
/// endpoints, credentials, private URLs and raw slicer logs are never part of it.
/// </remarks>
[ApiController]
[Route("api/calibration-orchestrations")]
[Authorize]
[CalibrationApiContract]
public sealed class CalibrationOrchestrationsController(ICalibrationGenerationSaga saga)
    : CalibrationControllerBase
{
    private readonly ICalibrationGenerationSaga _saga = saga ?? throw new ArgumentNullException(nameof(saga));

    /// <summary>Returns the durable status of one orchestration.</summary>
    /// <param name="id">The durable orchestration identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The redacted durable status.</returns>
    [HttpGet("{id:guid}")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Read)]
    public async Task<IActionResult> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        return actor is null
            ? AuthenticationProblem()
            : OrchestrationResult(await _saga.GetStatusAsync(id, actor, cancellationToken));
    }
}
