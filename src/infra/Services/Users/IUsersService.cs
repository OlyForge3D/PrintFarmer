using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Auth;

namespace Farm.Infrastructure.Services.Users;

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
    /// <param name="id">User to update.</param>
    /// <param name="request">Update payload.</param>
    /// <param name="actorUserId">The administrator performing the update, used to attribute any resulting session revocation and audit event.</param>
    /// <param name="ipAddress">The IP address the request was made from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated user, or null if not found.</returns>
    Task<UserDto?> UpdateUserAsync(Guid id, UpdateUserRequest request, Guid actorUserId, string? ipAddress, CancellationToken ct);

    /// <summary>Deletes a user account.</summary>
    /// <returns>True if deleted; false if not found.</returns>
    Task<bool> DeleteUserAsync(Guid id, CancellationToken ct);

    /// <summary>Gets all available roles.</summary>
    Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct);

    /// <summary>Checks if a username and/or email are available for registration.</summary>
    Task<UserAvailabilityDto> CheckAvailabilityAsync(string? username, string? email, CancellationToken ct);

    /// <summary>Changes password for a target user (admin operation).</summary>
    /// <returns>True when target user exists and password was changed.</returns>
    Task<bool> ChangeUserPasswordAsync(Guid userId, string newPassword, CancellationToken ct);
}
