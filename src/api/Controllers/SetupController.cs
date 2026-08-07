using Farm.Infrastructure;
using Farm.Infrastructure.Services.Setup;
using Farm.Infrastructure.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for initial application setup and configuration.
/// Used during first-run to create initial admin user and configure the system.
/// </summary>
[ApiController]
[Route("api/setup")]
public class SetupController(ISetupService setupService, ISettingsService settingsService) : ControllerBase
{
    private readonly ISetupService _setupService = setupService;
    private readonly ISettingsService _settingsService = settingsService;

    /// <summary>
    /// Checks if the application needs initial setup.
    /// Returns true if no admin users exist in the system.
    /// </summary>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    [HttpGet("status")]
    [AllowAnonymous] // Public because an unconfigured installation has no account that can authenticate yet.
    public async Task<ActionResult<object>> GetSetupStatusAsync(CancellationToken ct)
    {
        bool needsSetup = await _setupService.NeedsSetupAsync(ct);
        return Ok(new { needsSetup });
    }

    /// <summary>
    /// Gets the non-secret deployment defaults needed by the first-run wizard.
    /// </summary>
    /// <remarks>
    /// This endpoint intentionally exposes only the Spoolman base URL and is unavailable after an
    /// administrator exists. The authenticated settings endpoint remains the canonical settings
    /// surface after setup.
    /// </remarks>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    [HttpGet("bootstrap")]
    [AllowAnonymous] // Public only while no account exists; the response contains one non-secret setup default.
    [ProducesResponseType<SetupBootstrapResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SetupBootstrapResponse>> GetBootstrapAsync(CancellationToken ct)
    {
        if (!await _setupService.NeedsSetupAsync(ct))
        {
            return NotFound();
        }

        SpoolmanSettings settings = _settingsService.Get<SpoolmanSettings>() ?? new SpoolmanSettings();
        return Ok(new SetupBootstrapResponse(settings.BaseUrl));
    }

    /// <summary>
    /// Creates the initial admin user and completes first-run setup.
    /// This endpoint is only available when no admin users exist.
    /// </summary>
    /// <param name="request">The request containing the initial admin user details.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    [HttpPost("initial-admin")]
    [AllowAnonymous] // Public because this bootstrap action creates the installation's first authenticated account.
    public async Task<ActionResult<AuthenticationResult>> CreateInitialAdminAsync(
        [FromBody] CreateInitialAdminRequest request,
        CancellationToken ct)
    {
        AuthenticationResult result = await _setupService.CreateInitialAdminAsync(request, ct);

        if (!result.Success)
        {
            // Check for specific error types to return appropriate status codes
            if (result.Error?.Contains("already been completed") == true ||
                result.Error?.Contains("already exist") == true)
            {
                return BadRequest(result);
            }

            return result.Error?.Contains("not found in database") == true
                ? StatusCode(StatusCodes.Status500InternalServerError, result)
                : BadRequest(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Gets available configuration options for setup.
    /// </summary>
    [HttpGet("config-options")]
    [AllowAnonymous] // Public because the setup client needs installation options before the first account exists.
    public ActionResult<object> GetConfigurationOptions()
    {
        SetupConfigurationOptions options = _setupService.GetConfigurationOptions();

        // Use dictionaries to preserve exact key casing as expected by tests
        Dictionary<string, object> result = new()
        {
            ["DatabaseProviders"] = options.DatabaseProviders,
            ["DefaultNetworkRanges"] = options.DefaultNetworkRanges,
            ["RecommendedPorts"] = options.RecommendedPorts
        };

        return Ok(result);
    }
}
