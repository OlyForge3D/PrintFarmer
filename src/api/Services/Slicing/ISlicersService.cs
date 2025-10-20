using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Web.Shared.Contracts.Slicing;

namespace Farm.Web.Api.Services.Slicing
{
    public interface ISlicersService
    {
        Task<IReadOnlyList<SlicerService>> ListAsync(CancellationToken ct);
        Task<(Guid id, string apiKey)> RegisterAsync(RegisterSlicerDto dto, CancellationToken ct);
        Task<SlicerService?> GetAsync(Guid id, CancellationToken ct);
        Task<bool> HeartbeatAsync(Guid id, HeartbeatDto dto, CancellationToken ct);
        Task<bool> DeregisterAsync(Guid id, CancellationToken ct);
        Task<string?> RotateApiKeyAsync(Guid id, CancellationToken ct, bool isAdminForced = false);
    }
}
