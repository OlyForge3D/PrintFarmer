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
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Slicing;
using Farm.Infrastructure.Repositories.Workers;
using Farm.Web.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Services.Slicing
{
    public class SlicersService : ISlicersService
    {
        private readonly ISlicersRepository _repo;
        private readonly IWorkerRepository _workerRepo;
        private readonly IProcessProfileRepository _profileRepo;
        private readonly IHubContext<SlicerHub> _hub;
        private readonly SlicerServiceMetrics _metrics;
        private readonly HttpClient _httpClient;

        private readonly Microsoft.Extensions.Options.IOptionsMonitor<Farm.Infrastructure.Settings.SlicerSettings> _slicerSettings;

        public SlicersService(
            ISlicersRepository repo,
            IWorkerRepository workerRepo,
            IProcessProfileRepository profileRepo,
            IHubContext<SlicerHub> hub,
            SlicerServiceMetrics metrics,
            HttpClient httpClient,
            Microsoft.Extensions.Options.IOptionsMonitor<Farm.Infrastructure.Settings.SlicerSettings> slicerSettings)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _workerRepo = workerRepo ?? throw new ArgumentNullException(nameof(workerRepo));
            _profileRepo = profileRepo ?? throw new ArgumentNullException(nameof(profileRepo));
            _hub = hub ?? throw new ArgumentNullException(nameof(hub));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
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

        public async Task<IReadOnlyList<SlicerService>> ListAsync(CancellationToken ct)
        {
            return await _repo.ListAsync(ct);
        }

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
                // In production, you'd want proper logging here
                Debug.WriteLine($"Failed to sync Worker entity: {ex.Message}");
            }

            // Seed profiles from the worker (OrcaSlicer only)
            if (svc.SlicerType == 1) // OrcaSlicer
            {
                try
                {
                    await SeedProfilesFromWorkerAsync(svc.Host ?? string.Empty, ct);
                }
                catch (Exception ex)
                {
                    // Log but don't fail registration if profile seeding fails
                    Debug.WriteLine($"Failed to seed profiles from worker: {ex.Message}");
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

        public async Task<SlicerService?> GetAsync(Guid id, CancellationToken ct)
        {
            return await _repo.GetByIdAsync(id, ct);
        }

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
                Debug.WriteLine($"Failed to sync Worker heartbeat: {ex.Message}");
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
                Debug.WriteLine($"Failed to sync Worker deregistration: {ex.Message}");
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
                Debug.WriteLine($"Failed to sync Worker API key rotation: {ex.Message}");
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
        /// </summary>
        private async Task SeedProfilesFromWorkerAsync(string workerHost, CancellationToken ct)
        {
            try
            {
                // Early exit: Only seed if no system profiles exist for OrcaSlicer
                // This prevents re-seeding on every worker registration
                IReadOnlyList<ProcessProfile> existingSystemProfiles = await _profileRepo.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: true, userId: null, ct);
                if (existingSystemProfiles.Any(p => p.IsSystem))
                {
                    Debug.WriteLine("System OrcaSlicer profiles already exist, skipping seed");
                    return;
                }

                // Call the worker's /profiles endpoint which now returns AllProfilesResponseDto with all three profile types
                string workerUrl = workerHost.TrimEnd('/');
                HttpResponseMessage response = await _httpClient.GetAsync($"{workerUrl}/profiles", ct);

                if (!response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"Worker /profiles returned {response.StatusCode}");
                    return;
                }

                string json = await response.Content.ReadAsStringAsync(ct);
                AllProfilesResponseDto? allProfiles = JsonSerializer.Deserialize<AllProfilesResponseDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (allProfiles == null || (allProfiles.ProcessProfiles?.Count == 0 && allProfiles.FilamentProfiles?.Count == 0 && allProfiles.MachineProfiles?.Count == 0))
                {
                    Debug.WriteLine("No profiles available from worker");
                    return;
                }

                int imported = 0;

                // Flatten profiles from the grouped dictionaries
                List<ProcessProfileDto> flattenedProcessProfiles = allProfiles.ProcessProfiles?.SelectMany(kvp => kvp.Value).ToList() ?? new List<ProcessProfileDto>();
                List<FilamentProfileDto> flattenedFilamentProfiles = allProfiles.FilamentProfiles?.SelectMany(kvp => kvp.Value).ToList() ?? new List<FilamentProfileDto>();

                // Import process profiles from worker
                if (flattenedProcessProfiles.Count > 0)
                {
                    foreach (ProcessProfileDto? profile in flattenedProcessProfiles)
                    {
                        try
                        {
                            string profileJson = JsonSerializer.Serialize(profile);
                            string profileHash = ComputeProfileHash(profileJson);

                            ProcessProfile? existing = await _profileRepo.GetByHashAsync(profileHash, ct);
                            if (existing != null && existing.IsSystem)
                            {
                                continue;
                            }

                            ProcessProfile systemProfile = new ProcessProfile
                            {
                                Id = Guid.NewGuid(),
                                Name = string.IsNullOrEmpty(profile.Name) ? $"{profile.Quality} ({profile.LayerHeight}mm)" : profile.Name,
                                Description = $"OrcaSlicer process profile: {profile.Quality} quality at {profile.LayerHeight}mm layer height",
                                SlicerType = SlicerType.OrcaSlicer,
                                Quality = Enum.TryParse(profile.Quality ?? "standard", true, out ProfileQuality q) ? q : ProfileQuality.Standard,
                                LayerHeight = profile.LayerHeight,
                                InfillPercentage = profile.InfillPercentage,
                                PrintSpeed = profile.PrintSpeed,
                                EnableSupports = profile.Supports,
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
                            Debug.WriteLine($"Failed to import process profile: {ex.Message}");
                        }
                    }
                }

                if (imported > 0)
                {
                    Debug.WriteLine($"Seeded {imported} system OrcaSlicer profiles on worker registration");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error seeding profiles: {ex.Message}");
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
