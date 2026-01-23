using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for integrating with Spoolman filament management system.
/// </summary>
[ApiController]
[Route("api/spoolman")]
[Tags("Spoolman Integration")]
[Authorize]
public class SpoolmanController(
    ISpoolmanService spoolman,
    ISettingsService settingsService,
    IUnifiedLoggingService logger) : ControllerBase
{
    private readonly ISettingsService _settingsService = settingsService;
    private readonly IUnifiedLoggingService _logger = logger;

    /// <summary>
    /// Tests connectivity to an arbitrary Spoolman base URL without persisting configuration.
    /// Used by the setup wizard before saving settings. Always returns 200 with success flag.
    /// </summary>
    /// <param name="request">Request containing the candidate BaseUrl.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>JSON object { success, normalizedUrl?, endpointTried?, statusCode?, version?, message? }</returns>
    /// <response code="200">Returns probe result (success may be true/false)</response>
    [HttpPost("test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [AllowAnonymous]
    public async Task<IActionResult> TestAsync([FromBody] SpoolmanConfigDto? request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            return Ok(new { success = false, message = "BaseUrl is required" });
        }

        SpoolmanProbeResult probe = await spoolman.ProbeAsync(request.BaseUrl, ct);
        return Ok(new { success = probe.Success, normalizedUrl = probe.NormalizedUrl, endpointTried = probe.EndpointTried, statusCode = probe.StatusCode, version = probe.Version, message = probe.Message, errorCategory = probe.ErrorCategory });
    }

    // Note: Exception categorization was moved into the SpoolmanService Probe implementation.

    /// <summary>
    /// Gets the current Spoolman integration configuration.
    /// </summary>
    /// <returns>Current Spoolman configuration including server URL and connection settings</returns>
    /// <response code="200">Returns the current Spoolman configuration</response>
    [HttpGet("config")]
    [ProducesResponseType(typeof(SpoolmanConfigDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult<SpoolmanConfigDto?> GetConfig() => spoolman.GetConfig();

    /// <summary>
    /// Updates the Spoolman integration configuration.
    /// </summary>
    /// <param name="config">New Spoolman configuration settings</param>
    /// <returns>No content if successful</returns>
    /// <response code="204">If the configuration was successfully updated</response>
    /// <response code="400">If the configuration data is invalid</response>
    [HttpPost("config")]
    [Authorize(Policy = "RequireAdmin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult SetConfig([FromBody] SpoolmanConfigDto? config)
    {
        // Extra logging for 401 diagnostics
        System.Security.Claims.ClaimsPrincipal user = HttpContext.User;
        if (user.Identity == null || !user.Identity.IsAuthenticated)
        {
            _logger.LogWarning("[SpoolmanController] SetConfig: User is not authenticated. Claims: {Claims}", string.Join(", ", user.Claims.Select(c => $"{c.Type}={c.Value}")));
        }
        else
        {
            string? name = user.Identity != null ? user.Identity.Name : "(null)";
            _logger.LogInformation("[SpoolmanController] SetConfig: Authenticated user: {Name}. Claims: {Claims}", name, string.Join(", ", user.Claims.Select(c => $"{c.Type}={c.Value}")));
        }

        if (config is null)
        {
            return BadRequest("Config body is required.");
        }

        spoolman.SetConfig(config);
        return NoContent();
    }

    /// <summary>
    /// Gets all spools from the connected Spoolman server.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of all filament spools from Spoolman</returns>
    /// <response code="200">Returns the list of spools from Spoolman</response>
    /// <response code="503">If Spoolman is not configured or unavailable</response>
    [HttpGet("spools")]
    [ProducesResponseType(typeof(IEnumerable<SpoolmanSpoolDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IEnumerable<SpoolmanSpoolDto>>> GetSpoolsAsync(CancellationToken ct)
        => Ok(await spoolman.ListSpoolsAsync(ct));

    /// <summary>
    /// Performs a lightweight health probe against the configured Spoolman instance.
    /// Returns basic status information (success flag and optional message) without enumerating all spools.
    /// </summary>
    /// <returns>Health status for Spoolman integration</returns>
    /// <response code="200">Health probe executed (Success may be true/false)</response>
    [HttpGet("health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> HealthAsync(CancellationToken ct)
    {
        SpoolmanProbeResult probe = await spoolman.HealthProbeAsync(ct);
        if (!probe.Success)
        {
            return Ok(new { configured = true, success = false, message = probe.Message });
        }

        return Ok(new { configured = true, success = true, endpoint = probe.EndpointTried, statusCode = probe.StatusCode });
    }

    /// <summary>
    /// Clears the Spoolman configuration.
    /// </summary>
    /// <returns>No content</returns>
    [HttpDelete("config")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult ClearConfig()
    {
        try
        {
            spoolman.ClearConfig();
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in /api/spoolman/config (DELETE): {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, $"Internal Server Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Scans the configured network ranges for Spoolman instances.
    /// Uses the discovery settings to determine which IP ranges to scan.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of discovered Spoolman instances</returns>
    /// <response code="200">Returns list of discovered Spoolman instances</response>
    [HttpPost("scan-network")]
    [ProducesResponseType(typeof(IEnumerable<SpoolmanDiscoveryResult>), StatusCodes.Status200OK)]
    [AllowAnonymous]
    public async Task<IActionResult> ScanNetworkAsync(CancellationToken ct)
    {
        try
        {
            NetworkDiscoverySettings settings = _settingsService.Get<NetworkDiscoverySettings>();
            List<string> ranges = settings?.DiscoverySubnets?.ToList() ?? new List<string>();
            IEnumerable<SpoolmanDiscoveryResult> results = await spoolman.ScanNetworkForSpoolmanAsync(ranges, ct);
            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in /api/spoolman/scan-network: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new[]
            {
                new SpoolmanDiscoveryResult(
                    Url: string.Empty,
                    IsAvailable: false,
                    Error: $"Network scan failed: {ex.Message}")
            });
        }
    }
}
