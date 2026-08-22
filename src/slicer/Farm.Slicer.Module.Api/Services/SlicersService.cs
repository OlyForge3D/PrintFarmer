using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Logging;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Services.Catalog;
using Farm.Infrastructure.Services.Gcode;
using Farm.Slicer.Module.Api.Hubs;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Metrics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Services;

/// <summary>
/// Service for managing 3D printer slicer workers and their lifecycle, including registration,
/// heartbeat monitoring, capacity tracking, and health status management.
/// </summary>
/// <remarks>
/// This service orchestrates all slicer worker operations across the printing farm, including:
/// - Worker registration and deregistration with capacity tracking
/// - Real-time heartbeat monitoring to detect failed or unresponsive workers
/// - Dynamic capacity calculation (total capacity, available slots, active job count)
/// - Health status assessment with automatic unhealthy worker identification
/// - SignalR broadcast notifications for worker state changes
/// - Metrics collection for capacity and worker utilization tracking
/// - Integration with process profile repository for worker capability validation
///
/// The service maintains a registry of active slicer workers and their current state,
/// enabling the job queue system to intelligently distribute slicing jobs across available
/// workers based on their capacity and health status.
/// </remarks>
public class SlicersService : Farm.Slicer.Module.Services.ISlicersService
{
    private readonly ISlicersRepository _repo;
    private readonly IWorkerRepository _workerRepo;
    private readonly IProcessProfileRepository _profileRepo;
    private readonly IFilamentProfileRepository _filamentProfileRepo;
    private readonly IMachineProfileRepository _machineProfileRepo;
    private readonly IMachineModelProfileRepository _machineModelProfileRepo;
    private readonly ICatalogService _catalogService;
    private readonly IPrinterModelAliasService _aliasService;
    private readonly Farm.Infrastructure.Settings.ISettingsService _settingsService;
    private readonly IHubContext<SlicerHub> _hub;
    private readonly SlicerServiceMetrics _metrics;
    private readonly HttpClient _httpClient;
    private readonly ILogger<SlicersService> _logger;

    private readonly Microsoft.Extensions.Options.IOptionsMonitor<Farm.Slicer.Module.Settings.SlicerSettings> _slicerSettings;

