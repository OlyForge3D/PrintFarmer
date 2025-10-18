using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Repositories.Slicing;
using Farm.Web.Shared.Contracts.Slicing; // shared DTOs for RegisterSlicerDto, HeartbeatDto
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Services.Slicing
{
    public class SlicersService : ISlicersService
    {
        private readonly ISlicersRepository _repo;
        private readonly IHubContext<Services.SlicerServices.SlicerProgressHub> _hub;

        public SlicersService(ISlicersRepository repo, IHubContext<Services.SlicerServices.SlicerProgressHub> hub)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _hub = hub ?? throw new ArgumentNullException(nameof(hub));
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

            // Broadcast registration event (best-effort)
            try
            {
                await _hub.Clients.All.SendAsync("SlicerRegistered", new
                {
                    id = svc.Id,
                    name = svc.Name,
                    version = svc.Version,
                    host = svc.Host,
                    maxConcurrentJobs = svc.MaxConcurrentJobs,
                    status = svc.Status
                }, ct);
            }
            catch
            {
                // ignore broadcasting failures
            }

            return (svc.Id, svc.ApiKey ?? string.Empty);
        }

        public async Task<SlicerService?> GetAsync(Guid id, CancellationToken ct)
        {
            return await _repo.GetByIdAsync(id, ct);
        }

        public async Task<bool> HeartbeatAsync(Guid id, HeartbeatDto dto, CancellationToken ct)
        {
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

            try
            {
                await _hub.Clients.All.SendAsync("SlicerHeartbeat", new
                {
                    id = svc.Id,
                    status = svc.Status,
                    freeSlots = dto.FreeSlots
                }, ct);
            }
            catch
            {
                // ignore
            }

            return true;
        }

        public async Task<bool> DeregisterAsync(Guid id, CancellationToken ct)
        {
            var svc = await _repo.GetByIdAsync(id, ct);
            if (svc == null)
            {
                return false;
            }

            await _repo.RemoveAsync(svc, ct);
            await _repo.SaveChangesAsync(ct);

            try
            {
                await _hub.Clients.All.SendAsync("SlicerDeregistered", new { id = svc.Id }, ct);
            }
            catch
            {
                // ignore
            }

            return true;
        }
    }
}
