using Farm.Web.Api.Services;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for integrating with Spoolman filament management system.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Tags("Spoolman Integration")]
public class SpoolmanController(SpoolmanService spoolman, IHttpClientFactory httpClientFactory) : ControllerBase
{
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

        var raw = request.BaseUrl.Trim();
        // Prepend scheme if user omitted (assume http)
        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = "http://" + raw; // safer default inside container networks
        }
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var baseUri))
        {
            return Ok(new { success = false, message = "Invalid URL" });
        }
        var normalized = baseUri.ToString().TrimEnd('/');

        string[] probePaths = ["/api/v1/health", "/api/v1/info"]; // order matters
        foreach (var path in probePaths)
        {
            try
            {
                var client = httpClientFactory.CreateClient("SpoolmanTestProbe");
                client.Timeout = TimeSpan.FromSeconds(5);
                using var resp = await client.GetAsync(normalized + path, ct);
                if (resp.IsSuccessStatusCode)
                {
                    string? version = null;
                    try
                    {
                        using var stream = await resp.Content.ReadAsStreamAsync(ct);
                        using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);
                        var root = doc.RootElement;
                        // Try common version property names
                        if (root.TryGetProperty("version", out var vProp) && vProp.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            version = vProp.GetString();
                        }
                        else if (root.TryGetProperty("spoolman_version", out var svProp) && svProp.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            version = svProp.GetString();
                        }
                    }
                    catch { /* ignore JSON parse failures */ }

                    return Ok(new
                    {
                        success = true,
                        normalizedUrl = normalized,
                        endpointTried = path,
                        statusCode = (int)resp.StatusCode,
                        version
                    });
                }
            }
            catch (Exception ex)
            {
                if (path == probePaths[^1])
                {
                    var (category, message) = CategorizeException(ex);
                    return Ok(new { success = false, normalizedUrl = normalized, endpointTried = path, message, errorCategory = category });
                }
            }
        }

        return Ok(new { success = false, normalizedUrl = normalized, message = "Probe endpoints failed" });
    }

    private static (string category, string message) CategorizeException(Exception ex)
    {
        if (ex is TaskCanceledException or OperationCanceledException)
        {
            return ("timeout", "Connection timed out");
        }
        if (ex is HttpRequestException hre)
        {
            if (hre.InnerException is System.Net.Sockets.SocketException se)
            {
                return se.SocketErrorCode switch
                {
                    System.Net.Sockets.SocketError.HostNotFound => ("dns_failure", "Host could not be resolved"),
                    System.Net.Sockets.SocketError.ConnectionRefused => ("connection_refused", "Connection refused"),
                    System.Net.Sockets.SocketError.TimedOut => ("timeout", "Connection timed out"),
                    _ => ("network_error", hre.Message)
                };
            }
            return ("http_error", hre.Message);
        }
        if (ex is System.Security.Authentication.AuthenticationException)
        {
            return ("tls_error", "TLS/SSL negotiation failed");
        }
        return ("unknown", ex.Message);
    }
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
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = "RequireAdmin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult SetConfig([FromBody] SpoolmanConfigDto? config)
    {
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
        var cfg = spoolman.GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            return Ok(new { configured = false, success = false, message = "Spoolman not configured" });
        }

        // Use a minimal endpoint (info or health). Try /api/v1/health first, fallback to /api/v1/info
        var baseUrl = cfg.BaseUrl.TrimEnd('/');
        string[] probePaths = ["/api/v1/health", "/api/v1/info"]; // order matters
        foreach (var p in probePaths)
        {
            try
            {
                var client = httpClientFactory.CreateClient("SpoolmanHealthProbe");
                client.Timeout = TimeSpan.FromSeconds(5);
                var resp = await client.GetAsync(baseUrl + p, ct);
                if (resp.IsSuccessStatusCode)
                {
                    return Ok(new { configured = true, success = true, endpoint = p, statusCode = (int)resp.StatusCode });
                }
            }
            catch (Exception ex)
            {
                // try next
                if (p == probePaths[^1])
                {
                    return Ok(new { configured = true, success = false, message = ex.Message });
                }
            }
        }
        return Ok(new { configured = true, success = false, message = "Probe endpoints failed" });
    }

    /// <summary>
    /// Clears the stored Spoolman configuration (disables integration until reconfigured).
    /// </summary>
    /// <response code="204">Configuration cleared</response>
    [HttpDelete("config")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = "RequireAdmin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult ClearConfig()
    {
        spoolman.ClearConfig();
        return NoContent();
    }
}
