using Farm.Infrastructure;
using Farm.Infrastructure.Services.Setup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for initial application setup and configuration.
/// Used during first-run to create initial admin user and configure the system.
/// </summary>
[ApiController]
[Route("api/setup")]
[AllowAnonymous] // First-run setup must be reachable before any user or admin account exists.
public class SetupController(ISetupService setupService) : ControllerBase
{
    private readonly ISetupService _setupService = setupService;

    /// <summary>
    /// Checks if the application needs initial setup.
    /// Returns true if no admin users exist in the system.
    /// </summary>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
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
    /// <param name="request">The request containing the initial admin user details.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
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
