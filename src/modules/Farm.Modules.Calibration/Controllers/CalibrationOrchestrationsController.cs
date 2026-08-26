using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Security;
using Farm.Web.Api.Contracts;
using Farm.Web.Api.Infrastructure;
using Farm.Web.Api.Services.Calibration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Read and advance API for the filament-calibration saga's durable
/// <see cref="Farm.Infrastructure.Domain.CalibrationOrchestration"/> checkpoint.
/// </summary>
/// <remarks>
/// The orchestration row this controller exposes is always created up front by
/// <see cref="ICalibrationProjectService.CreateAttemptAsync"/> - it is a byproduct record of an
/// attempt's progress, never a precondition an operator must satisfy before starting a
/// calibration. Advancing the saga never blocks on anything beyond what the flow itself already
/// requires (a submitted slice, a dispatched print, a recorded measurement).
/// </remarks>
[ApiController]
[Route("api/calibration-orchestrations")]
[Authorize]
[CalibrationApiContract]
public sealed class CalibrationOrchestrationsController(ICalibrationOrchestrationSagaService sagaService)
    : CalibrationControllerBase
{
    private readonly ICalibrationOrchestrationSagaService _sagaService =
        sagaService ?? throw new ArgumentNullException(nameof(sagaService));

    /// <summary>Gets the current saga checkpoint for one orchestration.</summary>
    [HttpGet("{orchestrationId:guid}")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Read)]
    public async Task<IActionResult> GetAsync(Guid orchestrationId, CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        return OrchestrationResult(await _sagaService.GetAsync(orchestrationId, actor, cancellationToken));
    }

    /// <summary>Gets the current saga checkpoint by its owning attempt instead of its own ID.</summary>
    [HttpGet("by-attempt/{attemptId:guid}")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Read)]
    public async Task<IActionResult> GetByAttemptAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        return OrchestrationResult(await _sagaService.GetByAttemptAsync(attemptId, actor, cancellationToken));
    }

    /// <summary>
    /// Drives the saga one step forward from its current checkpoint. Safe to call repeatedly:
    /// a step that is still waiting (for a slice, a print, or a measurement) is a no-op poll, and a
    /// step already <c>completed</c> or terminally <c>failed</c> is answered from the current
    /// checkpoint without re-running anything.
    /// </summary>
    [HttpPost("{orchestrationId:guid}/advance")]
    [RequirePermission(PrintFarmerPermissions.Calibration.Update)]
    public async Task<IActionResult> AdvanceAsync(
        Guid orchestrationId,
        [FromBody] CalibrationOrchestrationAdvanceRequest request,
        CancellationToken cancellationToken)
    {
        CalibrationActor? actor = GetActor();
        if (actor is null)
        {
            return AuthenticationProblem();
        }

        return OrchestrationResult(
            await _sagaService.AdvanceAsync(orchestrationId, request, actor, cancellationToken));
    }
}
