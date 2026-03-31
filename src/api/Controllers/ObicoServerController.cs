using System.Net;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.FailureDetection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Controller for managing Obico ML API servers used for AI-powered print failure detection.
/// </summary>
[ApiController]
[Route("api/obico-servers")]
[Authorize]
public class ObicoServerController : ControllerBase
{
    private const string UpstreamHealthProbeSnapshotUrl = "http://printfarmer.local/obico-health-probe.jpg";

    private readonly AppDbContext _dbContext;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ObicoServerController> _logger;

    public ObicoServerController(
        AppDbContext dbContext,
        IHttpClientFactory httpClientFactory,
        ILogger<ObicoServerController> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets all Obico ML servers.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<ObicoServerDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ObicoServerDto>>> GetAllServersAsync(CancellationToken ct)
    {
        List<ObicoServer> servers = await _dbContext.ObicoServers
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

        return Ok(servers.Select(ToDto).ToList());
    }

    /// <summary>
    /// Gets a specific Obico ML server by ID.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ObicoServerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ObicoServerDto>> GetServerAsync(Guid id, CancellationToken ct)
    {
        ObicoServer? server = await _dbContext.ObicoServers.FindAsync([id], ct);
        if (server == null)
        {
            return NotFound($"Obico server with ID {id} not found");
        }

        return Ok(ToDto(server));
    }

    /// <summary>
    /// Creates a new Obico ML server configuration.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ObicoServerDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ObicoServerDto>> CreateServerAsync([FromBody] CreateObicoServerDto dto, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        // Validate URL format
        if (!Uri.TryCreate(dto.Url, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest("URL must be a valid HTTP or HTTPS URL");
        }

        // Check for duplicate names
        bool nameExists = await _dbContext.ObicoServers
            .AnyAsync(s => s.Name == dto.Name, ct);

        if (nameExists)
        {
            return BadRequest($"An Obico server with the name '{dto.Name}' already exists");
        }

        var server = new ObicoServer
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            Url = dto.Url,
            ApiKey = string.IsNullOrWhiteSpace(dto.ApiKey) ? null : dto.ApiKey.Trim(),
            IsEnabled = dto.IsEnabled ?? true,
            MaxConcurrentAnalyses = dto.MaxConcurrentAnalyses ?? 4,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Validate server connectivity before saving
        ObicoServerHealthDto healthResult = await ValidateServerConnectivityAsync(server, ct);
        if (!healthResult.Healthy)
        {
            return BadRequest($"Obico server validation failed: {healthResult.ErrorMessage}");
        }

        _dbContext.ObicoServers.Add(server);
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[ObicoServer] Created new Obico server: {ServerId} ({ServerName}) at {ServerUrl}",
            server.Id, server.Name, server.Url);

        return CreatedAtAction("GetServer", new { id = server.Id }, ToDto(server));
    }

    /// <summary>
    /// Updates an existing Obico ML server configuration.
    /// </summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ObicoServerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ObicoServerDto>> UpdateServerAsync(Guid id, [FromBody] UpdateObicoServerDto dto, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        ObicoServer? server = await _dbContext.ObicoServers.FindAsync([id], ct);
        if (server == null)
        {
            return NotFound($"Obico server with ID {id} not found");
        }

        // Validate URL format if changed
        if (dto.Url != null && dto.Url != server.Url)
        {
            if (!Uri.TryCreate(dto.Url, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return BadRequest("URL must be a valid HTTP or HTTPS URL");
            }

            server.Url = dto.Url;
        }

        // Check for duplicate names if changed
        if (dto.Name != null && dto.Name != server.Name)
        {
            bool nameExists = await _dbContext.ObicoServers
                .AnyAsync(s => s.Name == dto.Name && s.Id != id, ct);

            if (nameExists)
            {
                return BadRequest($"An Obico server with the name '{dto.Name}' already exists");
            }

            server.Name = dto.Name;
        }

        if (dto.IsEnabled.HasValue)
        {
            // Validate connectivity when enabling a server
            if (dto.IsEnabled.Value && !server.IsEnabled)
            {
                // Apply URL/ApiKey changes before validation
                ObicoServer probeServer = new()
                {
                    Url = dto.Url ?? server.Url,
                    ApiKey = dto.ApiKey is not null
                        ? (string.IsNullOrWhiteSpace(dto.ApiKey) ? null : dto.ApiKey.Trim())
                        : server.ApiKey
                };
                ObicoServerHealthDto healthResult = await ValidateServerConnectivityAsync(probeServer, ct);
                if (!healthResult.Healthy)
                {
                    return BadRequest($"Cannot enable server — validation failed: {healthResult.ErrorMessage}");
                }
            }

            server.IsEnabled = dto.IsEnabled.Value;
        }

        if (dto.MaxConcurrentAnalyses.HasValue)
        {
            server.MaxConcurrentAnalyses = dto.MaxConcurrentAnalyses.Value;
        }

        // ApiKey: null means "don't change", empty string means "clear it"
        if (dto.ApiKey is not null)
        {
            server.ApiKey = string.IsNullOrWhiteSpace(dto.ApiKey) ? null : dto.ApiKey.Trim();
        }

        server.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[ObicoServer] Updated Obico server: {ServerId} ({ServerName})",
            server.Id, server.Name);

