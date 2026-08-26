using Farm.OrcaSlicer.Worker.Services;
using Microsoft.AspNetCore.Mvc;

namespace Farm.OrcaSlicer.Worker.Controllers;

/// <summary>
/// Manages rendered custom profile bundles owned by this worker version.
/// </summary>
[ApiController]
[RequireWorkerSharedKey]
[Route("api/profiles/custom-bundles")]
[Tags("Custom Slicer Profiles")]
public sealed class CustomProfilesController(
    CustomProfileBundleStore bundleStore,
    CachedOrcaProfilesService profilesService,
    ILogger<CustomProfilesController> logger) : ControllerBase
{
    private readonly CustomProfileBundleStore _bundleStore =
        bundleStore ?? throw new ArgumentNullException(nameof(bundleStore));

    private readonly CachedOrcaProfilesService _profilesService =
        profilesService ?? throw new ArgumentNullException(nameof(profilesService));

    private readonly ILogger<CustomProfilesController> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Installs or replaces a complete rendered custom manufacturer bundle and
    /// performs an in-process profile reload.
    /// </summary>
    /// <param name="bundleName">Custom manufacturer bundle name.</param>
    /// <param name="request">Rendered manifest and profile files.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Reload counts and any hard custom inheritance failures.</returns>
    [HttpPut("{bundleName}")]
    [ProducesResponseType(typeof(CustomProfileMutationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(CustomProfileMutationResponse), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<CustomProfileMutationResponse>> InstallAsync(
        string bundleName,
        [FromBody] CustomProfileBundleRequest request,
        CancellationToken ct)
    {
        try
        {
            await _bundleStore.InstallAsync(bundleName, request, ct);
            ProfileReloadResult reload = await _profilesService.ReloadProfilesAsync(ct);
            return ReloadResult(
                new CustomProfileMutationResponse(
                    "installed",
                    bundleName,
                    reload.MachineCount,
                    reload.FilamentCount,
                    reload.ProcessCount,
                    reload.Failures));
        }
        catch (CustomProfileBundleException ex)
        {
            return BundleProblem(ex);
        }
    }

    /// <summary>
    /// Removes a custom manufacturer bundle and performs an in-process profile
    /// reload.
    /// </summary>
    /// <param name="bundleName">Custom manufacturer bundle name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Reload counts and any remaining hard custom inheritance failures.</returns>
    [HttpDelete("{bundleName}")]
    [ProducesResponseType(typeof(CustomProfileMutationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(CustomProfileMutationResponse), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<CustomProfileMutationResponse>> RemoveAsync(
        string bundleName,
        CancellationToken ct)
    {
        try
        {
            if (!await _bundleStore.RemoveAsync(bundleName, ct))
            {
                return Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Custom profile bundle not found",
                    detail: $"Custom bundle '{bundleName}' is not installed.");
            }

            ProfileReloadResult reload = await _profilesService.ReloadProfilesAsync(ct);
            return ReloadResult(
                new CustomProfileMutationResponse(
                    "removed",
                    bundleName,
                    reload.MachineCount,
                    reload.FilamentCount,
                    reload.ProcessCount,
                    reload.Failures));
        }
        catch (CustomProfileBundleException ex)
        {
            return BundleProblem(ex);
        }
    }

    /// <summary>
    /// Clears SQLite and every process-lifetime Orca profile cache, then waits
    /// for a complete in-process rebuild.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Reload counts and any hard custom inheritance failures.</returns>
    [HttpPost("~/api/profiles/cache/reload")]
    [ProducesResponseType(typeof(CustomProfileMutationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(CustomProfileMutationResponse), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<CustomProfileMutationResponse>> ReloadAsync(
        CancellationToken ct)
    {
        ProfileReloadResult reload = await _profilesService.ReloadProfilesAsync(ct);
        return ReloadResult(
            new CustomProfileMutationResponse(
                "reloaded",
                null,
                reload.MachineCount,
                reload.FilamentCount,
                reload.ProcessCount,
                reload.Failures));
    }

    private ActionResult<CustomProfileMutationResponse> ReloadResult(
        CustomProfileMutationResponse response)
    {
        if (response.Failures.Count == 0)
        {
            return Ok(response);
        }

        _logger.LogWarning(
            "Custom profile reload excluded {FailureCount} profiles with unavailable source presets",
            response.Failures.Count);
        return UnprocessableEntity(response);
    }

    private ActionResult<CustomProfileMutationResponse> BundleProblem(
        CustomProfileBundleException exception)
    {
        int statusCode = exception.Code switch
        {
            "stock_bundle_conflict" or "overlay_path_conflict" =>
                StatusCodes.Status409Conflict,
            "custom_profiles_unavailable" =>
                StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };
        return StatusCode(statusCode, new ProblemDetails
        {
            Status = statusCode,
            Title = "Custom profile bundle rejected",
            Detail = exception.Message,
            Extensions =
            {
                ["code"] = exception.Code,
            },
        });
    }
}

/// <summary>
/// Result of a custom profile mutation or explicit reload.
/// </summary>
/// <param name="Operation">Completed operation.</param>
/// <param name="BundleName">Affected bundle, or null for a standalone reload.</param>
/// <param name="MachineCount">Selectable machine profile count.</param>
/// <param name="FilamentCount">Filament profile count.</param>
/// <param name="ProcessCount">Process profile count.</param>
/// <param name="Failures">Custom profiles excluded because a parent was unavailable.</param>
public sealed record CustomProfileMutationResponse(
    string Operation,
    string? BundleName,
    int MachineCount,
    int FilamentCount,
    int ProcessCount,
    IReadOnlyList<CustomProfileLoadFailure> Failures);
