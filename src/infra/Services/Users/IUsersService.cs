using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Auth;

namespace Farm.Infrastructure.Services.Users
{
    /// <summary>
    /// Service for user account management operations.
    /// </summary>
    public interface IUsersService
    {
        /// <summary>Gets all users in the system.</summary>
        Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct);

        /// <summary>Creates a new user account.</summary>
        Task<UserDto> CreateUserAsync(CreateUserRequest request, CancellationToken ct);

        /// <summary>Updates an existing user's profile.</summary>
        /// <returns>The updated user, or null if not found.</returns>
        Task<UserDto?> UpdateUserAsync(Guid id, UpdateUserRequest request, CancellationToken ct);

        /// <summary>Deletes a user account.</summary>
        /// <returns>True if deleted; false if not found.</returns>
        Task<bool> DeleteUserAsync(Guid id, CancellationToken ct);

        /// <summary>Gets all available roles.</summary>
        Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct);

        /// <summary>Checks if a username and/or email are available for registration.</summary>
        Task<UserAvailabilityDto> CheckAvailabilityAsync(string? username, string? email, CancellationToken ct);
    }
}
