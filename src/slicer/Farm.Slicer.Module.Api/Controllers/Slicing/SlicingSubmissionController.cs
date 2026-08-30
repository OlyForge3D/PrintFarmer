using System.Security.Claims;
using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Api.Authorization;
using Farm.Slicer.Module.Api.Filters;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Slicer.Module.Api.Controllers.Slicing;

/// <summary>
/// Endpoints for submitting slicing jobs via the legacy submission service.
/// </summary>
/// <remarks>
/// Superseded by <c>POST /api/slice</c>, which is the canonical production contract. These routes
/// remain available for existing non-calibration callers and advertise their replacement, but they
/// must not be used for calibration work: they do not carry model identity, resolved profile
/// snapshots or lease fencing.
/// </remarks>
[ApiController]
[Route("api/slicer")]
[Tags("Slicer Submission")]
[DeprecatedSliceRoute(CanonicalSliceRoute, CanonicalSliceRouteSunset)]
public class SlicingSubmissionController(
    ISlicingSubmissionService submissionService,
    IPrinterAccessValidator? printerAccess = null) : ControllerBase
{
    /// <summary>The canonical replacement route advertised to callers.</summary>
    internal const string CanonicalSliceRoute = "/api/slice";

    /// <summary>Advertised sunset date for the superseded submission routes.</summary>
    internal const string CanonicalSliceRouteSunset = "Wed, 01 Jul 2026 00:00:00 GMT";

    private readonly ISlicingSubmissionService _submissionService = submissionService;
    private readonly IPrinterAccessValidator? _printerAccess = printerAccess;

    /// <summary>
    /// Submits a new file for slicing.
    /// </summary>
    /// <param name="file">The model file to slice.</param>
    /// <param name="slicerEngine">The slicer engine to use.</param>
    /// <param name="printerId">Target printer ID.</param>
    /// <param name="profileJson">Slicer profile JSON.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("slice")]
    [Authorize]
    [RequirePermission(PrintFarmerPermissions.Slicing.Submit)]
    public async Task<IActionResult> SubmitFileAsync(
        IFormFile file,
        [FromForm] string? slicerEngine,
        [FromForm] Guid? printerId,
        [FromForm] string? profileJson,
        CancellationToken ct)
    {
        if (file.Length == 0)
        {
            return BadRequest(new { error = "File is empty." });
        }

        Guid userId = GetUserId();
        if (userId == Guid.Empty ||
            (_printerAccess is not null &&
             !await _printerAccess.IsEnabledAsync(printerId, ct)))
        {
            return SlicerApiProblems.ResourceForbidden(this);
        }

        SlicerProfileDto profile = DeserializeProfile(profileJson);

        // Issue #2229: closes the same negative-value bypass as SliceJobController.SubmitAsync
        // for this legacy route's strongly-typed profile shape.
        if (!ProcessOverrideSettingsValidation.TryValidate(profile.ProcessProfile, out string? printSettingsError))
        {
            return BadRequest(new { error = printSettingsError });
        }

        SlicingSubmissionResult result = await _submissionService.SubmitSlicingJobAsync(
            file,
            slicerEngine ?? SlicerEngineType.OrcaSlicer.ToString(),
            printerId ?? Guid.Empty,
            profile,
            userId,
            ct);

        if (!result.Success)
        {
            return BadRequest(new { error = result.Error });
        }

        return Ok(result.Result);
    }

    /// <summary>
    /// Submits an existing model for slicing.
    /// </summary>
    /// <param name="modelId">The existing model ID.</param>
    /// <param name="slicerEngine">The slicer engine to use.</param>
    /// <param name="printerId">Target printer ID.</param>
    /// <param name="profileJson">Slicer profile JSON.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("slice-model/{modelId}")]
    [Authorize]
    [RequirePermission(PrintFarmerPermissions.Slicing.Submit)]
    public async Task<IActionResult> SubmitModelAsync(
        Guid modelId,
        [FromForm] string? slicerEngine,
        [FromForm] Guid? printerId,
        [FromForm] string? profileJson,
        CancellationToken ct)
    {
        Guid userId = GetUserId();
        if (userId == Guid.Empty ||
            (_printerAccess is not null &&
             !await _printerAccess.IsEnabledAsync(printerId, ct)))
        {
            return SlicerApiProblems.ResourceForbidden(this);
        }

        // This legacy route resolves any existing model by ID (Farm.Infrastructure.Authorization.
        // DesktopScopeClaims.IsMissingModelScope) with no ownership check of its own, and is
        // gated only by the broad slicing:submit permission - so without this guard a
        // Desktop-exchange token issued only for slicing (issue #838) could use it to slice/read
        // an arbitrary library model it was never granted ModelRead/LibrarySync access to, exactly
        // the bypass closed for POST /api/slice in issue #1770's follow-up.
        if (Farm.Infrastructure.Authorization.DesktopScopeClaims.IsMissingModelScope(User))
        {
            return SlicerApiProblems.ResourceForbidden(this);
        }

        SlicerProfileDto profile = DeserializeProfile(profileJson);

        // Issue #2229: closes the same negative-value bypass as SliceJobController.SubmitAsync
        // for this legacy route's strongly-typed profile shape.
        if (!ProcessOverrideSettingsValidation.TryValidate(profile.ProcessProfile, out string? printSettingsError))
        {
            return BadRequest(new { error = printSettingsError });
        }

        SlicingSubmissionResult result = await _submissionService.SubmitSlicingJobFromModelAsync(
            modelId,
            slicerEngine ?? SlicerEngineType.OrcaSlicer.ToString(),
            printerId ?? Guid.Empty,
            profile,
            userId,
            ct);

        if (!result.Success)
        {
            return BadRequest(new { error = result.Error });
        }

        return Ok(result.Result);
    }

    private Guid GetUserId()
    {
        string? userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(userIdStr, out Guid userId) ? userId : Guid.Empty;
    }

    private static SlicerProfileDto DeserializeProfile(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SlicerProfileDto();
        }

        return System.Text.Json.JsonSerializer.Deserialize<SlicerProfileDto>(json) ?? new SlicerProfileDto();
    }
}
