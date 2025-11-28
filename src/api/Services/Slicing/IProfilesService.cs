using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;

namespace Farm.Web.Api.Services.Slicing
{
    public interface IProfilesService
    {
        Task<ProcessProfileResponseDto> CreateProfileAsync(CreateProcessProfileDto req, CancellationToken ct);
        Task<ProcessProfileResponseDto?> GetProfileAsync(Guid id, CancellationToken ct);
        Task<IReadOnlyList<SlicerProfileDto>> GetProfilesAsync(CancellationToken ct);
        Task DeleteProfileAsync(Guid id, CancellationToken ct);
    }
}
