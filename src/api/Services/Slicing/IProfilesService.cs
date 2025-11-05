using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.Slicing
{
    public interface IProfilesService
    {
        Task<SlicerProfileResponseDto> CreateProfileAsync(CreateSlicerProfileDto req, CancellationToken ct);
        Task<SlicerProfileResponseDto?> GetProfileAsync(Guid id, CancellationToken ct);
        Task<IReadOnlyList<SlicerProfileDto>> GetProfilesAsync(CancellationToken ct);
        Task DeleteProfileAsync(Guid id, CancellationToken ct);
    }
}
