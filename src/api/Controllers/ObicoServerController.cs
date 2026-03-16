using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
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
            IsEnabled = dto.IsEnabled ?? true,
            MaxConcurrentAnalyses = dto.MaxConcurrentAnalyses ?? 4,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.ObicoServers.Add(server);
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[ObicoServer] Created new Obico server: {ServerId} ({ServerName}) at {ServerUrl}",
            server.Id, server.Name, server.Url);

        return CreatedAtAction(nameof(GetServerAsync), new { id = server.Id }, ToDto(server));
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
            server.IsEnabled = dto.IsEnabled.Value;
        }

        if (dto.MaxConcurrentAnalyses.HasValue)
        {
            server.MaxConcurrentAnalyses = dto.MaxConcurrentAnalyses.Value;
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
            .Include(s => s.Printers)
            .FirstOrDefaultAsync(s => s.Id == id, ct);

        if (server == null)
        {
            return NotFound($"Obico server with ID {id} not found");
        }

        // Check if any printers are assigned to this server
        if (server.Printers.Count > 0)
        {
            return BadRequest(
                $"Cannot delete Obico server '{server.Name}' because {server.Printers.Count} printer(s) are assigned to it. " +
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
    /// Tests connectivity to an Obico ML server by calling its /p/ endpoint.
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

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        bool isHealthy = false;
        string? errorMessage = null;

        try
        {
            using HttpClient httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            httpClient.BaseAddress = new Uri(server.Url);

            // Test the /p/ endpoint with a simple HEAD request
            var request = new HttpRequestMessage(HttpMethod.Head, "/p/");
            HttpResponseMessage response = await httpClient.SendAsync(request, ct);

            // Accept 405 Method Not Allowed as healthy (server exists but doesn't support HEAD)
            // Accept 400 Bad Request as healthy (server exists but requires proper multipart form data)
            isHealthy = response.IsSuccessStatusCode ||
                        response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed ||
                        response.StatusCode == System.Net.HttpStatusCode.BadRequest;

            if (!isHealthy)
            {
                errorMessage = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
            }
        }
        catch (HttpRequestException ex)
        {
            errorMessage = $"Connection failed: {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            errorMessage = "Request timeout";
        }
        catch (Exception ex)
        {
            errorMessage = $"Unexpected error: {ex.Message}";
        }
        finally
        {
            stopwatch.Stop();
        }

        _logger.LogInformation(
            "[ObicoServer] Health check for {ServerId} ({ServerName}) at {ServerUrl}: healthy={IsHealthy}, latency={Latency}ms",
            server.Id, server.Name, server.Url, isHealthy, stopwatch.ElapsedMilliseconds);

        return Ok(new ObicoServerHealthDto
        {
            Healthy = isHealthy,
            LatencyMs = stopwatch.ElapsedMilliseconds,
            ErrorMessage = errorMessage
        });
    }

    private static ObicoServerDto ToDto(ObicoServer server)
    {
        return new ObicoServerDto
        {
            Id = server.Id,
            Name = server.Name,
            Url = server.Url,
            IsEnabled = server.IsEnabled,
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
