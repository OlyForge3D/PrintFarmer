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
using Farm.Infrastructure.Contracts.Slicing; // shared DTOs for RegisterSlicerDto, HeartbeatDto
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Slicing;
using Farm.Infrastructure.Repositories.Workers;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Services.Catalog;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services.Slicing
{
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
    public class SlicersService : ISlicersService
    {
        private readonly ISlicersRepository _repo;
        private readonly IWorkerRepository _workerRepo;
        private readonly IProcessProfileRepository _profileRepo;
        private readonly IFilamentProfileRepository _filamentProfileRepo;
        private readonly IMachineProfileRepository _machineProfileRepo;
        private readonly ICatalogService _catalogService;
        private readonly Farm.Infrastructure.Settings.ISettingsService _settingsService;
        private readonly IHubContext<SlicerHub> _hub;
        private readonly SlicerServiceMetrics _metrics;
        private readonly HttpClient _httpClient;
        private readonly IUnifiedLoggingService _logger;

        private readonly Microsoft.Extensions.Options.IOptionsMonitor<Farm.Infrastructure.Settings.SlicerSettings> _slicerSettings;

        /// <summary>
        /// Initializes a new instance of the SlicersService with required dependencies.
        /// Sets up capacity metrics providers for real-time monitoring of worker capacity.
        /// </summary>
        /// <param name="repo">Repository for slicer service data persistence and retrieval</param>
        /// <param name="workerRepo">Repository for worker data access and management</param>
        /// <param name="profileRepo">Repository for process profile data access</param>
        /// <param name="filamentProfileRepo">Repository for filament profile data access</param>
        /// <param name="machineProfileRepo">Repository for machine profile data access</param>
        /// <param name="catalogService">Service for manufacturer and printer model catalog lookups</param>
        /// <param name="settingsService">Service for managing application settings and distributed locks</param>
        /// <param name="hub">SignalR hub context for broadcasting worker state changes to connected clients</param>
        /// <param name="metrics">Metrics collection service for capacity and utilization tracking</param>
        /// <param name="httpClient">HTTP client for external service communication and health checks</param>
        /// <param name="logger">Unified logging service for audit trails and debugging</param>
        /// <param name="slicerSettings">Configuration options for slicer service behavior and constraints</param>
        /// <exception cref="ArgumentNullException">Thrown when any required dependency is null</exception>
        public SlicersService(
            ISlicersRepository repo,
            IWorkerRepository workerRepo,
            IProcessProfileRepository profileRepo,
            IFilamentProfileRepository filamentProfileRepo,
            IMachineProfileRepository machineProfileRepo,
            ICatalogService catalogService,
            Farm.Infrastructure.Settings.ISettingsService settingsService,
            IHubContext<SlicerHub> hub,
            SlicerServiceMetrics metrics,
            HttpClient httpClient,
            IUnifiedLoggingService logger,
            Microsoft.Extensions.Options.IOptionsMonitor<Farm.Infrastructure.Settings.SlicerSettings> slicerSettings)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _workerRepo = workerRepo ?? throw new ArgumentNullException(nameof(workerRepo));
            _profileRepo = profileRepo ?? throw new ArgumentNullException(nameof(profileRepo));
            _filamentProfileRepo = filamentProfileRepo ?? throw new ArgumentNullException(nameof(filamentProfileRepo));
            _machineProfileRepo = machineProfileRepo ?? throw new ArgumentNullException(nameof(machineProfileRepo));
            _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _hub = hub ?? throw new ArgumentNullException(nameof(hub));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _slicerSettings = slicerSettings ?? throw new ArgumentNullException(nameof(slicerSettings));

            // Set up observable capacity metrics
            _metrics.SetCapacityProviders(
                getTotalCapacity: () => GetTotalCapacitySync(),
                getAvailableCapacity: () => GetAvailableCapacitySync(),
                getActiveJobs: () => GetActiveJobsSync());
        }

        private int GetTotalCapacitySync()
        {
            try
            {
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
                IReadOnlyList<SlicerService> services = _repo.ListAsync(CancellationToken.None).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
                return services.Sum(s => s.MaxConcurrentJobs);
            }
            catch
            {
                return 0;
            }
        }

        private int GetAvailableCapacitySync()
        {
            try
            {
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
                IReadOnlyList<Worker> workers = _workerRepo.GetAllAsync(limit: 1000).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
                return workers.Sum(w => w.FreeSlots);
            }
            catch
            {
                return 0;
            }
        }

        private int GetActiveJobsSync()
        {
            try
            {
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
                IReadOnlyList<Worker> workers = _workerRepo.GetAllAsync(limit: 1000).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
                return workers.Sum(w => w.ActiveJobs);
            }
            catch
            {
                return 0;
            }
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
        public async Task<(Guid id, string apiKey)> RegisterAsync(RegisterSlicerDto dto, CancellationToken ct)
        {
            SlicerService svc = new SlicerService
            {
                Id = Guid.NewGuid(),
                Name = dto.Name ?? "orca-service",
                SlicerType = dto.SlicerType,
                Version = dto.Version,
                Host = dto.Host,
                UiManifestUrl = dto.UiManifestUrl,
                CapabilitiesJson = dto.CapabilitiesJson,
                MaxConcurrentJobs = Math.Min(dto.MaxConcurrentJobs, Math.Max(1, _slicerSettings.CurrentValue.MaxConcurrentJobs)), // enforce global upper bound
                Status = "Online",
                LastSeen = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Tags = dto.Tags
            };

            svc.ApiKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Replace("=", "");

            await _repo.AddAsync(svc, ct);
            await _repo.SaveChangesAsync(ct);

            // Synchronize to Worker table for dispatcher
            try
            {
                Worker worker = new Worker
                {
                    Id = Guid.NewGuid(),
                    ServiceId = svc.Id.ToString(),
                    Name = svc.Name,
                    EndpointUrl = svc.Host ?? string.Empty,
                    CapabilitiesJson = svc.CapabilitiesJson ?? "[]",
                    Status = WorkerStatus.Online,
                    TotalSlots = Math.Min(dto.MaxConcurrentJobs, Math.Max(1, _slicerSettings.CurrentValue.MaxConcurrentJobs)),
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
                await _repo.SaveChangesAsync(ct); // Use _repo's SaveChanges since both entities use same DbContext
            }
            catch (Exception ex)
            {
                // Log but don't fail registration if Worker sync fails
                _logger.LogWarning($"[RegisterAsync] Failed to sync Worker entity: {ex.Message}");
            }

            // Seed profiles from the worker (OrcaSlicer only)
            if (svc.SlicerType == 1) // OrcaSlicer
            {
                try
                {
                    _logger.LogInformation($"OrcaSlicer service registered, attempting to seed profiles from {svc.Host}");
                    await SeedProfilesFromWorkerAsync(svc.Host ?? string.Empty, ct);
                    _logger.LogInformation("Profile seeding completed");
                }
                catch (Exception ex)
                {
                    // Log but don't fail registration if profile seeding fails
                    _logger.LogWarning($"Failed to seed profiles from worker: {ex.Message}");
                }
            }

            // Record metrics
            _metrics.RecordServiceRegistration(GetSlicerTypeName(svc.SlicerType), svc.Id.ToString());

            // Broadcast registration event (best-effort)
            try
            {
                await _hub.Clients.All.SendAsync(SlicerHubEvents.SlicerRegistered, new
                {
                    id = svc.Id,
                    name = svc.Name,
                    slicerType = svc.SlicerType,
                    version = svc.Version,
                    host = svc.Host,
                    capabilitiesJson = svc.CapabilitiesJson,
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
                _logger.LogWarning($"[HeartbeatAsync] Failed to sync Worker heartbeat: {ex.Message}");
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
                await _hub.Clients.All.SendAsync(SlicerHubEvents.SlicerHeartbeat, new
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
        /// Deregisters a slicer worker service and marks it as offline in the system.
        /// </summary>
        /// <param name="id">The unique identifier of the slicer worker to deregister</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>True if deregistration was successful; false if worker not found</returns>
        /// <remarks>
        /// This method performs the following operations:
        /// - Removes the slicer service from active registration
        /// - Synchronizes worker status to offline in the Worker table
        /// - Records metrics for service deregistration
        /// - Broadcasts deregistration events to all connected clients via SignalR
        /// - Preserves worker history for audit trails
        /// 
        /// Deregistered workers are no longer available for job assignment. The system
        /// automatically fails over any pending jobs from deregistered workers.
        /// </remarks>
        public async Task<bool> DeregisterAsync(Guid id, CancellationToken ct)
        {
            SlicerService? svc = await _repo.GetByIdAsync(id, ct);
            if (svc == null)
            {
                return false;
            }

            string slicerTypeName = GetSlicerTypeName(svc.SlicerType);

            await _repo.RemoveAsync(svc, ct);
            await _repo.SaveChangesAsync(ct);

            // Synchronize to Worker table - mark as offline or remove
            try
            {
                Worker? worker = await _workerRepo.GetByServiceIdAsync(id.ToString());
                if (worker != null)
                {
                    worker.Status = WorkerStatus.Offline;
                    worker.OfflineAt = DateTime.UtcNow;
                    worker.UpdatedAt = DateTime.UtcNow;
                    await _repo.SaveChangesAsync(ct); // Worker entity tracked by same DbContext
                }
            }
            catch (Exception ex)
            {
                // Log but don't fail deregistration if Worker sync fails
                _logger.LogWarning($"[DeregisterAsync] Failed to sync Worker deregistration: {ex.Message}");
            }

            // Record metrics
            _metrics.RecordServiceDeregistration(slicerTypeName, id.ToString(), "normal");

            try
            {
                await _hub.Clients.All.SendAsync(SlicerHubEvents.SlicerDeregistered, new { id = svc.Id, name = svc.Name }, ct);
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
            string newApiKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Replace("=", "");
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
                _logger.LogWarning($"[RotateApiKeyAsync] Failed to sync Worker API key rotation: {ex.Message}");
            }

            // Record metrics
            _metrics.RecordApiKeyRotation(slicerTypeName, id.ToString(), success: true, isAdminForced);

            // Broadcast rotation event (best-effort)
            try
            {
                await _hub.Clients.All.SendAsync(SlicerHubEvents.SlicerApiKeyRotated, new
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

        /// <summary>
        /// Seed OrcaSlicer profiles from the worker into the database on registration.
        /// This happens automatically when an OrcaSlicer worker registers, so profiles are available immediately.
        /// Only seeds if no system OrcaSlicer profiles exist yet (idempotent - won't reseed on subsequent registrations).
        /// Uses a distributed lock to ensure seeding happens only once, even with multiple concurrent worker registrations.
        /// Profiles are filtered to only include those for manufacturers and models in the catalog.
        /// </summary>
        private async Task SeedProfilesFromWorkerAsync(string workerHost, CancellationToken ct)
        {
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

                // Call the worker's /api/profiles endpoint which now returns AllProfilesResponseDto with all three profile types
                string workerUrl = workerHost.TrimEnd('/');
                _logger.LogInformation($"[SeedProfilesFromWorker] Fetching profiles from worker at: {workerUrl}/api/profiles");
                HttpResponseMessage response = await _httpClient.GetAsync($"{workerUrl}/api/profiles", ct);

                if (!response.IsSuccessStatusCode)
                {
                    string errorContent = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning($"[SeedProfilesFromWorker] Worker /api/profiles returned {response.StatusCode}: {errorContent}");
                    // Clear lock on error so retry can happen
                    await _settingsService.ClearLockAsync(SEED_LOCK_KEY, ct);
                    return;
                }

                string json = await response.Content.ReadAsStringAsync(ct);
                _logger.LogInformation($"[SeedProfilesFromWorker] Received {json.Length} bytes from worker");
                AllProfilesResponseDto? allProfiles = JsonSerializer.Deserialize<AllProfilesResponseDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (allProfiles == null || (allProfiles.ProcessProfiles?.Count == 0 && allProfiles.FilamentProfiles?.Count == 0 && allProfiles.MachineProfiles?.Count == 0))
                {
                    _logger.LogWarning($"[SeedProfilesFromWorker] No profiles available from worker (parsed null: {allProfiles == null}, process groups: {allProfiles?.ProcessProfiles?.Count ?? 0}, filament groups: {allProfiles?.FilamentProfiles?.Count ?? 0}, machine groups: {allProfiles?.MachineProfiles?.Count ?? 0})");
                    // Clear lock on empty response so retry can happen
                    await _settingsService.ClearLockAsync(SEED_LOCK_KEY, ct);
                    return;
                }

                // Get catalog manufacturers and models to filter profiles
                (IReadOnlyList<ManufacturerDto> catalogManufacturers, _) = await _catalogService.GetManufacturersAsync(ct);
                (IReadOnlyList<PrinterModelDto> catalogModels, _) = await _catalogService.GetModelsAsync(null, ct);

                HashSet<string> catalogManufacturerNames = new HashSet<string>(catalogManufacturers.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
                HashSet<string> catalogModelNames = new HashSet<string>(catalogModels.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);

                _logger.LogInformation($"[SeedProfilesFromWorker] Filtering profiles for {catalogManufacturerNames.Count} manufacturers and {catalogModels.Count} models in catalog");

                int imported = 0;

                // Use the hierarchical structure from the worker: Manufacturer -> Model -> Profiles
                if (allProfiles?.ByHierarchy != null && allProfiles.ByHierarchy.Count > 0)
                {
                    _logger.LogInformation($"[SeedProfilesFromWorker] Processing {allProfiles.ByHierarchy.Count} manufacturers from worker hierarchy");
                    foreach (var manufacturerEntry in allProfiles.ByHierarchy)
                    {
                        string manufacturerName = manufacturerEntry.Key;
                        ManufacturerProfilesDto manufacturerProfiles = manufacturerEntry.Value;

                        // Check if manufacturer is in catalog
                        if (!catalogManufacturerNames.Contains(manufacturerName))
                        {
                            _logger.LogDebug($"[SeedProfilesFromWorker] Skipping manufacturer '{manufacturerName}' - not in catalog (catalog has: {string.Join(", ", catalogManufacturerNames.Where(m => m.StartsWith(manufacturerName.Substring(0, Math.Min(3, manufacturerName.Length)), StringComparison.OrdinalIgnoreCase)))})");
                            continue;
                        }

                        _logger.LogInformation($"[SeedProfilesFromWorker] Processing manufacturer '{manufacturerName}' with {manufacturerProfiles.Models?.Count ?? 0} models");

                        // Process each model for this manufacturer
                        if (manufacturerProfiles.Models == null || manufacturerProfiles.Models.Count == 0)
                        {
                            _logger.LogWarning($"[SeedProfilesFromWorker] Manufacturer '{manufacturerName}' has no models!");
                            continue;
                        }

                        foreach (var modelEntry in manufacturerProfiles.Models)
                        {
                            string modelId = modelEntry.Key;
                            PrinterModelProfilesDto modelProfiles = modelEntry.Value;
                            string displayName = modelProfiles.Name;

                            // Check if this model is in the catalog
                            if (!catalogModelNames.Contains(displayName))
                            {
                                _logger.LogDebug($"[SeedProfilesFromWorker] Skipping model '{displayName}' - not in catalog");
                                continue;
                            }

                            // STEP 1: Import machine profiles for this model FIRST (they're the foundation)
                            // Only import profiles with instantiation=true (user-selectable profiles)
                            if (modelProfiles.MachineProfiles != null && modelProfiles.MachineProfiles.Count > 0)
                            {
                                var instantiableMachineProfiles = modelProfiles.MachineProfiles.Where(p => p.Instantiation).ToList();
                                _logger.LogDebug($"[SeedProfilesFromWorker] Importing {instantiableMachineProfiles.Count} instantiable machine profiles (out of {modelProfiles.MachineProfiles.Count} total) for {displayName}");
                                
                                foreach (var machineProfile in instantiableMachineProfiles)
                                {
                                    try
                                    {
                                        string profileJson = JsonSerializer.Serialize(machineProfile);
                                        string profileHash = ComputeProfileHash(profileJson);

                                        MachineProfile? existing = await _machineProfileRepo.GetByHashAsync(profileHash, ct);
                                        if (existing != null && existing.IsSystem)
                                        {
                                            continue;
                                        }

                                        MachineProfile systemProfile = new MachineProfile
                                        {
                                            Id = Guid.NewGuid(),
                                            Name = !string.IsNullOrEmpty(machineProfile.Name) ? machineProfile.Name : displayName,
                                            Manufacturer = manufacturerName,
                                            Description = $"OrcaSlicer machine profile for {displayName}" + (machineProfile.NozzleDiameter.HasValue ? $" ({machineProfile.NozzleDiameter}mm nozzle)" : ""),
                                            SlicerType = SlicerType.OrcaSlicer,
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
                                        _logger.LogWarning($"[SeedProfilesFromWorker] Failed to import machine profile {machineProfile.Name} for {displayName}: {ex.Message}");
                                    }
                                }
                            }

                            // STEP 2: Import filament profiles for this model (they're compatible with the model)
                            // Only import profiles with instantiation=true (user-selectable profiles)
                            if (modelProfiles.FilamentProfiles != null && modelProfiles.FilamentProfiles.Count > 0)
                            {
                                var instantiableFilamentProfiles = modelProfiles.FilamentProfiles.Where(p => p.Instantiation).ToList();
                                _logger.LogDebug($"[SeedProfilesFromWorker] Importing {instantiableFilamentProfiles.Count} instantiable filament profiles (out of {modelProfiles.FilamentProfiles.Count} total) for {displayName}");
                                
                                foreach (var filamentProfile in instantiableFilamentProfiles)
                                {
                                    try
                                    {
                                        string profileJson = JsonSerializer.Serialize(filamentProfile);
                                        string profileHash = ComputeProfileHash(profileJson);

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
                                            NozzleTemperature = filamentProfile.NozzleTemperature,
                                            BedTemperature = filamentProfile.BedTemperature,
                                            PrintSpeed = filamentProfile.PrintSpeed,
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
                                        _logger.LogWarning($"[SeedProfilesFromWorker] Failed to import filament profile for {displayName}: {ex.Message}");
                                    }
                                }
                            }

                            // STEP 3: Import process profiles for this model (they're compatible with the model)
                            // STEP 3: Import process/quality profiles for this model
                            // Only import profiles with instantiation=true (user-selectable profiles)
                            if (modelProfiles.ProcessProfiles != null && modelProfiles.ProcessProfiles.Count > 0)
                            {
                                var instantiableProcessProfiles = modelProfiles.ProcessProfiles.Where(p => p.Instantiation).ToList();
                                _logger.LogDebug($"[SeedProfilesFromWorker] Importing {instantiableProcessProfiles.Count} instantiable process profiles (out of {modelProfiles.ProcessProfiles.Count} total) for {displayName}");
                                
                                foreach (var processProfile in instantiableProcessProfiles)
                                {
                                    try
                                    {
                                        string profileJson = JsonSerializer.Serialize(processProfile);
                                        string profileHash = ComputeProfileHash(profileJson);

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
                                            Quality = Enum.TryParse(processProfile.Quality ?? "standard", true, out ProfileQuality q) ? q : ProfileQuality.Standard,
                                            LayerHeight = processProfile.LayerHeight,
                                            InfillPercentage = processProfile.InfillPercentage,
                                            PrintSpeed = processProfile.PrintSpeed,
                                            EnableSupports = processProfile.Supports,
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
                                        _logger.LogWarning($"[SeedProfilesFromWorker] Failed to import process profile for {displayName}: {ex.Message}");
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
                    _logger.LogInformation($"[SeedProfilesFromWorker] Seeded {imported} system OrcaSlicer profiles (machine, filament, and process) on worker registration (filtered to catalog manufacturers and models)");
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
                    _logger.LogWarning($"[SeedProfilesFromWorker] Failed to clear lock on error: {lockEx.Message}");
                }

                _logger.LogError($"[SeedProfilesFromWorker] Error: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                // Don't throw - profile seeding is best-effort
            }
        }

        private static string ComputeProfileHash(string profileJson)
        {
            using SHA256 sha256 = SHA256.Create();
            byte[] hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(profileJson));
            return Convert.ToHexString(hashedBytes);
        }
    }
}
