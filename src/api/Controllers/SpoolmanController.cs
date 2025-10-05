using System.Text.Json;
using Farm.Web.Api.Services;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for integrating with Spoolman filament management system.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Tags("Spoolman Integration")]
public class SpoolmanController(
    SpoolmanService spoolman,
    IHttpClientFactory httpClientFactory,
    NetworkUrlRewriteService urlRewriter,
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

        string raw = request.BaseUrl.Trim();
        // Prepend scheme if user omitted (assume http)
        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = "http://" + raw; // safer default inside container networks
        }
        if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri? baseUri))
        {
            return Ok(new { success = false, message = "Invalid URL" });
        }

        // Apply environment-specific URL rewriting for network access
        string rewrittenUrl = urlRewriter.RewriteUrl(baseUri.ToString(), "Spoolman");
        string normalized = rewrittenUrl.TrimEnd('/');

        string[] probePaths = new[] { "/api/v1/health", "/api/v1/info" }; // order matters
        try
        {
            foreach (string path in probePaths)
            {
                try
                {
                    HttpClient client = httpClientFactory.CreateClient("SpoolmanTestProbe");
                    client.Timeout = TimeSpan.FromSeconds(5);
                    using HttpResponseMessage resp = await client.GetAsync(normalized + path, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        string? version = null;
                        try
                        {
                            using Stream stream = await resp.Content.ReadAsStreamAsync(ct);
                            using JsonDocument doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);
                            JsonElement root = doc.RootElement;
                            // Try common version property names
                            if (root.TryGetProperty("version", out JsonElement vProp) && vProp.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                version = vProp.GetString();
                            }
                            else if (root.TryGetProperty("spoolman_version", out JsonElement svProp) && svProp.ValueKind == System.Text.Json.JsonValueKind.String)
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
                        // Log only the last failure
                        _logger.LogError(ex, "Unhandled exception in /api/spoolman/test: {Message}", ex.Message);
                        (string? category, string? message) = CategorizeException(ex);
                        return StatusCode(StatusCodes.Status500InternalServerError, new { success = false, normalizedUrl = normalized, endpointTried = path, message, errorCategory = category });
                    }
                }
            }
            return StatusCode(StatusCodes.Status500InternalServerError, new { success = false, normalizedUrl = normalized, message = "Probe endpoints failed" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in /api/spoolman/test: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { success = false, normalizedUrl = normalized, message = $"Internal Server Error: {ex.Message}" });
        }
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
        // Extra logging for 401 diagnostics
        var user = HttpContext.User;
        if (user.Identity == null || !user.Identity.IsAuthenticated)
        {
            _logger.LogWarning("[SpoolmanController] SetConfig: User is not authenticated. Claims: {Claims}", string.Join(", ", user.Claims.Select(c => $"{c.Type}={c.Value}")));
        }
        else
        {
            var name = user.Identity != null ? user.Identity.Name : "(null)";
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
        SpoolmanConfigDto? cfg = spoolman.GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            return Ok(new { configured = false, success = false, message = "Spoolman not configured" });
        }

        // Use a minimal endpoint (info or health). Try /api/v1/health first, fallback to /api/v1/info
        string baseUrl = cfg.BaseUrl.TrimEnd('/');
        string[] probePaths = new[] { "/api/v1/health", "/api/v1/info" }; // order matters
        try
        {
            foreach (string p in probePaths)
            {
                try
                {
                    HttpClient client = httpClientFactory.CreateClient("SpoolmanHealthProbe");
                    client.Timeout = TimeSpan.FromSeconds(5);
                    HttpResponseMessage resp = await client.GetAsync(baseUrl + p, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        return Ok(new { configured = true, success = true, endpoint = p, statusCode = (int)resp.StatusCode });
                    }
                }
                catch (Exception ex)
                {
                    if (p == probePaths[^1])
                    {
                        _logger.LogError(ex, "Unhandled exception in /api/spoolman/health: {Message}", ex.Message);
                        return StatusCode(StatusCodes.Status500InternalServerError, new { configured = true, success = false, message = $"Internal Server Error: {ex.Message}" });
                    }
                }
            }
            return StatusCode(StatusCodes.Status500InternalServerError, new { configured = true, success = false, message = "Probe endpoints failed" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in /api/spoolman/health: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { configured = true, success = false, message = $"Internal Server Error: {ex.Message}" });
        }
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
            var settings = _settingsService.Get<NetworkDiscoverySettings>();
            var ranges = settings?.DiscoverySubnets?.ToList() ?? new List<string>();
            var results = await spoolman.ScanNetworkForSpoolmanAsync(ranges, ct);
            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in /api/spoolman/scan-network: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new[]
            {
                new SpoolmanDiscoveryResult(
                    Url: "",
                    IsAvailable: false,
                    Error: $"Network scan failed: {ex.Message}")
            });
        }
    }
}
