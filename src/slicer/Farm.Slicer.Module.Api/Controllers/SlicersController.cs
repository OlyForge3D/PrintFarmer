using Farm.Slicer.Module.Api.Filters;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Contracts.Libraries;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Slicer.Module.Api.Controllers;

/// <summary>
/// API endpoints for slicer service registration and lifecycle management.
/// </summary>
[ApiController]
[Route("api/slicers")]

// Slicer workers authenticate through the slicer API-key filters, not PrintFarmer bearer tokens.
[AllowAnonymous]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "SonarAnalyzer",
    "S6960:This controller has multiple responsibilities",
    Justification = "SlicersController owns both slicer worker registration and the read-only engines-list endpoint (issue #578). Splitting would fragment a small, cohesive surface.")]
public class SlicersController(ISlicersService service, ISlicerRegistry registry) : ControllerBase
{
    private readonly ISlicersService _service = service ?? throw new ArgumentNullException(nameof(service));
    private readonly ISlicerRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <summary>
    /// Lists all slicer engines available in the plugin registry, including all
    /// installed versions per engine (issue #578). Used by the React version
    /// selector. Each version carries an <c>available</c> flag that is true only
    /// when at least one Online <see cref="SlicerService"/> currently advertises
    /// that (engine, version) pair, so the UI can distinguish "installed" from
    /// "actually claimable right now" and never pin a job to a version no worker
    /// can serve. When no services have registered yet (fresh install) every
    /// version is reported as available so the version pin is still usable.
    /// </summary>
    [HttpGet("engines")]
    [AllowAnonymous]
    public async Task<IActionResult> ListEnginesAsync()
    {
        IReadOnlyList<ISlicerLibrary> libraries = _registry.ListAllLibraries().ToList();
        IReadOnlyList<SlicerService> services = await _service.ListAsync(HttpContext.RequestAborted);

        HashSet<(string Engine, string Version)> online = services
            .Where(s => string.Equals(s.Status, "Online", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(s.Version))
            .Select(s => (Engine: SlicerTypeToEngineName(s.SlicerType), Version: s.Version!.Trim()))
            .Where(t => !string.IsNullOrEmpty(t.Engine))
            .ToHashSet();

        // If nothing is online, don't hide the registry — legacy single-worker
        // deployments may never have a SlicerService row, and hiding the
        // registry would break the version selector entirely.
        bool anyOnline = online.Count > 0;

        var engines = libraries
            .GroupBy(l => l.SlicerName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var versionEntries = g
                    .Select(l => new
                    {
                        version = l.SlicerVersion,
                        available = !anyOnline || online.Contains((g.Key, l.SlicerVersion)),
                    })
                    .ToArray();

                string[] allVersions = versionEntries.Select(v => v.version).ToArray();
                string? latestAvailable = versionEntries.FirstOrDefault(v => v.available)?.version
                                          ?? allVersions.FirstOrDefault();

                return new
                {
                    engine = g.Key,
                    versions = allVersions,
                    versionEntries,
                    latest = latestAvailable,
                };
            })
            .OrderBy(e => e.engine, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(engines);
    }

    private static string SlicerTypeToEngineName(int slicerType)
    {
        // Mirrors Farm.Slicer.Module.Domain.SlicerType enum values. Keep in sync
        // when adding engines. Unknown enums are ignored (empty string) so a
        // stale registry row can't accidentally satisfy an availability check.
        return slicerType switch
        {
            0 => "PrusaSlicer",
            1 => "OrcaSlicer",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Lists all registered slicer services.
    /// </summary>
    [HttpGet]
    [RequireSlicerApiKey]
    public async Task<IActionResult> ListAsync()
    {
        IReadOnlyList<SlicerService> list = await _service.ListAsync(HttpContext.RequestAborted);
        return Ok(list.Select(ToResponseDto).ToList());
    }

    /// <summary>
    /// Registers a new slicer service.
    /// </summary>
    /// <param name="dto">Registration data.</param>
    [HttpPost("register")]
    [RequireSlicerApiKey]
    public async Task<IActionResult> RegisterAsync([FromBody] RegisterSlicerDto dto)
    {
        CancellationToken ct = HttpContext.RequestAborted;
        (Guid id, string? apiKey) = await _service.RegisterAsync(dto, ct);
        string location = $"/api/slicers/{id}";
        return Created(location, new { id, apiKey });
    }

    /// <summary>
    /// Gets a specific slicer service by ID.
    /// </summary>
    /// <param name="id">The slicer service ID.</param>
    [HttpGet("{id}")]
    [RequireSlicerServiceApiKey]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        SlicerService? svc = await _service.GetAsync(id, HttpContext.RequestAborted);
        return svc == null ? NotFound() : Ok(ToResponseDto(svc));
    }

    /// <summary>
    /// Processes a heartbeat from a slicer service.
    /// </summary>
    /// <param name="id">The slicer service ID.</param>
    /// <param name="dto">Heartbeat data.</param>
    [HttpPost("{id}/heartbeat")]
    [RequireSlicerServiceApiKey]
    public async Task<IActionResult> HeartbeatAsync(Guid id, [FromBody] HeartbeatDto dto)
    {
        bool ok = await _service.HeartbeatAsync(id, dto, HttpContext.RequestAborted);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>
    /// Deregisters a slicer service.
    /// </summary>
    /// <param name="id">The slicer service ID.</param>
    [HttpPost("{id}/deregister")]
    [RequireSlicerServiceApiKey]
    public async Task<IActionResult> DeregisterAsync(Guid id)
    {
        bool ok = await _service.DeregisterAsync(id, HttpContext.RequestAborted);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>
    /// Rotates the API key for a slicer service.
    /// </summary>
    /// <param name="id">The slicer service ID.</param>
    [HttpPost("{id}/rotate-key")]
    [RequireSlicerServiceApiKey]
    public async Task<IActionResult> RotateApiKeyAsync(Guid id)
    {
        string? newApiKey = await _service.RotateApiKeyAsync(id, HttpContext.RequestAborted);
        return newApiKey == null ? NotFound() : Ok(new { id, apiKey = newApiKey });
    }

    private static SlicerServiceResponseDto ToResponseDto(SlicerService service)
    {
        return new SlicerServiceResponseDto
        {
            Id = service.Id,
            Name = service.Name,
            SlicerType = service.SlicerType,
            Version = service.Version,
            Host = service.Host,
            UiManifestUrl = service.UiManifestUrl,
            CapabilitiesJson = service.CapabilitiesJson,
            MaxConcurrentJobs = service.MaxConcurrentJobs,
            Status = service.Status,
            LastSeen = service.LastSeen,
            ApiKeyRotatedAt = service.ApiKeyRotatedAt,
            CreatedAt = service.CreatedAt,
            UpdatedAt = service.UpdatedAt,
            Tags = service.Tags,
            InstanceId = service.InstanceId,
        };
    }
}
