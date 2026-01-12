using Farm.Infrastructure;
using Farm.Web.Api.Services.Setup;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for initial application setup and configuration.
/// Used during first-run to create initial admin user and configure the system.
/// </summary>
[ApiController]
[Route("api/setup")]
public class SetupController(ISetupService setupService) : ControllerBase
{
    private readonly ISetupService _setupService = setupService;

    /// <summary>
    /// Checks if the application needs initial setup.
    /// Returns true if no admin users exist in the system.
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<object>> GetSetupStatusAsync(CancellationToken ct)
    {
        bool needsSetup = await _setupService.NeedsSetupAsync(ct);
        return Ok(new { needsSetup });
    }

    /// <summary>
    /// Creates the initial admin user and completes first-run setup.
    /// This endpoint is only available when no admin users exist.
    /// </summary>
    [HttpPost("initial-admin")]
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

            if (result.Error?.Contains("not found in database") == true)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, result);
            }

            return BadRequest(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Gets available configuration options for setup.
    /// </summary>
    [HttpGet("config-options")]
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
