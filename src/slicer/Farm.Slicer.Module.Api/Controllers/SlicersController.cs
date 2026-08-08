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
    /// can serve. When there are no registered <see cref="SlicerService"/> rows
    /// at all (fresh install / legacy single-worker deployments that never call
    /// /api/slicers/register), every version is reported as available so the
    /// version pin remains usable. Once at least one row exists, availability
    /// is gated strictly by the Online set — offline services are honoured.
    /// </summary>
    [HttpGet("engines")]
    public async Task<IActionResult> ListEnginesAsync()
    {
        IReadOnlyList<ISlicerLibrary> libraries = _registry.ListAllLibraries().ToList();
        IReadOnlyList<SlicerService> services = await _service.ListAsync(HttpContext.RequestAborted);

        // Freshness gate for the "Online" status: an abruptly killed worker
        // can leave its row Status='Online' in the DB until the health monitor
        // sweeps it. Consumers of this endpoint use `available` to pin jobs,
        // so if a crashed worker looked "Online" for another 60s+ we would
        // dispatch jobs into a black hole. Require the row to have heartbeated
        // recently (twice the default health-monitor interval, 60s).
        DateTime freshnessCutoff = DateTime.UtcNow.AddSeconds(-WorkerStatus.OnlineFreshnessSeconds);
        HashSet<(string Engine, string Version)> online = services
            .Where(s => string.Equals(s.Status, "Online", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(s.Version)
                        && s.LastSeen >= freshnessCutoff)
            .Select(s => (Engine: SlicerTypeToEngineName(s.SlicerType), Version: s.Version!.Trim()))
            .Where(t => !string.IsNullOrEmpty(t.Engine))
            .ToHashSet();

        // Fresh install / legacy deployment fallback: only when NO service rows
        // exist at all do we mark every registry version available. When rows
        // exist but none are Online we honour that state (marking versions
        // unavailable) — otherwise the UI would happily let the user pin a
        // job to a version that will hang in the queue forever.
        bool anyServiceRows = services.Count > 0;

        var engines = libraries
            .GroupBy(l => l.SlicerName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var versionEntries = g
                    .Select(l => new
                    {
                        version = l.SlicerVersion,
                        available = !anyServiceRows || online.Contains((g.Key, l.SlicerVersion)),
                    })
                    .ToArray();

                string[] allVersions = versionEntries.Select(v => v.version).ToArray();

                // `latest` is the SIGNAL the frontend uses to decide whether
                // to pin a slice job (Hicks/Vasquez R3). Emit non-null only
                // when at least one version is currently available to claim
                // a job AND at least one service row exists. Three branches
                // produce null:
                //   1. No service rows exist (fresh install / legacy) — leave
                //      jobs unpinned so a generic "orcaslicer" worker can claim.
                //      In this branch `available` is true for every entry so
                //      the selector is usable, but we must not pin.
                //   2. Rows exist but ALL are offline/stale — a pinned job
                //      would sit in the queue with no worker willing to touch
                //      it. The UI shows every entry as "(offline)" disabled
                //      and blocks Latest-mode submission.
                //   3. Registry knows about a version but no worker of that
                //      version is fresh/Online — again, no pin target.
                string? latestAvailable = !anyServiceRows
                    ? null
                    : versionEntries.FirstOrDefault(v => v.available)?.version;

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
    [AllowAnonymous] // Public to JWT auth because slicer hosts authenticate with their slicer API key.
    public async Task<IActionResult> ListAsync()
    {
        IReadOnlyList<SlicerService> list = await _service.ListAsync(HttpContext.RequestAborted);
        return Ok(list.Select(MapToResponse));
    }

    /// <summary>
    /// Registers a new slicer service.
    /// </summary>
    /// <param name="dto">Registration data.</param>
    [HttpPost("register")]
    [RequireSlicerApiKey]
    [AllowAnonymous] // Public to JWT auth because new slicer hosts authenticate with the registration key.
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
    [AllowAnonymous] // Public to JWT auth because slicer hosts authenticate with their slicer API key.
    public async Task<IActionResult> GetAsync(Guid id)
    {
        SlicerService? svc = await _service.GetAsync(id, HttpContext.RequestAborted);
        return svc == null ? NotFound() : Ok(MapToResponse(svc));
    }

    /// <summary>
    /// Processes a heartbeat from a slicer service.
    /// </summary>
    /// <param name="id">The slicer service ID.</param>
    /// <param name="dto">Heartbeat data.</param>
    [HttpPost("{id}/heartbeat")]
    [RequireSlicerServiceApiKey]
    [AllowAnonymous] // Public to JWT auth because slicer hosts authenticate with their slicer API key.
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
    [AllowAnonymous] // Public to JWT auth because slicer hosts authenticate with their slicer API key.
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
    [AllowAnonymous] // Public to JWT auth because slicer hosts authenticate with their current slicer API key.
    public async Task<IActionResult> RotateApiKeyAsync(Guid id)
    {
        string? newApiKey = await _service.RotateApiKeyAsync(id, HttpContext.RequestAborted);
        return newApiKey == null ? NotFound() : Ok(new { id, apiKey = newApiKey });
    }

    private static SlicerServiceResponse MapToResponse(SlicerService service) => new()
    {
        Id = service.Id,
        Name = service.Name,
        SlicerType = service.SlicerType,
        Version = service.Version,
        MaxConcurrentJobs = service.MaxConcurrentJobs,
        Status = service.Status,
        LastSeen = service.LastSeen,
        CreatedAt = service.CreatedAt,
        UpdatedAt = service.UpdatedAt,
        Tags = service.Tags,
    };
}