        return Ok(ToDto(server));
    }

    /// <summary>
    /// Deletes an Obico ML server configuration.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteServerAsync(Guid id, CancellationToken ct)
    {
        ObicoServer? server = await _dbContext.ObicoServers
            .Include(s => s.PrinterServiceStates)
            .FirstOrDefaultAsync(s => s.Id == id, ct);

        if (server == null)
        {
            return NotFound($"Obico server with ID {id} not found");
        }

        // Check if any printers are assigned to this server
        if (server.PrinterServiceStates.Count > 0)
        {
            return BadRequest(
                $"Cannot delete Obico server '{server.Name}' because {server.PrinterServiceStates.Count} printer(s) are assigned to it. " +
                "Please reassign or remove the printers first.");
        }

        _dbContext.ObicoServers.Remove(server);
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[ObicoServer] Deleted Obico server: {ServerId} ({ServerName})",
            server.Id, server.Name);

        return NoContent();
    }

    /// <summary>
    /// Tests connectivity to an Obico ML server by validating its prediction endpoint.
    /// </summary>
    [HttpGet("{id:guid}/health")]
    [ProducesResponseType(typeof(ObicoServerHealthDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ObicoServerHealthDto>> TestServerHealthAsync(Guid id, CancellationToken ct)
    {
        ObicoServer? server = await _dbContext.ObicoServers.FindAsync([id], ct);
        if (server == null)
        {
            return NotFound($"Obico server with ID {id} not found");
        }

        ObicoServerHealthDto result = await ValidateServerConnectivityAsync(server, ct);

        _logger.LogInformation(
            "[ObicoServer] Health check for {ServerId} ({ServerName}) at {ServerUrl}: healthy={IsHealthy}, latency={Latency}ms",
            server.Id, server.Name, server.Url, result.Healthy, result.LatencyMs);

        return Ok(result);
    }

    /// <summary>
    /// Validates full Obico ML server connectivity against the upstream GET contract first,
    /// then falls back to the legacy multipart probe for backward compatibility.
    /// </summary>
    private async Task<ObicoServerHealthDto> ValidateServerConnectivityAsync(ObicoServer server, CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        List<string> errors = [];

        try
        {
            using HttpClient httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            httpClient.BaseAddress = new Uri(server.Url.TrimEnd('/') + "/");

            if (!string.IsNullOrWhiteSpace(server.ApiKey))
            {
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.ApiKey);
            }

            (bool validated, string? upstreamError) = await TryValidateUpstreamPredictionEndpointAsync(httpClient, ct);
            if (!validated)
            {
                string? legacyError = await TryValidateLegacyPredictionEndpointAsync(httpClient, ct);
                if (!string.IsNullOrWhiteSpace(legacyError))
                {
                    errors.Add(legacyError);
                }
            }
            else if (!string.IsNullOrWhiteSpace(upstreamError))
            {
                errors.Add(upstreamError);
            }
        }
        catch (UriFormatException)
        {
            errors.Add($"Invalid server URL: {server.Url}");
        }
        catch (HttpRequestException ex)
        {
            errors.Add($"Connection failed: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            errors.Add("Request timeout — server did not respond within 10 seconds");
        }
        catch (Exception ex)
        {
            errors.Add($"Unexpected error: {ex.Message}");
        }
        finally
        {
            stopwatch.Stop();
        }

        return new ObicoServerHealthDto
        {
            Healthy = errors.Count == 0,
            LatencyMs = stopwatch.ElapsedMilliseconds,
            ErrorMessage = errors.Count > 0 ? string.Join("; ", errors) : null
        };
    }

    /// <summary>
    /// Probes the upstream self-hosted contract (`GET /p/`) and validates the `detections` payload shape.
    /// </summary>
    private async Task<(bool validated, string? error)> TryValidateUpstreamPredictionEndpointAsync(HttpClient httpClient, CancellationToken ct)
    {
        try
        {
            string requestPath = $"p/?img={Uri.EscapeDataString(UpstreamHealthProbeSnapshotUrl)}";
            HttpResponseMessage response = await httpClient.GetAsync(requestPath, ct);
            string responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                if (ObicoSnapshotFallbackDetector.ShouldFallbackToLegacyUpload(response.StatusCode) ||
                    ObicoSnapshotFallbackDetector.ShouldFallbackBecauseSnapshotWasUnreachable(response.StatusCode, responseBody))
                {
                    return (false, null);
                }

                return (true, $"Prediction endpoint /p/ returned HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
            }

            if (HasDetectionsArray(responseBody))
            {
                return (true, null);
            }

            return (false, null);
        }
        catch (HttpRequestException ex)
        {
            return (true, $"Cannot reach prediction endpoint /p/: {ex.Message}");
        }
    }

    /// <summary>
    /// Preserves compatibility with older multipart upload contracts when the upstream GET contract is unavailable.
    /// </summary>
    private static async Task<string?> TryValidateLegacyPredictionEndpointAsync(HttpClient httpClient, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "p/")
        {
            Content = new StringContent(string.Empty)
        };
        HttpResponseMessage response = await httpClient.SendAsync(request, ct);

        bool endpointReachable = response.IsSuccessStatusCode ||
            response.StatusCode == HttpStatusCode.BadRequest ||
            response.StatusCode == HttpStatusCode.UnsupportedMediaType;

        return endpointReachable
            ? null
            : $"Prediction endpoint /p/ returned HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
    }

    /// <summary>
    /// Detects the upstream self-hosted response shape without depending on exact property casing.
    /// </summary>
    private static bool HasDetectionsArray(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            return TryGetPropertyIgnoreCase(document.RootElement, "detections", out JsonElement detectionsElement)
                && detectionsElement.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds a JSON property without requiring exact casing.
    /// </summary>
    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static ObicoServerDto ToDto(ObicoServer server)
    {
        return new ObicoServerDto
        {
            Id = server.Id,
            Name = server.Name,
            Url = server.Url,
            IsEnabled = server.IsEnabled,
            HasApiKey = !string.IsNullOrEmpty(server.ApiKey),
            MaxConcurrentAnalyses = server.MaxConcurrentAnalyses,
            CreatedAt = server.CreatedAt,
            UpdatedAt = server.UpdatedAt
        };
    }
}

/// <summary>
/// DTO for Obico ML server information.
/// </summary>
public sealed class ObicoServerDto
{
    public Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Url { get; init; }

    public bool IsEnabled { get; init; }

    /// <summary>
    /// Whether this server has an API key configured (key value is never exposed).
    /// </summary>
    public bool HasApiKey { get; init; }

    public int MaxConcurrentAnalyses { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }
}

/// <summary>
/// DTO for creating a new Obico ML server.
/// </summary>
public sealed class CreateObicoServerDto
{
    public required string Name { get; init; }

    public required string Url { get; init; }

    /// <summary>
    /// Optional API key for authenticating with this Obico server.
    /// </summary>
    public string? ApiKey { get; init; }

    public bool? IsEnabled { get; init; }

    public int? MaxConcurrentAnalyses { get; init; }
}

/// <summary>
/// DTO for updating an existing Obico ML server.
/// </summary>
public sealed class UpdateObicoServerDto
{
    public string? Name { get; init; }

    public string? Url { get; init; }

    /// <summary>
    /// Optional API key for authenticating with this Obico server.
    /// Set to empty string to clear the API key.
    /// </summary>
    public string? ApiKey { get; init; }

    public bool? IsEnabled { get; init; }

    public int? MaxConcurrentAnalyses { get; init; }
}

/// <summary>
/// DTO for Obico ML server health check results.
/// </summary>
public sealed class ObicoServerHealthDto
{
    public bool Healthy { get; init; }

    public long LatencyMs { get; init; }

    public string? ErrorMessage { get; init; }
}
