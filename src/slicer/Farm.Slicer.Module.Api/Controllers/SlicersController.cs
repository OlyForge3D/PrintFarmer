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
    /// Lists all slicer engines available in the plugin registry, filtered to
    /// versions that have at least one <em>configured</em> worker (issue #1772).
    /// A registry version with zero <see cref="SlicerService"/> rows in any
    /// status — the "never configured, nobody could ever run this" case
    /// (e.g. a stale OrcaSlicer 2.3.1 plugin left installed after every
    /// worker moved to 2.4.2) — is dropped entirely rather than surfaced as a
    /// disabled "(offline)" option; it is noise, not a real choice. A version
    /// backed by at least one service row that happens to be offline right
    /// now IS still configured, so it remains listed with <c>available:false</c>
    /// — the "(offline)" disabled option — since workers restart and this
    /// distinguishes "was set up, temporarily down" from "was never set up".
    /// This per-version filter only applies to an engine that has AT LEAST
    /// ONE configured version somewhere in its group; an engine with ZERO
    /// configured versions at all keeps its full, all-unavailable version
    /// list instead of collapsing to an empty array. This matters because the
    /// React client's submit guards (<c>NewSliceJobPage.tsx</c>,
    /// <c>QuickSliceModal.tsx</c>) detect "no worker available for this
    /// engine" by checking that <c>versions.length &gt; 0</c> with none
    /// available — an empty array reads as "nothing to check" and would
    /// silently let a job dispatch unpinned to an engine with no workers at
    /// all, which is the exact failure this endpoint exists to prevent, just
    /// promoted from per-version to whole-engine scope.
    /// Each version carries an <c>available</c> flag that is true only when at
    /// least one Online <see cref="SlicerService"/> currently advertises that
    /// (engine, version) pair, so the UI never pins a job to a version no
    /// worker can serve right now. When there are no registered
    /// <see cref="SlicerService"/> rows at all (fresh install / legacy
    /// single-worker deployments that never call /api/slicers/register), no
    /// configuration data exists to filter against, so every registry version
    /// is kept and reported as available so the version pin remains usable.
    /// Once at least one row exists, both the filter and the availability flag
    /// are gated by that row data — offline services are honoured.
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

        // "Configured" is any service row for (engine, version), in ANY status
        // (Online or Offline) — this answers "has a worker for this version
        // ever been registered", not "is it up right now". Unlike `online`
        // above, this set is NOT freshness-gated: a worker that registered a
        // version and later went stale/offline still proves that version was
        // set up, which is exactly the distinction issue #1772 asks for.
        HashSet<(string Engine, string Version)> configured = services
            .Where(s => !string.IsNullOrWhiteSpace(s.Version))
            .Select(s => (Engine: SlicerTypeToEngineName(s.SlicerType), Version: s.Version!.Trim()))
            .Where(t => !string.IsNullOrEmpty(t.Engine))
            .ToHashSet();

        // Fresh install / legacy deployment fallback: only when NO service rows
        // exist at all do we mark every registry version available and skip
        // the "configured" filter — there is no configuration data yet to
        // filter against, and dropping every version would leave a blank,
        // unusable selector for legacy single-worker deployments. When rows
        // exist but none are Online we honour that state (marking versions
        // unavailable) — otherwise the UI would happily let the user pin a
        // job to a version that will hang in the queue forever.
        bool anyServiceRows = services.Count > 0;

        var engines = libraries
            .GroupBy(l => l.SlicerName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                // Only drop unconfigured versions when THIS engine has at
                // least one configured version elsewhere in the group. If an
                // entire engine has zero configured workers (nobody has ever
                // registered ANY version of it), we deliberately do NOT empty
                // out its versionEntries: NewSliceJobPage.tsx's submit guard
                // (`engineInfo.versions.length > 0 && !latest && !anyAvailable`)
                // and QuickSliceModal's equivalent both rely on a non-empty
                // `versions` array to detect "engine known but has no worker"
                // and block submission with an error. An empty array reads as
                // "nothing to check" and would silently let the job go out
                // unpinned to an engine with zero workers — the exact failure
                // these guards exist to prevent, just promoted from
                // per-version to whole-engine scope (Bishop/Hicks finding on
                // issue #1772's PR). Keeping the full, all-unavailable list
                // preserves that guard while still fixing the reported bug:
                // when an engine DOES have a configured version (e.g.
                // OrcaSlicer 2.4.2), its never-configured siblings (2.3.1)
                // are still dropped as noise.
                bool engineHasAnyConfiguredVersion = g.Any(l => configured.Contains((g.Key, l.SlicerVersion)));

                var versionEntries = g

                    // Drop registry versions with no configured worker in any
                    // status (issue #1772) — nobody could ever run these, so
                    // they're noise rather than a real "offline" choice. Only
                    // applies once service rows exist AND this engine has at
                    // least one configured version; the legacy fallback (no
                    // rows at all) and the all-unconfigured-engine case both
                    // keep every version to stay usable / preserve the
                    // frontend's no-worker submit guard (see above).
                    .Where(l => !anyServiceRows
                                || !engineHasAnyConfiguredVersion
                                || configured.Contains((g.Key, l.SlicerVersion)))
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
