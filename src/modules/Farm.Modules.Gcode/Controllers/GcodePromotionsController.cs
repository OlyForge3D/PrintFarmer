using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Security;
using Farm.Modules.Calibration.Controllers;
using Farm.Modules.Calibration.Services.Calibration;
using Farm.Modules.Calibration.Services.Gcode;
using Farm.Modules.Gcode.Services.Gcode;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Modules.Gcode.Controllers;

/// <summary>Request body for promoting a completed slicer artifact into the G-code library.</summary>
public sealed record GcodePromotionCreateRequest
{
    /// <summary>Completed slicer artifact to promote.</summary>
    public Guid SourceArtifactId { get; init; }

    /// <summary>Slice job the caller believes produced the artifact.</summary>
    public Guid SourceSliceJobId { get; init; }

    /// <summary>SHA-256 (hex) the caller verified for the artifact bytes.</summary>
    public string ExpectedSha256 { get; init; } = string.Empty;

    /// <summary>Byte count the caller verified for the artifact bytes.</summary>
    public long ExpectedSizeBytes { get; init; }

    /// <summary>Canonical artifact kind; only <c>gcode</c> is promotable.</summary>
    public string ArtifactKind { get; init; } = "gcode";

    /// <summary>Worker the caller believes produced the artifact.</summary>
    public Guid? SourceWorkerId { get; init; }

    /// <summary>Calibration project the promotion belongs to.</summary>
    public Guid? CalibrationProjectId { get; init; }

    /// <summary>Calibration attempt the promotion belongs to.</summary>
    public Guid? CalibrationAttemptId { get; init; }

    /// <summary>Durable orchestration requesting the promotion.</summary>
    public Guid? CalibrationOrchestrationId { get; init; }

    /// <summary>Virtual library directory that receives the promoted file.</summary>
    public string? VirtualDirectory { get; init; }
}

/// <summary>Explicit user-action request to save a staged slice artifact to the library.</summary>
public sealed record SliceArtifactPromotionRequest
{
    /// <summary>Completed slice job identifier.</summary>
    public Guid SliceJobId { get; init; }

    /// <summary>G-code artifact identifier asserted by the caller.</summary>
    public Guid ArtifactId { get; init; }
}

/// <summary>
/// Authenticated promotion boundary for turning completed slicer artifacts into library G-code.
/// </summary>
/// <remarks>
/// Bytes are streamed server side; this route never returns a storage path, a private URL or a worker
/// credential, and it never accepts client-supplied content.
/// </remarks>
[ApiController]
[Route("api/gcode-promotions")]
[Authorize]
public sealed class GcodePromotionsController(
    IGcodeArtifactPromoter promoter,
    ISliceArtifactLibraryService sliceArtifactLibrary) : CalibrationControllerBase
{
    private readonly IGcodeArtifactPromoter _promoter = promoter ?? throw new ArgumentNullException(nameof(promoter));
    private readonly ISliceArtifactLibraryService _sliceArtifactLibrary =
        sliceArtifactLibrary ?? throw new ArgumentNullException(nameof(sliceArtifactLibrary));

    /// <summary>Explicitly saves one staged slice artifact to the durable farm-wide library.</summary>
    /// <param name="request">Authoritative slice job and artifact identifiers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The durable file identity, display name, and replay status.</returns>
    [HttpPost("slice-artifact")]
    [RequirePermission(PrintFarmerPermissions.Slicing.Promote)]
    [ProducesResponseType(typeof(SliceArtifactLibraryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(SliceArtifactLibraryResult), StatusCodes.Status201Created)]
    public async Task<IActionResult> PromoteSliceArtifactAsync(
        [FromBody] SliceArtifactPromotionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        CalibrationApiResult<SliceArtifactLibraryResult> result =
            await _sliceArtifactLibrary.PromoteAsync(
                request.SliceJobId,
                request.ArtifactId,
                actor,
                cancellationToken);
        return !result.IsSuccess || result.Value is null
            ? Problem(result.StatusCode, result.Code ?? "promotion_operation_failed")
            : StatusCode(result.StatusCode, result.Value);
    }

    /// <summary>Promotes an artifact, or replays the stable result of an identical earlier request.</summary>
    /// <param name="request">The immutable promotion request.</param>
    /// <param name="idempotencyKey">The operation key supplied through the <c>Idempotency-Key</c> header.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The durable promotion result.</returns>
    [HttpPost]
    [RequirePermission(PrintFarmerPermissions.Slicing.Promote)]
    public async Task<IActionResult> PromoteAsync(
        [FromBody] GcodePromotionCreateRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Problem(StatusCodes.Status400BadRequest, "idempotency_key_required");
        }

        CalibrationApiResult<GcodePromotionDto> result = await _promoter.PromoteAsync(
            new GcodeArtifactPromotionRequest
            {
                OperationId = idempotencyKey,
                SourceArtifactId = request.SourceArtifactId,
                SourceSliceJobId = request.SourceSliceJobId,
                ExpectedSha256 = request.ExpectedSha256,
                ExpectedSizeBytes = request.ExpectedSizeBytes,
                ArtifactKind = request.ArtifactKind,
                SourceWorkerId = request.SourceWorkerId,
                CalibrationProjectId = request.CalibrationProjectId,
                CalibrationAttemptId = request.CalibrationAttemptId,
                CalibrationOrchestrationId = request.CalibrationOrchestrationId,
                VirtualDirectory = request.VirtualDirectory,
            },
            actor,
            cancellationToken);
        return PromotionResult(result);
    }

    /// <summary>Returns the durable promotion recorded for an operation key.</summary>
    /// <param name="operationId">The idempotency operation key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The durable promotion result.</returns>
    [HttpGet("{operationId}")]
    [RequirePermission(PrintFarmerPermissions.Slicing.Promote)]
    public async Task<IActionResult> GetPromotionAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        return actor is null
            ? AuthenticationProblem()
            : PromotionResult(await _promoter.GetPromotionAsync(operationId, actor, cancellationToken));
    }

    private IActionResult PromotionResult(CalibrationApiResult<GcodePromotionDto> result)
    {
        if (!result.IsSuccess || result.Value is null)
        {
            return Problem(result.StatusCode, result.Code ?? "promotion_operation_failed");
        }

        if (result.Replayed)
        {
            Response.Headers["X-Calibration-Replayed"] = "true";
        }

        return StatusCode(result.StatusCode, result.Value);
    }
}
