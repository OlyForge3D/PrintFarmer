using Farm.Infrastructure.PrinterCalibration;
using Farm.Slicer.Module.Api.Filters;
using Farm.Slicer.Module.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Slicer.Module.Api.Controllers.Calibration;

/// <summary>
/// Serves worker/version compatibility for the main API's calibration generation capability probe
/// from the worker registry this host owns (issue #1848).
/// </summary>
/// <remarks>
/// <para>
/// Split and microservices deployments never register an in-process
/// <c>IDbContextFactory&lt;SlicerDbContext&gt;</c> for the main API, because this host owns the worker
/// registry. Without this endpoint the API's capability probe always returned an empty snapshot,
/// misreporting <c>calibrationGenerationEnabled: false</c> even when a fully attested worker was
/// online and ready.
/// </para>
/// <para>
/// Security: this is a service-to-service hop, not an end-user request, so it is guarded by the
/// shared worker-authentication key (<c>WorkerAuth:SharedKey</c>) rather than a forwarded JWT — the
/// same mechanism <c>Farm.Slicer.Module.Api.Controllers.SlicersController</c> already uses for its
/// worker-facing endpoints.
/// </para>
/// </remarks>
[ApiController]
[Route(WorkerCompatibilityContract.RoutePrefix)]
[Tags("Calibration Capabilities")]
public sealed class WorkerCompatibilityController(
    ISlicerHostWorkerCompatibilityService compatibilityService)
    : ControllerBase
{
    private readonly ISlicerHostWorkerCompatibilityService _compatibilityService =
        compatibilityService ?? throw new ArgumentNullException(nameof(compatibilityService));

    /// <summary>Reports the eligible pinned worker identity and observed upstream versions.</summary>
    /// <param name="requiredSlicerVersion">
    /// An optional exact slicer version the eligible worker must report.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The worker compatibility snapshot.</returns>
    [HttpGet(WorkerCompatibilityContract.WorkerCompatibilityActionRoute)]
    [RequireSlicerApiKey]
    [AllowAnonymous] // Public to JWT auth; the main API authenticates with the shared worker key.
    [ProducesResponseType(typeof(WorkerCompatibilitySnapshotDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetWorkerCompatibilityAsync(
        [FromQuery(Name = WorkerCompatibilityContract.RequiredSlicerVersionQueryParam)]
        string? requiredSlicerVersion,
        CancellationToken cancellationToken)
    {
        WorkerCompatibilitySnapshotDto snapshot = await _compatibilityService.GetWorkerCompatibilityAsync(
            string.IsNullOrWhiteSpace(requiredSlicerVersion) ? null : requiredSlicerVersion,
            cancellationToken);
        return Ok(snapshot);
    }
}
