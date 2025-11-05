using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.Users
{
    public interface IUsersService
    {
        Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct);
        Task<UserDto> CreateUserAsync(Farm.Web.Shared.CreateUserRequest request, CancellationToken ct);
        Task<UserDto?> UpdateUserAsync(Guid id, Farm.Web.Shared.UpdateUserRequest request, CancellationToken ct);
        Task<bool> DeleteUserAsync(Guid id, CancellationToken ct);
        Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct);
        Task<UserAvailabilityDto> CheckAvailabilityAsync(string? username, string? email, CancellationToken ct);
    }
}