    /// <summary>
    /// Initializes a new instance of the SlicersService with required dependencies.
    /// Capacity gauges are refreshed out-of-band by <c>SlicerCapacityMetricsRefreshService</c>,
    /// which owns its own DI scope — this constructor does not register any callbacks on the
    /// singleton <see cref="SlicerServiceMetrics"/> (see #1676).
    /// </summary>
    /// <param name="repo">Repository for slicer service data persistence and retrieval</param>
    /// <param name="workerRepo">Repository for worker data access and management</param>
    /// <param name="profileRepo">Repository for process profile data access</param>
    /// <param name="filamentProfileRepo">Repository for filament profile data access</param>
    /// <param name="machineProfileRepo">Repository for machine profile data access</param>
    /// <param name="machineModelProfileRepo">Repository for machine model profile (base templates) data access</param>
    /// <param name="catalogService">Service for manufacturer and printer model catalog lookups</param>
    /// <param name="aliasService">Service for resolving slicer profile model names to catalog PrinterModel IDs via aliases</param>
    /// <param name="settingsService">Service for managing application settings and distributed locks</param>
    /// <param name="hub">SignalR hub context for broadcasting worker state changes to connected clients</param>
    /// <param name="metrics">Metrics collection service for capacity and utilization tracking</param>
    /// <param name="httpClient">HTTP client for external service communication and health checks</param>
    /// <param name="logger">Logger for diagnostic and error logs</param>
    /// <param name="slicerSettings">Configuration options for slicer service behavior and constraints</param>
    /// <exception cref="ArgumentNullException">Thrown when any required dependency is null</exception>
    public SlicersService(
        ISlicersRepository repo,
        IWorkerRepository workerRepo,
        IProcessProfileRepository profileRepo,
        IFilamentProfileRepository filamentProfileRepo,
        IMachineProfileRepository machineProfileRepo,
        IMachineModelProfileRepository machineModelProfileRepo,
        ICatalogService catalogService,
        IPrinterModelAliasService aliasService,
        Farm.Infrastructure.Settings.ISettingsService settingsService,
        IHubContext<SlicerHub> hub,
        SlicerServiceMetrics metrics,
        HttpClient httpClient,
        ILogger<SlicersService> logger,
        Microsoft.Extensions.Options.IOptionsMonitor<Farm.Slicer.Module.Settings.SlicerSettings> slicerSettings)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _workerRepo = workerRepo ?? throw new ArgumentNullException(nameof(workerRepo));
        _profileRepo = profileRepo ?? throw new ArgumentNullException(nameof(profileRepo));
        _filamentProfileRepo = filamentProfileRepo ?? throw new ArgumentNullException(nameof(filamentProfileRepo));
        _machineProfileRepo = machineProfileRepo ?? throw new ArgumentNullException(nameof(machineProfileRepo));
        _machineModelProfileRepo = machineModelProfileRepo ?? throw new ArgumentNullException(nameof(machineModelProfileRepo));
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _aliasService = aliasService ?? throw new ArgumentNullException(nameof(aliasService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _slicerSettings = slicerSettings ?? throw new ArgumentNullException(nameof(slicerSettings));
    }

    /// <summary>
    /// Retrieves all registered slicer worker services from the system.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Read-only list of all registered slicer services with their configuration</returns>
    /// <remarks>
    /// This method queries the repository for all slicer services regardless of their health status.
    /// Use this when you need to inspect all configured slicer workers in the farm.
    /// </remarks>
    public async Task<IReadOnlyList<SlicerService>> ListAsync(CancellationToken ct)
    {
        return await _repo.ListAsync(ct);
    }

    /// <summary>
    /// Registers a new slicer worker service with the system and generates an API key for authentication.
    /// </summary>
    /// <param name="dto">Registration request containing worker details (name, URL, max concurrent jobs, capabilities)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Tuple containing the assigned worker ID and API key for authentication</returns>
    /// <remarks>
    /// This method performs the following operations:
    /// - Validates the registration request and worker connectivity
    /// - Generates a unique API key for secure communication
    /// - Stores the worker configuration in the repository
    /// - Broadcasts the new worker registration to all connected clients via SignalR
    /// - Updates capacity metrics to reflect the new worker's available capacity
    ///
    /// The returned API key must be securely transmitted to the worker and used for all subsequent API calls.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown if worker registration fails or worker is unreachable</exception>
    public async Task<(Guid Id, string ApiKey)> RegisterAsync(RegisterSlicerDto dto, CancellationToken ct)
    {
        int maxJobs = Math.Min(dto.MaxConcurrentJobs, Math.Max(1, _slicerSettings.CurrentValue.MaxConcurrentJobs));

        SlicerService svc = await UpsertServiceAndWorkerAsync(dto, maxJobs, ct);

        // Enable the slicer feature on first worker registration.
        // This is the single source of truth: slicer UI is shown only when a worker exists.
        try
        {
            var currentSettings = _settingsService.Get<Farm.Slicer.Module.Settings.SlicerSettings>();
            if (!currentSettings.Enabled)
            {
                currentSettings.Enabled = true;
                _settingsService.Save(currentSettings);
                _logger.LogInformation("Slicer feature enabled — first worker registered");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[RegisterAsync] Failed to enable slicer setting: {ExMessage}", ex.Message);
        }

        // Seed profiles from the worker (OrcaSlicer only) - only if explicitly enabled
        // Default is pull-based (profiles imported on-demand when printers are added)
        if (svc.SlicerType == 1 &&
            dto.SeedProfilesOnRegistration &&
            OrcaSlicerProfileCompatibility.IsSupportedVersion(svc.Version) &&
            CalibrationContractConstants.AttestsUpstreamSlicer(svc.CapabilitiesJson))
        {
            try
            {
                _logger.LogInformation(
                    "OrcaSlicer service {SlicerServiceId} registered with push seeding enabled",
                    svc.Id);
                await SeedProfilesFromWorkerAsync(svc, ct);
                _logger.LogInformation("Profile seeding completed");
            }
            catch (Exception ex)
            {
                // Log but don't fail registration if profile seeding fails
                _logger.LogWarning("Failed to seed profiles from worker: {ExMessage}", ex.Message);
            }
        }
        else if (svc.SlicerType == 1)
        {
            _logger.LogInformation(
                "OrcaSlicer service registered without upstream profile seeding");
        }

        // Record metrics
        _metrics.RecordServiceRegistration(GetSlicerTypeName(svc.SlicerType), svc.Id.ToString());

        // Broadcast registration event (best-effort)
        try
        {
            await _hub.Clients.Group(Farm.Infrastructure.Security.AuthorizedHubGroups.Administrators).SendAsync(SlicerHubEvents.SlicerRegistered, new
            {
                id = svc.Id,
                name = svc.Name,
                slicerType = svc.SlicerType,
                version = svc.Version,
                maxConcurrentJobs = Math.Min(svc.MaxConcurrentJobs, Math.Max(1, _slicerSettings.CurrentValue.MaxConcurrentJobs)),
                status = svc.Status,
                lastSeen = svc.LastSeen
            }, ct);
        }
        catch
        {
            // ignore broadcasting failures
        }

        return (svc.Id, svc.ApiKey ?? string.Empty);
    }

    /// <summary>
    /// Creates or updates the <see cref="SlicerService"/>/<see cref="Worker"/> pair for a
    /// registration and persists the change.
    /// </summary>
    /// <remarks>
    /// Every registration receives fresh credentials — InstanceId is never a
    /// key-recovery mechanism, so an old ApiKey can never be reclaimed by claiming a
    /// known instance ID. It is only used to find an existing row for a stable
    /// worker (e.g. the same container redeploying) so that row can be updated in
    /// place instead of the registration accumulating a new duplicate service/worker
    /// on every restart (issue #1528).
    ///
    /// The lookup-then-insert is not atomic, so two concurrent registrations for the
    /// same InstanceId could otherwise both decide no existing row exists and both
    /// try to insert one. A unique database index on <see cref="SlicerService.InstanceId"/>
    /// (see <c>SlicerServiceConfiguration</c>) makes the loser's insert fail with a
    /// <see cref="DbUpdateException"/> instead of silently creating a duplicate; when
    /// that happens we discard our tracked entities and retry once, which finds and
    /// updates the winner's row instead of surfacing an error.
    /// </remarks>
    private async Task<SlicerService> UpsertServiceAndWorkerAsync(RegisterSlicerDto dto, int maxJobs, CancellationToken ct)
    {
        bool hasInstanceId = !string.IsNullOrWhiteSpace(dto.InstanceId);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            string freshApiKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

            SlicerService? svc = hasInstanceId
                ? await _repo.GetByInstanceIdAsync(dto.InstanceId!, ct)
                : null;

            bool insertingNewInstanceRecord = svc is null && hasInstanceId;

            if (svc is not null)
            {
                _logger.LogInformation(
                    "Re-registering slicer service {ServiceId} for stable worker instance {InstanceId}; issuing fresh credentials.",
                    svc.Id,
                    LogSanitizer.Sanitize(dto.InstanceId));

                svc.Name = dto.Name ?? svc.Name;
                svc.SlicerType = dto.SlicerType;
                svc.Version = dto.Version;
                svc.Host = dto.Host;
                svc.UiManifestUrl = dto.UiManifestUrl;
                svc.CapabilitiesJson = dto.CapabilitiesJson;
                svc.MaxConcurrentJobs = maxJobs;
                svc.Status = "Online";
                svc.LastSeen = DateTime.UtcNow;
                svc.UpdatedAt = DateTime.UtcNow;
                svc.Tags = dto.Tags;
                svc.ApiKey = freshApiKey;
                svc.ApiKeyRotatedAt = DateTime.UtcNow;
            }
            else
            {
                svc = new SlicerService
                {
                    Id = Guid.NewGuid(),
                    Name = dto.Name ?? "orca-service",
                    SlicerType = dto.SlicerType,
                    Version = dto.Version,
                    Host = dto.Host,
                    UiManifestUrl = dto.UiManifestUrl,
                    CapabilitiesJson = dto.CapabilitiesJson,
                    MaxConcurrentJobs = maxJobs,
                    Status = "Online",
                    LastSeen = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Tags = dto.Tags,
                    InstanceId = dto.InstanceId,
                    ApiKey = freshApiKey,
                    ApiKeyRotatedAt = DateTime.UtcNow,
                };

                await _repo.AddAsync(svc, ct);
            }

            // Synchronize to Worker table for dispatcher
            Worker? worker = await _workerRepo.GetByServiceIdAsync(svc.Id.ToString());

            if (worker != null)
            {
                // Only lift a disable that the system applied itself. An administrator's
                // deliberate disable must survive a restart — otherwise any banned worker could
                // clear its own ban just by re-registering under the same InstanceId, and the
                // reason text recording why it was banned would be erased with it. Re-enabling
                // stays an explicit admin action (IWorkerRepository.EnableWorkerAsync).
                //
                // Without this a reclaimed worker comes back Online while still reporting
                // "Disabled: Slicer service deregistered", which is exactly the stale text
                // operators saw after every redeploy.
                //
                // The test and the write happen together in the database. Deciding it here from
                // the instance loaded above would read a snapshot an administrator can invalidate
                // mid-request, and saving that snapshot would write IsDisabled = false straight
                // over a ban committed since. Runs before the edits below, because it refreshes
                // the tracked copy.
                _ = await _workerRepo.ClearAutomaticDisableAsync(svc.Id.ToString(), ct);

                worker.ServiceId = svc.Id.ToString();
                worker.Name = svc.Name;
                worker.EndpointUrl = svc.Host ?? string.Empty;
                worker.CapabilitiesJson = svc.CapabilitiesJson ?? "[]";
                worker.Status = WorkerStatus.Online;
                worker.TotalSlots = maxJobs;
                worker.ActiveJobs = 0;
                worker.LastHeartbeat = DateTime.UtcNow;
                worker.OnlineAt = DateTime.UtcNow;
                worker.ApiKey = svc.ApiKey;
                worker.Version = svc.Version;
                worker.UpdatedAt = DateTime.UtcNow;

                // The worker is demonstrably running again, so it is no longer Offline.
                worker.OfflineAt = null;
            }
            else
            {
                worker = new Worker
                {
                    Id = Guid.NewGuid(),
                    ServiceId = svc.Id.ToString(),
                    Name = svc.Name,
                    EndpointUrl = svc.Host ?? string.Empty,
                    CapabilitiesJson = svc.CapabilitiesJson ?? "[]",
                    Status = WorkerStatus.Online,
                    TotalSlots = maxJobs,
                    ActiveJobs = 0,
                    CompletedJobs = 0,
                    FailedJobs = 0,
                    LastHeartbeat = DateTime.UtcNow,
                    RegisteredAt = DateTime.UtcNow,
                    OnlineAt = DateTime.UtcNow,
                    ApiKey = svc.ApiKey,
                    Version = svc.Version,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    IsDisabled = false
                };

                await _workerRepo.AddAsync(worker);
            }

            try
            {
                // SlicerService and Worker share the scoped DbContext, so this single save is atomic.
                await _repo.SaveChangesAsync(ct);
                return svc;
            }
            catch (DbUpdateException) when (attempt == 0 && insertingNewInstanceRecord)
            {
                _logger.LogInformation(
                    "Concurrent registration for instance {InstanceId} won the race to insert a new row; retrying as an update against its record.",
                    LogSanitizer.Sanitize(dto.InstanceId));
                _repo.ClearTracking();
            }
        }

        // Unreachable: attempt 0 either returns or, on a non-retryable failure,
        // rethrows past this loop; attempt 1 either returns or lets any exception
        // propagate (the retry guard only matches attempt 0). Present only to
        // satisfy the compiler's flow analysis.
        throw new InvalidOperationException("Unreachable: RegisterAsync retry loop exited without returning or throwing.");
    }

    private static string GetSlicerTypeName(int slicerType)
    {
        return slicerType switch
        {
            0 => "PrusaSlicer",
            1 => "OrcaSlicer",
            2 => "Cura",
            3 => "SuperSlicer",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Retrieves a specific slicer worker service by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier of the slicer service to retrieve</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>The slicer service if found; otherwise null</returns>
    /// <remarks>
    /// This method queries the repository for a specific slicer worker without any filtering.
    /// Returns null if the worker has been deregistered or does not exist.
    /// </remarks>
    public async Task<SlicerService?> GetAsync(Guid id, CancellationToken ct)
    {
        return await _repo.GetByIdAsync(id, ct);
    }

    /// <summary>
    /// Processes a heartbeat signal from a slicer worker and updates its status and metrics.
    /// </summary>
    /// <param name="id">The unique identifier of the slicer worker sending the heartbeat</param>
    /// <param name="dto">Heartbeat data including current status, free slots, and worker health indicators</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if heartbeat was processed successfully; false if worker not found</returns>
    /// <remarks>
    /// This method performs the following operations:
    /// - Updates the slicer service's LastSeen timestamp and status
    /// - Synchronizes worker status to the Worker table for dispatcher coordination
    /// - Records heartbeat metrics for monitoring and analysis
    /// - Broadcasts heartbeat events to connected clients via SignalR
    /// - Calculates and records latency metrics for performance monitoring
    ///
    /// Heartbeats are critical for health monitoring and worker availability tracking.
    /// Missing heartbeats indicate worker connectivity issues or failure.
    /// </remarks>
    public async Task<bool> HeartbeatAsync(Guid id, HeartbeatDto dto, CancellationToken ct)
    {
        DateTime startTime = DateTime.UtcNow;
        SlicerService? svc = await _repo.GetByIdAsync(id, ct);
        if (svc == null)
        {
            return false;
        }

        svc.LastSeen = DateTime.UtcNow;
        svc.Status = dto.Status ?? svc.Status;
        if (dto.FreeSlots.HasValue)
        {
            svc.Tags = dto.FreeSlots.Value.ToString();
        }

        svc.UpdatedAt = DateTime.UtcNow;

        await _repo.SaveChangesAsync(ct);

        int? totalSlots = null;

        // Synchronize to Worker table for dispatcher
        try
        {
            Worker? worker = await _workerRepo.GetByServiceIdAsync(id.ToString());
            if (worker != null)
            {
                // Update existing worker
                worker.Status = MapStatus(dto.Status ?? svc.Status ?? "Online");
                worker.LastHeartbeat = DateTime.UtcNow;
                worker.UpdatedAt = DateTime.UtcNow;

                // Update FreeSlots/ActiveJobs if provided in heartbeat
                if (dto.FreeSlots.HasValue)
                {
                    worker.ActiveJobs = Math.Max(0, worker.TotalSlots - dto.FreeSlots.Value);
                }

                totalSlots = worker.TotalSlots;
                await _repo.SaveChangesAsync(ct); // Worker entity tracked by same DbContext
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail heartbeat if Worker sync fails
            _logger.LogWarning("[HeartbeatAsync] Failed to sync Worker heartbeat: {ExMessage}", ex.Message);
        }

        // Record heartbeat metrics
        double latencyMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
        _metrics.RecordServiceHeartbeat(
            GetSlicerTypeName(svc.SlicerType),
            id.ToString(),
            success: true,
            latencyMs,
            dto.FreeSlots,
            totalSlots);

        try
        {
            await _hub.Clients.Group(Farm.Infrastructure.Security.AuthorizedHubGroups.Administrators).SendAsync(SlicerHubEvents.SlicerHeartbeat, new
            {
                id = svc.Id,
                name = svc.Name,
                status = svc.Status,
                freeSlots = dto.FreeSlots,
                lastSeen = svc.LastSeen
            }, ct);
        }
        catch
        {
            // ignore
        }

        return true;
    }

    /// <summary>
    /// Map SlicerService status to Worker status constants
    /// </summary>
    private static string MapStatus(string slicerStatus)
    {
        return slicerStatus switch
        {
            "Online" => WorkerStatus.Online,
            "Busy" => WorkerStatus.Busy,
            "Draining" => WorkerStatus.Draining,
            "Offline" => WorkerStatus.Offline,
            "Error" => WorkerStatus.Error,
            _ => WorkerStatus.Online
        };
    }

    /// <summary>
    /// Deregisters a slicer worker service and revokes its worker credentials.
    /// </summary>
    /// <param name="id">The unique identifier of the slicer worker to deregister</param>
    /// <param name="retainForReregistration">
    /// Whether the caller will return under the same <see cref="SlicerService.InstanceId"/> and
    /// wants its row kept so it can be re-identified. Defaults to <see langword="false"/>, which
    /// preserves the historical delete-on-deregister behaviour for any client unaware of it.
    /// </param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if deregistration was successful; false if worker not found</returns>
    /// <remarks>
    /// This is the worker-initiated path: a worker calls it from its own shutdown handler to
    /// say "I am going away right now", which is not the same as "delete me permanently".
    /// The administrative "remove this slicer" action is <see cref="PurgeAsync"/>.
    ///
    /// <para><b>Why retention is opt-in and caller-declared.</b> The service row is the only
    /// anchor <see cref="UpsertServiceAndWorkerAsync"/> can match a returning worker against,
    /// so unconditionally deleting it is what made every redeploy register a duplicate: a
    /// graceful shutdown — exactly what <c>deploy-docker.sh</c> triggers when it recreates the
    /// container — deleted the anchor, the replacement container then failed to match its own
    /// InstanceId, and both a new <see cref="SlicerService"/> and, because Worker rows are
    /// keyed by the service's Guid, a new <see cref="Worker"/> row were inserted, orphaning
    /// the old one as "Disabled: Slicer service deregistered" forever. Retention fixes that.</para>
    ///
    /// <para>But retention is only ever correct when the worker really does return under the
    /// same identity, and the presence of an InstanceId does <b>not</b> establish that. A
    /// worker always sends one: it falls back to a fresh random per-process
    /// <c>WorkerIdentity.Create()</c> GUID whenever no stable ID is configured. Workers
    /// deployed by <c>deploy-docker.sh</c> always have one configured — single deployments
    /// through <c>ORCA_WORKER_INSTANCE_ID</c>, scaled ones through a literal
    /// <c>Worker__InstanceId</c> baked into each per-replica service block (issue #1847) — but
    /// a worker started outside that tooling need not, and retaining rows for those throwaway
    /// identities would leave one unreclaimable Offline row per process start — the same orphan
    /// accumulation this change exists to stop, on the one path that previously cleaned up
    /// after itself.</para>
    ///
    /// <para>Only the worker knows which kind of identity it holds, so it declares it via
    /// <paramref name="retainForReregistration"/>. The default is <see langword="false"/> so
    /// the behaviour is fail-safe: a client that never sets it gets the old self-cleaning
    /// delete. The InstanceId check below is a secondary guard — retention is meaningless
    /// without an anchor to match on.</para>
    ///
    /// Credentials are revoked identically on both paths — the service key is cleared and the
    /// Worker record is disabled and marked Offline — so a retained row can never be used to
    /// keep authenticating. <c>SlicerApiKeyValidator</c> requires a matching service key
    /// <i>and</i> an enabled, non-Offline Worker, so all three conditions fail after this
    /// call. A worker that returns is issued a fresh key by registration; retention is never
    /// a key-recovery mechanism.
    ///
    /// Deregistered workers are no longer available for job assignment. The system
    /// automatically fails over any pending jobs from deregistered workers.
    /// </remarks>
    public async Task<bool> DeregisterAsync(Guid id, bool retainForReregistration, CancellationToken ct)
    {
        SlicerService? svc = await _repo.GetByIdAsync(id, ct);
        if (svc == null)
        {
            return false;
        }

        string slicerTypeName = GetSlicerTypeName(svc.SlicerType);
        bool retain = retainForReregistration && !string.IsNullOrWhiteSpace(svc.InstanceId);

        _ = await _workerRepo.RevokeForDeregistrationAsync(id.ToString(), ct);

        if (retain)
        {
            svc.Status = "Offline";
            svc.ApiKey = null;
            svc.UpdatedAt = DateTime.UtcNow;

            _logger.LogInformation(
                "Slicer service {ServiceId} deregistered; retaining its row so worker instance {InstanceId} is re-identified when it returns.",
                svc.Id,
                LogSanitizer.Sanitize(svc.InstanceId));
        }
        else
        {
            await _repo.RemoveAsync(svc, ct);
        }

        await _repo.SaveChangesAsync(ct);

        // Record metrics
        _metrics.RecordServiceDeregistration(slicerTypeName, id.ToString(), "normal");

        try
        {
            await _hub.Clients.Group(Farm.Infrastructure.Security.AuthorizedHubGroups.Administrators)
                .SendAsync(SlicerHubEvents.SlicerDeregistered, new { id = svc.Id, name = svc.Name }, ct);
        }
        catch
        {
            // ignore
        }

        return true;
    }

    /// <summary>
    /// Permanently removes a slicer worker service and its paired <see cref="Worker"/> record.
    /// </summary>
    /// <param name="id">The unique identifier of the slicer worker to remove</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if the service was removed; false if it was not found</returns>
    /// <remarks>
    /// This is the administrative "remove this slicer" action, and it is deliberately separate
    /// from <see cref="DeregisterAsync"/>. Worker-initiated deregistration now retains the row
    /// of any worker carrying a stable InstanceId so the worker can be re-identified, so the
    /// admin action needs its own path to still mean permanent removal.
    ///
    /// Unlike the old shared implementation this also deletes the paired <see cref="Worker"/>
    /// row rather than leaving it disabled. An admin removing a service otherwise left a Worker
    /// row whose <see cref="Worker.ServiceId"/> pointed at a service that no longer existed —
    /// an orphan nothing could reclaim or clean up except the stale-worker sweep.
    ///
    /// A worker process that is still running will simply register again on its next heartbeat
    /// cycle, exactly as it did before this method existed.
    /// </remarks>
    public async Task<bool> PurgeAsync(Guid id, CancellationToken ct)
    {
        SlicerService? svc = await _repo.GetByIdAsync(id, ct);
        if (svc == null)
        {
            return false;
        }

        string slicerTypeName = GetSlicerTypeName(svc.SlicerType);

        Worker? worker = await _workerRepo.GetByServiceIdAsync(id.ToString());
        if (worker != null)
        {
            // No credential revocation here: unlike deregistration this deletes the row outright,
            // so blanking its columns first would write state nothing can ever read.
            await _workerRepo.DeleteAsync(worker.Id);
        }

        await _repo.RemoveAsync(svc, ct);
        await _repo.SaveChangesAsync(ct);

        _metrics.RecordServiceDeregistration(slicerTypeName, id.ToString(), "normal");

        try
        {
            await _hub.Clients.Group(Farm.Infrastructure.Security.AuthorizedHubGroups.Administrators)
                .SendAsync(SlicerHubEvents.SlicerDeregistered, new { id = svc.Id, name = svc.Name }, ct);
        }
        catch
        {
            // ignore
        }

        return true;
    }

    /// <summary>
    /// Rotates the API key for a slicer worker service for security purposes.
    /// </summary>
    /// <param name="id">The unique identifier of the slicer worker</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <param name="isAdminForced">Whether this rotation is admin-forced for security reasons (true) or routine (false)</param>
    /// <returns>The new API key if rotation was successful; null if worker not found</returns>
    /// <remarks>
    /// This method performs the following operations:
    /// - Generates a new cryptographically secure API key
    /// - Replaces the old API key in both SlicerService and Worker tables
    /// - Persists the new key to the database
    /// - Broadcasts key rotation notification to connected clients
    /// - Records metrics for API key rotation events
    ///
    /// API key rotation is recommended for security maintenance. The new key must be
    /// communicated securely to the worker and updated in its configuration.
    /// The isAdminForced parameter indicates whether this rotation is mandatory
    /// due to security concerns (e.g., suspected key compromise).
    /// </remarks>
    public async Task<string?> RotateApiKeyAsync(Guid id, CancellationToken ct, bool isAdminForced = false)
    {
        SlicerService? svc = await _repo.GetByIdAsync(id, ct);
        if (svc == null)
        {
            return null;
        }

        string slicerTypeName = GetSlicerTypeName(svc.SlicerType);

        // Generate new API key
        string newApiKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Replace("=", string.Empty);
        svc.ApiKey = newApiKey;
        svc.ApiKeyRotatedAt = DateTime.UtcNow;
        svc.UpdatedAt = DateTime.UtcNow;

        await _repo.SaveChangesAsync(ct);

        // Synchronize to Worker table
        try
        {
            Worker? worker = await _workerRepo.GetByServiceIdAsync(id.ToString());
            if (worker != null)
            {
                worker.ApiKey = newApiKey;
                worker.UpdatedAt = DateTime.UtcNow;
                await _repo.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail rotation if Worker sync fails
            _logger.LogWarning("[RotateApiKeyAsync] Failed to sync Worker API key rotation: {ExMessage}", ex.Message);
        }

        // Record metrics
        _metrics.RecordApiKeyRotation(slicerTypeName, id.ToString(), success: true, isAdminForced);

        // Broadcast rotation event (best-effort)
        try
        {
            await _hub.Clients.Group(Farm.Infrastructure.Security.AuthorizedHubGroups.Administrators).SendAsync(SlicerHubEvents.SlicerApiKeyRotated, new
            {
                id = svc.Id,
                name = svc.Name,
                rotatedAt = svc.ApiKeyRotatedAt
            }, ct);
        }
        catch
        {
            // ignore broadcasting failures
        }

        return newApiKey;
    }

    /// <inheritdoc />
    public async Task<int> ImportProfilesForModelAsync(Guid printerModelId, string printerModelName, string manufacturerName, CancellationToken ct)
    {
        if (printerModelId == Guid.Empty || string.IsNullOrWhiteSpace(printerModelName) || string.IsNullOrWhiteSpace(manufacturerName))
        {
            _logger.LogDebug("[ImportProfilesForModel] Skipping import - invalid parameters: modelId={PrinterModelId}, modelName={PrinterModelName}, manufacturer={ManufacturerName}", printerModelId, LogSanitizer.Sanitize(printerModelName), LogSanitizer.Sanitize(manufacturerName));
            return 0;
        }

        // Check if profiles already exist for this model by checking machine model profiles
        // We use machine model profiles since they represent the base templates
        MachineModelProfile? existingProfile = await _machineModelProfileRepo.GetByPrinterModelIdAsync(printerModelId, ct);
        if (existingProfile != null)
        {
            _logger.LogDebug("[ImportProfilesForModel] Profiles already exist for {PrinterModelName} (has machine model profile), skipping import", LogSanitizer.Sanitize(printerModelName));
            return 0;
        }

        // Find an available OrcaSlicer worker
        IReadOnlyList<SlicerService> slicers = await _repo.ListAsync(ct);
        SlicerService? orcaWorker = slicers.FirstOrDefault(s =>
            s.SlicerType == 1 &&
            OrcaSlicerProfileCompatibility.IsSupportedVersion(s.Version) &&
            CalibrationContractConstants.AttestsUpstreamSlicer(s.CapabilitiesJson) &&
            !string.IsNullOrWhiteSpace(s.Host));

        if (orcaWorker == null)
        {
            _logger.LogDebug("[ImportProfilesForModel] No OrcaSlicer worker available for profile import for {PrinterModelName}", LogSanitizer.Sanitize(printerModelName));
            return 0;
        }

        _logger.LogInformation(
            "[ImportProfilesForModel] Importing slicer profiles for {ManufacturerName} {PrinterModelName} from worker {WorkerId}",
            LogSanitizer.Sanitize(manufacturerName),
            LogSanitizer.Sanitize(printerModelName),
            orcaWorker.Id);

        try
        {
            // Fetch all profiles from the worker
            string workerUrl = orcaWorker.Host!.TrimEnd('/');
            HttpResponseMessage response = await _httpClient.GetAsync($"{workerUrl}/api/profiles", ct);

            if (!response.IsSuccessStatusCode)
            {
                string errorContent = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("[ImportProfilesForModel] Worker /api/profiles returned {ResponseStatusCode}: {ErrorContent}", response.StatusCode, LogSanitizer.Sanitize(errorContent));
                return 0;
            }

            string json = await response.Content.ReadAsStringAsync(ct);
            AllProfilesResponseDto? allProfiles = JsonSerializer.Deserialize<AllProfilesResponseDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (allProfiles == null || allProfiles.ByHierarchy == null)
            {
                _logger.LogWarning("[ImportProfilesForModel] No profiles available from worker for {PrinterModelName}", LogSanitizer.Sanitize(printerModelName));
                return 0;
            }

            int imported = 0;
            HashSet<string> processedMachineModelHashes = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> processedMachineHashes = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> processedFilamentHashes = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> processedProcessHashes = new(StringComparer.OrdinalIgnoreCase);

            // Import machine model profiles for this manufacturer/model
            if (allProfiles.MachineModelProfiles?.TryGetValue(manufacturerName, out IList<MachineModelProfileDto>? modelProfiles) == true && modelProfiles != null)
            {
                foreach (MachineModelProfileDto modelProfile in modelProfiles)
                {
                    // Check if this profile matches our target model (via alias lookup)
                    Guid? resolvedModelId = await _aliasService.ResolveModelAliasAsync(modelProfile.Name ?? string.Empty, "OrcaSlicer");
                    if (resolvedModelId != printerModelId)
                    {
                        continue;
                    }

                    string profileJson = JsonSerializer.Serialize(modelProfile);
                    string profileHash = ComputeProfileHash(profileJson);

                    if (processedMachineModelHashes.Contains(profileHash))
                    {
                        continue;
                    }

                    processedMachineModelHashes.Add(profileHash);

                    MachineModelProfile? existing = await _machineModelProfileRepo.GetByHashAsync(profileHash, ct);
                    if (existing != null)
                    {
                        continue;
                    }

                    MachineModelProfile systemProfile = new MachineModelProfile
                    {
                        Id = Guid.NewGuid(),
                        Name = modelProfile.Name ?? string.Empty,
                        Manufacturer = manufacturerName,
                        PrinterModelId = printerModelId,
                        IsSystem = true,
                        IsPublic = true,
                        Hash = profileHash,
                        RawJson = profileJson,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    await _machineModelProfileRepo.AddAsync(systemProfile, ct);
                    imported++;
                    _logger.LogDebug("[ImportProfilesForModel] Imported machine model profile: {ModelProfileName}", modelProfile.Name);
                }
            }

            // Import from hierarchy structure for machine, filament, and process profiles
            if (allProfiles.ByHierarchy.TryGetValue(manufacturerName, out ManufacturerProfilesDto? manufacturerProfiles) && manufacturerProfiles?.Models != null)
            {
                foreach (KeyValuePair<string, PrinterModelProfilesDto> modelEntry in manufacturerProfiles.Models)
                {
                    PrinterModelProfilesDto profiles = modelEntry.Value;
                    string modelDisplayName = profiles.Name;

                    // Check if this model matches our target via name comparison or alias
                    bool isTargetModel = string.Equals(modelDisplayName, printerModelName, StringComparison.OrdinalIgnoreCase);
                    if (!isTargetModel)
                    {
                        Guid? resolvedId = await _aliasService.ResolveModelAliasAsync(modelDisplayName, "OrcaSlicer");
                        isTargetModel = resolvedId == printerModelId;
                    }

                    if (!isTargetModel)
                    {
                        continue;
                    }

                    // Import machine profiles (nozzle variants)
                    if (profiles.MachineProfiles != null)
                    {
                        List<MachineProfileDto> instantiable = profiles.MachineProfiles.Where(p => p.Instantiation).ToList();
                        foreach (MachineProfileDto machineProfile in instantiable)
                        {
                            string profileJson = JsonSerializer.Serialize(machineProfile);
                            string profileHash = ComputeProfileHash(profileJson);

                            if (processedMachineHashes.Contains(profileHash))
                            {
                                continue;
                            }

                            processedMachineHashes.Add(profileHash);

                            MachineProfile? existing = await _machineProfileRepo.GetByHashAsync(profileHash, ct);
                            if (existing != null)
                            {
                                continue;
                            }

                            MachineProfile systemProfile = new MachineProfile
                            {
                                Id = Guid.NewGuid(),
                                Name = machineProfile.Name ?? modelDisplayName,
                                Manufacturer = manufacturerName,
                                Description = $"OrcaSlicer machine profile for {modelDisplayName}" + (machineProfile.NozzleDiameter.HasValue ? $" ({machineProfile.NozzleDiameter}mm nozzle)" : string.Empty),
                                SlicerType = SlicerType.OrcaSlicer,
                                SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
                                ProfileFormat = CalibrationContractConstants.ProfileFormat,
                                SlicerVersion = orcaWorker.Version,
                                PrinterModelId = printerModelId,
                                IsSystem = true,
                                IsPublic = true,
                                IsDefault = false,
                                Hash = profileHash,
                                RawJson = profileJson,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            };

                            await _machineProfileRepo.AddAsync(systemProfile, ct);
                            imported++;
                        }
                    }

                    // Import filament profiles
                    if (profiles.FilamentProfiles != null)
                    {
                        List<FilamentProfileDto> instantiable = profiles.FilamentProfiles.Where(p => p.Instantiation).ToList();
                        foreach (FilamentProfileDto filamentProfile in instantiable)
                        {
                            string profileJson = JsonSerializer.Serialize(filamentProfile);
                            string profileHash = ComputeProfileHash(profileJson);

                            if (processedFilamentHashes.Contains(profileHash))
                            {
                                continue;
                            }

                            processedFilamentHashes.Add(profileHash);

                            FilamentProfile? existing = await _filamentProfileRepo.GetByHashAsync(profileHash, ct);
                            if (existing != null)
                            {
                                continue;
                            }

                            FilamentProfile systemProfile = new FilamentProfile
                            {
                                Id = Guid.NewGuid(),
                                Name = string.IsNullOrEmpty(filamentProfile.Name) ? filamentProfile.Material : filamentProfile.Name,
                                Material = filamentProfile.Material,
                                Manufacturer = filamentProfile.Manufacturer ?? manufacturerName,
                                Description = filamentProfile.Description ?? $"OrcaSlicer filament profile: {filamentProfile.Material}",
                                SlicerType = SlicerType.OrcaSlicer,
                                SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
                                ProfileFormat = CalibrationContractConstants.ProfileFormat,
                                SlicerVersion = orcaWorker.Version,
                                NozzleTemperature = filamentProfile.NozzleTemperature,
                                BedTemperature = filamentProfile.BedTemperature,
                                PrintSpeed = filamentProfile.PrintSpeed,
                                CompatiblePrinters = filamentProfile.CompatiblePrinters?.Count > 0
                                    ? string.Join(",", filamentProfile.CompatiblePrinters)
                                    : null,
                                IsSystem = true,
                                IsPublic = true,
                                IsDefault = false,
                                Hash = profileHash,
                                RawJson = profileJson,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            };

                            await _filamentProfileRepo.AddAsync(systemProfile, ct);
                            imported++;
                        }
                    }

                    // Import process profiles
                    if (profiles.ProcessProfiles != null)
                    {
                        List<ProcessProfileDto> instantiable = profiles.ProcessProfiles.Where(p => p.Instantiation).ToList();
                        foreach (ProcessProfileDto processProfile in instantiable)
                        {
                            string profileJson = JsonSerializer.Serialize(processProfile);
                            string profileHash = ComputeProfileHash(profileJson);

                            if (processedProcessHashes.Contains(profileHash))
                            {
                                continue;
                            }

                            processedProcessHashes.Add(profileHash);

                            ProcessProfile? existing = await _profileRepo.GetByHashAsync(profileHash, ct);
                            if (existing != null)
                            {
                                continue;
                            }

                            ProcessProfile systemProfile = new ProcessProfile
                            {
                                Id = Guid.NewGuid(),
                                Name = string.IsNullOrEmpty(processProfile.Name) ? $"{processProfile.Quality} ({processProfile.LayerHeight}mm)" : processProfile.Name,
                                Description = processProfile.Description ?? $"OrcaSlicer process profile: {processProfile.Quality} quality at {processProfile.LayerHeight}mm layer height",
                                SlicerType = SlicerType.OrcaSlicer,
                                SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
                                ProfileFormat = CalibrationContractConstants.ProfileFormat,
                                SlicerVersion = orcaWorker.Version,
                                PrinterModelId = printerModelId,
                                Quality = Enum.TryParse(processProfile.Quality ?? "standard", true, out ProfileQuality q) ? q : ProfileQuality.Standard,
                                LayerHeight = processProfile.LayerHeight,
                                InfillPercentage = processProfile.InfillPercentage,
                                PrintSpeed = processProfile.PrintSpeed,
                                EnableSupports = processProfile.Supports,
                                CompatiblePrinters = processProfile.CompatiblePrinters?.Count > 0
                                    ? string.Join(",", processProfile.CompatiblePrinters)
                                    : null,
                                IsSystem = true,
                                IsPublic = true,
                                IsDefault = false,
                                Hash = profileHash,
                                RawJson = profileJson,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            };

                            await _profileRepo.AddAsync(systemProfile, ct);
                            imported++;
                        }
                    }
                }
            }

            if (imported > 0)
            {
                await _repo.SaveChangesAsync(ct);
                _logger.LogInformation("[ImportProfilesForModel] Successfully imported {Imported} slicer profiles for {ManufacturerName} {PrinterModelName}", imported, LogSanitizer.Sanitize(manufacturerName), LogSanitizer.Sanitize(printerModelName));
            }
            else
            {
                _logger.LogDebug("[ImportProfilesForModel] No new profiles to import for {ManufacturerName} {PrinterModelName}", LogSanitizer.Sanitize(manufacturerName), LogSanitizer.Sanitize(printerModelName));
            }

            return imported;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[ImportProfilesForModel] Error importing profiles for {PrinterModelName}: {ExMessage}", LogSanitizer.Sanitize(printerModelName), LogSanitizer.Sanitize(ex.Message));
            return 0;
        }
    }

    /// <summary>
    /// Seed OrcaSlicer profiles from the worker into the database on registration.
    /// Only runs when a worker opts in via <c>SeedProfilesOnRegistration</c> (push seeding); the
    /// default is pull-based, where profiles are imported on demand as printers are added.
    /// Only seeds if no system OrcaSlicer profiles exist yet, and uses a distributed lock so seeding
    /// happens once even with multiple concurrent worker registrations.
    /// Profiles are filtered to only include those for manufacturers and models in the catalog.
    /// </summary>
    private async Task SeedProfilesFromWorkerAsync(
        SlicerService worker,
        CancellationToken ct)
    {
        string workerHost = worker.Host ?? string.Empty;
        const string SEED_LOCK_KEY = "SystemOrcaSlicerProfilesSeedLock";

        try
        {
            // Attempt to acquire distributed lock to prevent concurrent seeding
            bool lockAcquired = await _settingsService.TryAcquireLockAsync(SEED_LOCK_KEY, ct);
            if (!lockAcquired)
            {
                _logger.LogInformation("[SeedProfilesFromWorker] System OrcaSlicer profiles already seeded or seeding in progress, skipping");
                return;
            }

            _logger.LogInformation("[SeedProfilesFromWorker] Acquired distributed lock for profile seeding");

            // Early exit: Verify no system profiles exist (double-check after acquiring lock)
            IReadOnlyList<ProcessProfile> existingSystemProfiles = await _profileRepo.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: true, userId: null, ct);
            if (existingSystemProfiles.Any(p => p.IsSystem))
            {
                _logger.LogInformation("[SeedProfilesFromWorker] System OrcaSlicer profiles already exist, skipping seed (detected after acquiring lock)");
                await _settingsService.CompleteLockAsync(SEED_LOCK_KEY, ct);
                return;
            }

            // #1779: guard inserts on a stable IDENTITY, not on the content hash the per-profile checks
            // in this method use. The hash is SHA256 over the serialized worker DTO, so any change to a
            // DTO's shape between releases changes every hash — MachineProfileDto gained
            // IsHighFlowNozzle in #1806, for example — leaving a hash check unable to recognise profiles
            // it already imported. These tables carry UNIQUE indexes, so re-inserting does not merely
            // duplicate a row: it throws and leaves the failed entity tracked, which can then block the
            // very HF inserts this fix is about. Each identity below mirrors its table's declared index
            // (machine/machine-model on Name, filament on Name+Material, process on Name+PrinterModelId)
            // and covers ALL rows, not just system ones, because those indexes are global. Loading them
            // once also replaces one database roundtrip per profile with one query per type.
            HashSet<string> existingMachineModelNames = await LoadExistingProfileIdentitiesAsync(
                async token => (await _machineModelProfileRepo.GetByEngineAsync(SlicerType.OrcaSlicer, token)).Select(p => (p.Name ?? string.Empty).Trim()), ct);
            HashSet<string> existingMachineNames = await LoadExistingProfileIdentitiesAsync(
                async token => (await _machineProfileRepo.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, token)).Select(p => (p.Name ?? string.Empty).Trim()), ct);
            HashSet<string> existingFilamentNames = await LoadExistingProfileIdentitiesAsync(
                async token => (await _filamentProfileRepo.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, token)).Select(p => FilamentIdentity(p.Name, p.Material)), ct);
            HashSet<string> existingProcessNames = await LoadExistingProfileIdentitiesAsync(
                async token => (await _profileRepo.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, token)).Select(p => ProcessIdentity(p.Name, p.PrinterModelId)), ct);

            _logger.LogInformation(
                "[SeedProfilesFromWorker] Existing system profiles: {MachineModelCount} machine model, {MachineCount} machine, {FilamentCount} filament, {ProcessCount} process",
                existingMachineModelNames.Count,
                existingMachineNames.Count,
                existingFilamentNames.Count,
                existingProcessNames.Count);

            // Call the worker's /api/profiles endpoint which now returns AllProfilesResponseDto with all three profile types
            string workerUrl = workerHost.TrimEnd('/');
            _logger.LogInformation("[SeedProfilesFromWorker] Fetching profiles from worker at: {WorkerUrl}/api/profiles", LogSanitizer.Sanitize(workerUrl));
            HttpResponseMessage response = await _httpClient.GetAsync($"{workerUrl}/api/profiles", ct);

            if (!response.IsSuccessStatusCode)
            {
                string errorContent = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("[SeedProfilesFromWorker] Worker /api/profiles returned {ResponseStatusCode}: {ErrorContent}", response.StatusCode, LogSanitizer.Sanitize(errorContent));

                // Clear lock on error so retry can happen
                await _settingsService.ClearLockAsync(SEED_LOCK_KEY, ct);
                return;
            }

            string json = await response.Content.ReadAsStringAsync(ct);
            _logger.LogInformation("[SeedProfilesFromWorker] Received {JsonLength} bytes from worker", json.Length);
            AllProfilesResponseDto? allProfiles = JsonSerializer.Deserialize<AllProfilesResponseDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (allProfiles == null || (allProfiles.ProcessProfiles?.Count == 0 && allProfiles.FilamentProfiles?.Count == 0 && allProfiles.MachineProfiles?.Count == 0))
            {
                bool parsedNull = allProfiles == null;
                int processCount = allProfiles?.ProcessProfiles?.Count ?? 0;
                int filamentCount = allProfiles?.FilamentProfiles?.Count ?? 0;
                int machineCount = allProfiles?.MachineProfiles?.Count ?? 0;
                _logger.LogWarning("[SeedProfilesFromWorker] No profiles available from worker (parsed null: {ParsedNull}, process groups: {ProcessCount}, filament groups: {FilamentCount}, machine groups: {MachineCount})", parsedNull, processCount, filamentCount, machineCount);

                // Clear lock on empty response so retry can happen
                await _settingsService.ClearLockAsync(SEED_LOCK_KEY, ct);
                return;
            }

            // Get catalog manufacturers and models to filter profiles
            (IReadOnlyList<ManufacturerDto> catalogManufacturers, _) = await _catalogService.GetManufacturersAsync(ct);
            (IReadOnlyList<PrinterModelDto> catalogModels, _) = await _catalogService.GetModelsAsync(null, ct);

            HashSet<string> catalogManufacturerNames = new HashSet<string>(catalogManufacturers.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);

            // #1779: the worker keys its ByHierarchy groups by each machine profile's `printer_model`.
            // High-flow variants declare their own distinct printer_model ("Prusa CORE One HF"), which is
            // never a catalog model Name — only a configured OrcaSlicer alias of one. Matching on base
            // catalog names alone skipped those groups entirely, dropping all 8 CORE One / CORE One L HF
            // machine profiles (plus their filament/process profiles) before they ever reached the
            // database that /api/slicer/profiles/extended reads from.
            HashSet<string> catalogModelNames = await OrcaSlicerCatalogModelNames.BuildAsync(_catalogService, catalogModels, ct);

            _logger.LogInformation("[SeedProfilesFromWorker] Filtering profiles for {CatalogManufacturerNamesCount} manufacturers and {CatalogModelsCount} models in catalog (using alias service for PrinterModel linking)", catalogManufacturerNames.Count, catalogModels.Count);

            int imported = 0;

            // Track hashes we've already processed this session to avoid duplicate insert attempts
            // (same profile may be compatible with multiple printer models)
            HashSet<string> processedMachineModelHashes = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> processedMachineHashes = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> processedFilamentHashes = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> processedProcessHashes = new(StringComparer.OrdinalIgnoreCase);

            // STEP 0: Import machine MODEL profiles (base templates from machine_model_list)
            // These are NOT directly selectable by users - they define base printer models like "Sovol SV08"
            if (allProfiles?.MachineModelProfiles != null && allProfiles.MachineModelProfiles.Count > 0)
            {
                _logger.LogInformation("[SeedProfilesFromWorker] Processing {MachineModelProfilesCount} manufacturers for machine MODEL profiles (base templates)", allProfiles.MachineModelProfiles.Count);
                foreach (KeyValuePair<string, IList<MachineModelProfileDto>> manufacturerEntry in allProfiles.MachineModelProfiles)
                {
                    string manufacturerName = manufacturerEntry.Key;
                    IList<MachineModelProfileDto> modelProfiles = manufacturerEntry.Value;

                    // Check if manufacturer is in catalog
                    if (!catalogManufacturerNames.Contains(manufacturerName))
                    {
                        _logger.LogDebug("[SeedProfilesFromWorker] Skipping machine model profiles for manufacturer '{ManufacturerName}' - not in catalog", manufacturerName);
                        continue;
                    }

                    foreach (MachineModelProfileDto modelProfile in modelProfiles)
                    {
                        try
                        {
                            if (existingMachineModelNames.Contains((modelProfile.Name ?? string.Empty).Trim()))
                            {
                                continue;
                            }

                            _ = existingMachineModelNames.Add((modelProfile.Name ?? string.Empty).Trim());

                            string profileJson = JsonSerializer.Serialize(modelProfile);
                            string profileHash = ComputeProfileHash(profileJson);

                            // Skip if we've already processed this hash
                            if (processedMachineModelHashes.Contains(profileHash))
                            {
                                continue;
                            }

                            processedMachineModelHashes.Add(profileHash);

                            MachineModelProfile? existing = await _machineModelProfileRepo.GetByHashAsync(profileHash, ct);
                            if (existing != null && existing.IsSystem)
                            {
                                continue;
                            }

                            // Look up the catalog PrinterModelId via alias service using the profile name
                            Guid? printerModelId = null;
                            if (!string.IsNullOrEmpty(modelProfile.Name))
                            {
                                printerModelId = await _aliasService.ResolveModelAliasAsync(modelProfile.Name, "OrcaSlicer");
                            }

                            MachineModelProfile systemProfile = new MachineModelProfile
                            {
                                Id = Guid.NewGuid(),
                                Name = modelProfile.Name ?? string.Empty,
                                Manufacturer = manufacturerName,
                                PrinterModelId = printerModelId,
                                IsSystem = true,
                                IsPublic = true,
                                Hash = profileHash,
                                RawJson = profileJson,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            };

                            await _machineModelProfileRepo.AddAsync(systemProfile, ct);
                            imported++;
                            _logger.LogDebug("[SeedProfilesFromWorker] Imported machine MODEL profile '{ModelProfileName}' for {ManufacturerName} (PrinterModelId: {PrinterModelId})", modelProfile.Name, manufacturerName, printerModelId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("[SeedProfilesFromWorker] Failed to import machine MODEL profile '{ModelProfileName}' for {ManufacturerName}: {ExMessage}", modelProfile.Name, manufacturerName, ex.Message);
                        }
                    }
                }
            }

            // Use the hierarchical structure from the worker: Manufacturer -> Model -> Profiles
            if (allProfiles?.ByHierarchy != null && allProfiles.ByHierarchy.Count > 0)
            {
                _logger.LogInformation("[SeedProfilesFromWorker] Processing {ByHierarchyCount} manufacturers from worker hierarchy", allProfiles.ByHierarchy.Count);
                foreach (KeyValuePair<string, ManufacturerProfilesDto> manufacturerEntry in allProfiles.ByHierarchy)
                {
                    string manufacturerName = manufacturerEntry.Key;
                    ManufacturerProfilesDto manufacturerProfiles = manufacturerEntry.Value;

                    // Check if manufacturer is in catalog
                    if (!catalogManufacturerNames.Contains(manufacturerName))
                    {
                        string similarNames = string.Join(", ", catalogManufacturerNames.Where(m => m.StartsWith(manufacturerName.Substring(0, Math.Min(3, manufacturerName.Length)), StringComparison.OrdinalIgnoreCase)));
                        _logger.LogDebug("[SeedProfilesFromWorker] Skipping manufacturer '{ManufacturerName}' - not in catalog (catalog has: {SimilarNames})", manufacturerName, similarNames);
                        continue;
                    }

                    int modelCount = manufacturerProfiles.Models?.Count ?? 0;
                    _logger.LogInformation("[SeedProfilesFromWorker] Processing manufacturer '{ManufacturerName}' with {ModelCount} models", manufacturerName, modelCount);

                    // Process each model for this manufacturer
                    if (manufacturerProfiles.Models == null || manufacturerProfiles.Models.Count == 0)
                    {
                        _logger.LogWarning("[SeedProfilesFromWorker] Manufacturer '{ManufacturerName}' has no models!", manufacturerName);
                        continue;
                    }

                    foreach (KeyValuePair<string, PrinterModelProfilesDto> modelEntry in manufacturerProfiles.Models)
                    {
                        _ = modelEntry.Key;
                        PrinterModelProfilesDto modelProfiles = modelEntry.Value;
                        string displayName = modelProfiles.Name;

                        // Check if this model is in the catalog
                        if (!catalogModelNames.Contains(displayName))
                        {
                            _logger.LogDebug("[SeedProfilesFromWorker] Skipping model '{DisplayName}' - not in catalog", displayName);
                            continue;
                        }

                        // STEP 1: Import machine profiles for this model FIRST (they're the foundation)
                        // Only import profiles with instantiation=true (user-selectable profiles)
                        if (modelProfiles.MachineProfiles != null && modelProfiles.MachineProfiles.Count > 0)
                        {
                            var instantiableMachineProfiles = modelProfiles.MachineProfiles.Where(p => p.Instantiation).ToList();
                            _logger.LogDebug("[SeedProfilesFromWorker] Importing {InstantiableMachineProfilesCount} instantiable machine profiles (out of {MachineProfilesCount} total) for {DisplayName}", instantiableMachineProfiles.Count, modelProfiles.MachineProfiles.Count, displayName);

                            foreach (MachineProfileDto? machineProfile in instantiableMachineProfiles)
                            {
                                try
                                {
                                    if (existingMachineNames.Contains((machineProfile.Name ?? string.Empty).Trim()))
                                    {
                                        continue;
                                    }

                                    _ = existingMachineNames.Add((machineProfile.Name ?? string.Empty).Trim());

                                    string profileJson = JsonSerializer.Serialize(machineProfile);
                                    string profileHash = ComputeProfileHash(profileJson);

                                    // Skip if we've already processed this hash in this session
                                    if (processedMachineHashes.Contains(profileHash))
                                    {
                                        continue;
                                    }

                                    processedMachineHashes.Add(profileHash);

                                    MachineProfile? existing = await _machineProfileRepo.GetByHashAsync(profileHash, ct);
                                    if (existing != null && existing.IsSystem)
                                    {
                                        continue;
                                    }

                                    // Look up the catalog PrinterModelId via alias service
                                    // Use the printer_model field from the profile (e.g., "Voron 2.4 350") for alias lookup
                                    Guid? printerModelId = null;
                                    string? printerModel = machineProfile.PrinterModel;
                                    if (!string.IsNullOrEmpty(printerModel))
                                    {
                                        printerModelId = await _aliasService.ResolveModelAliasAsync(printerModel, "OrcaSlicer");
                                    }

                                    MachineProfile systemProfile = new MachineProfile
                                    {
                                        Id = Guid.NewGuid(),
                                        Name = !string.IsNullOrEmpty(machineProfile.Name) ? machineProfile.Name : displayName,
                                        Manufacturer = manufacturerName,
                                        Description = $"OrcaSlicer machine profile for {displayName}" + (machineProfile.NozzleDiameter.HasValue ? $" ({machineProfile.NozzleDiameter}mm nozzle)" : string.Empty),
                                        SlicerType = SlicerType.OrcaSlicer,
                                        SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
                                        ProfileFormat = CalibrationContractConstants.ProfileFormat,
                                        SlicerVersion = worker.Version,
                                        PrinterModelId = printerModelId,
                                        IsSystem = true,
                                        IsPublic = true,
                                        IsDefault = false,
                                        Hash = profileHash,
                                        RawJson = profileJson,
                                        CreatedAt = DateTime.UtcNow,
                                        UpdatedAt = DateTime.UtcNow
                                    };

                                    await _machineProfileRepo.AddAsync(systemProfile, ct);
                                    imported++;
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning("[SeedProfilesFromWorker] Failed to import machine profile {MachineProfileName} for {DisplayName}: {ExMessage}", machineProfile.Name, displayName, ex.Message);
                                }
                            }
                        }

                        // STEP 2: Import filament profiles for this model (they're compatible with the model)
                        // Only import profiles with instantiation=true (user-selectable profiles)
                        if (modelProfiles.FilamentProfiles != null && modelProfiles.FilamentProfiles.Count > 0)
                        {
                            var instantiableFilamentProfiles = modelProfiles.FilamentProfiles.Where(p => p.Instantiation).ToList();
                            _logger.LogDebug("[SeedProfilesFromWorker] Importing {InstantiableFilamentProfilesCount} instantiable filament profiles (out of {FilamentProfilesCount} total) for {DisplayName}", instantiableFilamentProfiles.Count, modelProfiles.FilamentProfiles.Count, displayName);

                            foreach (FilamentProfileDto? filamentProfile in instantiableFilamentProfiles)
                            {
                                try
                                {
                                    string filamentName = string.IsNullOrEmpty(filamentProfile.Name) ? filamentProfile.Material : filamentProfile.Name;
                                    string filamentIdentity = FilamentIdentity(filamentName, filamentProfile.Material);
                                    if (existingFilamentNames.Contains(filamentIdentity))
                                    {
                                        continue;
                                    }

                                    _ = existingFilamentNames.Add(filamentIdentity);

                                    string profileJson = JsonSerializer.Serialize(filamentProfile);
                                    string profileHash = ComputeProfileHash(profileJson);

                                    // Skip if we've already processed this hash in this session
                                    if (processedFilamentHashes.Contains(profileHash))
                                    {
                                        continue;
                                    }

                                    processedFilamentHashes.Add(profileHash);

                                    FilamentProfile? existing = await _filamentProfileRepo.GetByHashAsync(profileHash, ct);
                                    if (existing != null && existing.IsSystem)
                                    {
                                        continue;
                                    }

                                    FilamentProfile systemProfile = new FilamentProfile
                                    {
                                        Id = Guid.NewGuid(),
                                        Name = string.IsNullOrEmpty(filamentProfile.Name) ? filamentProfile.Material : filamentProfile.Name,
                                        Material = filamentProfile.Material,
                                        Manufacturer = filamentProfile.Manufacturer ?? manufacturerName,
                                        Description = filamentProfile.Description ?? $"OrcaSlicer filament profile: {filamentProfile.Material}",
                                        SlicerType = SlicerType.OrcaSlicer,
                                        SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
                                        ProfileFormat = CalibrationContractConstants.ProfileFormat,
                                        SlicerVersion = worker.Version,
                                        NozzleTemperature = filamentProfile.NozzleTemperature,
                                        BedTemperature = filamentProfile.BedTemperature,
                                        PrintSpeed = filamentProfile.PrintSpeed,
                                        CompatiblePrinters = filamentProfile.CompatiblePrinters?.Count > 0
                                            ? string.Join(",", filamentProfile.CompatiblePrinters)
                                            : null,
                                        IsSystem = true,
                                        IsPublic = true,
                                        IsDefault = false,
                                        Hash = profileHash,
                                        RawJson = profileJson,
                                        CreatedAt = DateTime.UtcNow,
                                        UpdatedAt = DateTime.UtcNow
                                    };

                                    await _filamentProfileRepo.AddAsync(systemProfile, ct);
                                    imported++;
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning("[SeedProfilesFromWorker] Failed to import filament profile for {DisplayName}: {ExMessage}", displayName, ex.Message);
                                }
                            }
                        }

                        // STEP 3: Import process profiles for this model (they're compatible with the model)
                        // STEP 3: Import process/quality profiles for this model
                        // Only import profiles with instantiation=true (user-selectable profiles)
                        if (modelProfiles.ProcessProfiles != null && modelProfiles.ProcessProfiles.Count > 0)
                        {
                            var instantiableProcessProfiles = modelProfiles.ProcessProfiles.Where(p => p.Instantiation).ToList();
                            _logger.LogDebug("[SeedProfilesFromWorker] Importing {InstantiableProcessProfilesCount} instantiable process profiles (out of {ProcessProfilesCount} total) for {DisplayName}", instantiableProcessProfiles.Count, modelProfiles.ProcessProfiles.Count, displayName);

                            foreach (ProcessProfileDto? processProfile in instantiableProcessProfiles)
                            {
                                try
                                {
                                    string processName = string.IsNullOrEmpty(processProfile.Name)
                                        ? $"{processProfile.Quality} ({processProfile.LayerHeight}mm)"
                                        : processProfile.Name;

                                    // Resolve PrinterModelId before the identity guard: process profiles are
                                    // unique on (Name, SlicerType, PrinterModelId), so the same process name
                                    // legitimately exists under two printer models and a name-only guard
                                    // would silently drop one of them (#1779).
                                    Guid? printerModelId = await _aliasService.ResolveModelAliasAsync(displayName, "OrcaSlicer");
                                    string processIdentity = ProcessIdentity(processName, printerModelId);
                                    if (existingProcessNames.Contains(processIdentity))
                                    {
                                        continue;
                                    }

                                    _ = existingProcessNames.Add(processIdentity);

                                    string profileJson = JsonSerializer.Serialize(processProfile);
                                    string profileHash = ComputeProfileHash(profileJson);

                                    // Skip if we've already processed this hash in this session
                                    if (processedProcessHashes.Contains(profileHash))
                                    {
                                        continue;
                                    }

                                    processedProcessHashes.Add(profileHash);

                                    ProcessProfile? existing = await _profileRepo.GetByHashAsync(profileHash, ct);
                                    if (existing != null && existing.IsSystem)
                                    {
                                        continue;
                                    }

                                    ProcessProfile systemProfile = new ProcessProfile
                                    {
                                        Id = Guid.NewGuid(),
                                        Name = string.IsNullOrEmpty(processProfile.Name) ? $"{processProfile.Quality} ({processProfile.LayerHeight}mm)" : processProfile.Name,
                                        Description = processProfile.Description ?? $"OrcaSlicer process profile: {processProfile.Quality} quality at {processProfile.LayerHeight}mm layer height",
                                        SlicerType = SlicerType.OrcaSlicer,
                                        SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
                                        ProfileFormat = CalibrationContractConstants.ProfileFormat,
                                        SlicerVersion = worker.Version,
                                        PrinterModelId = printerModelId,
                                        Quality = Enum.TryParse(processProfile.Quality ?? "standard", true, out ProfileQuality q) ? q : ProfileQuality.Standard,
                                        LayerHeight = processProfile.LayerHeight,
                                        InfillPercentage = processProfile.InfillPercentage,
                                        PrintSpeed = processProfile.PrintSpeed,
                                        EnableSupports = processProfile.Supports,
                                        CompatiblePrinters = processProfile.CompatiblePrinters?.Count > 0
                                            ? string.Join(",", processProfile.CompatiblePrinters)
                                            : null,
                                        IsSystem = true,
                                        IsPublic = true,
                                        IsDefault = false,
                                        Hash = profileHash,
                                        RawJson = profileJson,
                                        CreatedAt = DateTime.UtcNow,
                                        UpdatedAt = DateTime.UtcNow
                                    };

                                    await _profileRepo.AddAsync(systemProfile, ct);
                                    imported++;
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning("[SeedProfilesFromWorker] Failed to import process profile for {DisplayName}: {ExMessage}", displayName, ex.Message);
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                // Fallback to flat structures if ByHierarchy is not available
                _logger.LogWarning("[SeedProfilesFromWorker] ByHierarchy not available, falling back to flat profile structures");
            }

            if (imported > 0)
            {
                _logger.LogInformation("[SeedProfilesFromWorker] Seeded {Imported} system OrcaSlicer profiles (machine, filament, and process) on worker registration (filtered to catalog manufacturers and models)", imported);
                await _repo.SaveChangesAsync(ct);
            }

            // Mark lock as completed
            await _settingsService.CompleteLockAsync(SEED_LOCK_KEY, ct);
            _logger.LogInformation("[SeedProfilesFromWorker] Released distributed lock, marking seed as completed");
        }
        catch (Exception ex)
        {
            // Clear lock on error so retry can happen
            try
            {
                await _settingsService.ClearLockAsync(SEED_LOCK_KEY, ct);
            }
            catch (Exception lockEx)
            {
                _logger.LogWarning("[SeedProfilesFromWorker] Failed to clear lock on error: {LockExMessage}", lockEx.Message);
            }

            _logger.LogError("[SeedProfilesFromWorker] Error: {ExceptionType}: {ExMessage}\n{ExStackTrace}", ex.GetType().Name, ex.Message, ex.StackTrace);

            // Don't throw - profile seeding is best-effort
        }
    }

    private static string ComputeProfileHash(string profileJson)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(profileJson));
        return Convert.ToHexString(hashedBytes);
    }

    /// <summary>
    /// Loads the identities of already-persisted OrcaSlicer profiles of one type into a
    /// case-insensitive set, used as the seed's stable idempotency key (#1779). Each identity must
    /// mirror its table's declared UNIQUE index, and all rows are loaded (not just system ones)
    /// because those indexes are global. Failures propagate rather than degrading to an empty set,
    /// which would be indistinguishable from "nothing imported yet" and drive into a collision.
    /// </summary>
    private static async Task<HashSet<string>> LoadExistingProfileIdentitiesAsync(
        Func<CancellationToken, Task<IEnumerable<string>>> load,
        CancellationToken ct)
    {
        IEnumerable<string> identities = await load(ct);
        return new HashSet<string>(
            identities.Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Builds the filament identity matching its UNIQUE (Name, Material, SlicerType) index.</summary>
    private static string FilamentIdentity(string? name, string? material) =>
        $"{(name ?? string.Empty).Trim()}\u001F{(material ?? string.Empty).Trim()}";

    /// <summary>Builds the process identity matching its UNIQUE (Name, SlicerType, PrinterModelId) index.</summary>
    private static string ProcessIdentity(string? name, Guid? printerModelId) =>
        $"{(name ?? string.Empty).Trim()}\u001F{printerModelId?.ToString() ?? string.Empty}";
}
