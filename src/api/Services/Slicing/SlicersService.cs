using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Repositories.Slicing;
using Farm.Web.Api.Repositories.Workers;
using Farm.Web.Shared.Contracts.Slicing; // shared DTOs for RegisterSlicerDto, HeartbeatDto
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Services.Slicing
{
    public class SlicersService : ISlicersService
    {
        private readonly ISlicersRepository _repo;
        private readonly IWorkerRepository _workerRepo;
        private readonly IHubContext<SlicerHub> _hub;
        private readonly SlicerServiceMetrics _metrics;

        public SlicersService(
            ISlicersRepository repo,
            IWorkerRepository workerRepo,
            IHubContext<SlicerHub> hub,
            SlicerServiceMetrics metrics)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _workerRepo = workerRepo ?? throw new ArgumentNullException(nameof(workerRepo));
            _hub = hub ?? throw new ArgumentNullException(nameof(hub));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));

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
                var services = _repo.ListAsync(CancellationToken.None).GetAwaiter().GetResult();
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
                var workers = _workerRepo.GetAllAsync(limit: 1000).GetAwaiter().GetResult();
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
                var workers = _workerRepo.GetAllAsync(limit: 1000).GetAwaiter().GetResult();
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
            var svc = new SlicerService
            {
                Id = Guid.NewGuid(),
                Name = dto.Name ?? "orca-service",
                SlicerType = dto.SlicerType,
                Version = dto.Version,
                Host = dto.Host,
                UiManifestUrl = dto.UiManifestUrl,
                CapabilitiesJson = dto.CapabilitiesJson,
                MaxConcurrentJobs = dto.MaxConcurrentJobs,
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
                var worker = new Worker
                {
                    Id = Guid.NewGuid(),
                    ServiceId = svc.Id.ToString(),
                    Name = svc.Name,
                    EndpointUrl = svc.Host ?? string.Empty,
                    CapabilitiesJson = svc.CapabilitiesJson ?? "[]",
                    Status = WorkerStatus.Online,
                    FreeSlots = dto.MaxConcurrentJobs,
                    TotalSlots = dto.MaxConcurrentJobs,
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
                System.Diagnostics.Debug.WriteLine($"Failed to sync Worker entity: {ex.Message}");
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
                    maxConcurrentJobs = svc.MaxConcurrentJobs,
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
            var startTime = DateTime.UtcNow;
            var svc = await _repo.GetByIdAsync(id, ct);
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
                var worker = await _workerRepo.GetByServiceIdAsync(id.ToString());
                if (worker != null)
                {
                    // Update existing worker
                    worker.Status = MapStatus(dto.Status ?? svc.Status ?? "Online");
                    worker.FreeSlots = dto.FreeSlots ?? worker.FreeSlots;
                    worker.LastHeartbeat = DateTime.UtcNow;
                    worker.UpdatedAt = DateTime.UtcNow;

                    // Calculate active jobs from free slots and total slots
                    if (dto.FreeSlots.HasValue && worker.TotalSlots > 0)
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
                System.Diagnostics.Debug.WriteLine($"Failed to sync Worker heartbeat: {ex.Message}");
            }

            // Record heartbeat metrics
            var latencyMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
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
            var svc = await _repo.GetByIdAsync(id, ct);
            if (svc == null)
            {
                return false;
            }

            var slicerTypeName = GetSlicerTypeName(svc.SlicerType);

            await _repo.RemoveAsync(svc, ct);
            await _repo.SaveChangesAsync(ct);

            // Synchronize to Worker table - mark as offline or remove
            try
            {
                var worker = await _workerRepo.GetByServiceIdAsync(id.ToString());
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
                System.Diagnostics.Debug.WriteLine($"Failed to sync Worker deregistration: {ex.Message}");
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
            var svc = await _repo.GetByIdAsync(id, ct);
            if (svc == null)
            {
                return null;
            }

            var slicerTypeName = GetSlicerTypeName(svc.SlicerType);

            // Generate new API key
            var newApiKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Replace("=", "");
            svc.ApiKey = newApiKey;
            svc.ApiKeyRotatedAt = DateTime.UtcNow;
            svc.UpdatedAt = DateTime.UtcNow;

            await _repo.SaveChangesAsync(ct);

            // Synchronize to Worker table
            try
            {
                var worker = await _workerRepo.GetByServiceIdAsync(id.ToString());
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
                System.Diagnostics.Debug.WriteLine($"Failed to sync Worker API key rotation: {ex.Message}");
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
    }
}
