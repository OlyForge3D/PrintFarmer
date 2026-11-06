using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Logging;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.FailureDetection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Modules.Observability.Controllers;

/// <summary>
/// Controller for managing Obico ML API servers used for AI-powered print failure detection.
/// This is an administrative integration-management surface: every action requires the
/// <c>obico:manage</c> permission (farm_admin holds it implicitly).
/// </summary>
[ApiController]
[Route("api/obico-servers")]
[Authorize]
[RequirePermission(PrintFarmerPermissions.Integrations.ManageObico)]
public class ObicoServerController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IObicoConnectivityProbe _connectivityProbe;
    private readonly ILogger<ObicoServerController> _logger;

    public ObicoServerController(
        AppDbContext dbContext,
        IObicoConnectivityProbe connectivityProbe,
        ILogger<ObicoServerController> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _connectivityProbe = connectivityProbe ?? throw new ArgumentNullException(nameof(connectivityProbe));
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
            "[ObicoServer] Created new Obico server: {ServerId} ({ServerName})",
            server.Id,
            LogSanitizer.Sanitize(server.Name));

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
        bool urlChanged = dto.Url != null && dto.Url != server.Url;
        if (urlChanged &&
            (!Uri.TryCreate(dto.Url, UriKind.Absolute, out Uri? uri) ||
             (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            return BadRequest("URL must be a valid HTTP or HTTPS URL");
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
        }

        bool apiKeyChanged = dto.ApiKey is not null;
        string? resolvedApiKey = apiKeyChanged
            ? (string.IsNullOrWhiteSpace(dto.ApiKey) ? null : dto.ApiKey.Trim())
            : server.ApiKey;
        string resolvedUrl = dto.Url ?? server.Url;
        bool resolvedEnabled = dto.IsEnabled ?? server.IsEnabled;

        // Revalidate connectivity whenever the resulting server will be enabled AND either
        // (a) it is transitioning from disabled to enabled, or (b) its destination (Url) or
        // credentials (ApiKey) are changing. (a) catches a disabled server (e.g. stale,
        // seeded, or imported with a blocked/invalid destination) being enabled unchanged;
        // (b) catches an already-enabled server having its Url/ApiKey swapped to a blocked
        // destination without ever being re-probed through the egress guard. Neither check
        // alone is sufficient — both gaps allowed a forbidden target to be persisted.
        bool enablingTransition = resolvedEnabled && !server.IsEnabled;
        bool destinationChanging = urlChanged || apiKeyChanged;
        if (resolvedEnabled && (enablingTransition || destinationChanging))
        {
            ObicoServer probeServer = new()
            {
                Url = resolvedUrl,
                ApiKey = resolvedApiKey
            };
            ObicoServerHealthDto healthResult = await ValidateServerConnectivityAsync(probeServer, ct);
            if (!healthResult.Healthy)
            {
                return BadRequest($"Obico server validation failed: {healthResult.ErrorMessage}");
            }
        }

        if (urlChanged)
        {
            server.Url = dto.Url!;
        }

        if (dto.Name != null && dto.Name != server.Name)
        {
            server.Name = dto.Name;
        }

        if (dto.IsEnabled.HasValue)
        {
            server.IsEnabled = dto.IsEnabled.Value;
        }

        if (dto.MaxConcurrentAnalyses.HasValue)
        {
            server.MaxConcurrentAnalyses = dto.MaxConcurrentAnalyses.Value;
        }

        // ApiKey: null means "don't change", empty string means "clear it"
        if (apiKeyChanged)
        {
            server.ApiKey = resolvedApiKey;
        }

        server.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[ObicoServer] Updated Obico server: {ServerId} ({ServerName})",
            server.Id, LogSanitizer.Sanitize(server.Name));

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
            server.Id, LogSanitizer.Sanitize(server.Name));

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
            "[ObicoServer] Health check for {ServerId} ({ServerName}): healthy={IsHealthy}, latency={Latency}ms",
            server.Id,
            LogSanitizer.Sanitize(server.Name),
            result.Healthy,
            result.LatencyMs);

        return Ok(result);
    }

    /// <summary>
    /// Validates full Obico ML server connectivity against the upstream GET contract first,
    /// then falls back to the legacy multipart probe for backward compatibility.
    /// </summary>
    private async Task<ObicoServerHealthDto> ValidateServerConnectivityAsync(ObicoServer server, CancellationToken ct)
    {
        ObicoConnectivityResult result = await _connectivityProbe.ProbeAsync(server.Url, server.ApiKey, ct);
        return new ObicoServerHealthDto
        {
            Healthy = result.Healthy,
            LatencyMs = result.LatencyMs,
            ErrorMessage = result.ErrorMessage
        };
    }

    private static ObicoServerDto ToDto(ObicoServer server)
    {
        return new ObicoServerDto
        {
            Id = server.Id,
            Name = server.Name,
            HasEndpoint = !string.IsNullOrWhiteSpace(server.Url),
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

    public bool HasEndpoint { get; init; }

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
