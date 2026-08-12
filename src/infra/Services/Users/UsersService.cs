using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Services.Authentication;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Users;

/// <summary>
/// Service for managing user accounts, authentication, and user-related operations.
/// </summary>
/// <remarks>
/// This service provides comprehensive user management capabilities including:
/// - User CRUD operations (create, read, update, delete)
/// - Password hashing and validation
/// - User authentication and identity management
/// - Logging of all user-related operations for audit trails
/// All user operations are logged through ILogger&lt;UsersService&gt; for observability.
/// </remarks>
/// <remarks>
/// Initializes a new instance of the UsersService with required dependencies.
/// </remarks>
/// <param name="users">Repository for user data persistence and retrieval</param>
/// <param name="authService">Service for authentication operations and token management</param>
/// <param name="passwordHashingService">Service for secure password hashing and verification</param>
/// <param name="revocationService">Shared helper to revoke a user's active sessions when their effective permissions change</param>
/// <param name="authAuditService">Service for recording authentication and authorization audit events</param>
/// <exception cref="ArgumentNullException">Thrown when any required dependency is null</exception>
public class UsersService(
    IUsersRepository users,
    IAuthenticationService authService,
    IPasswordHashingService passwordHashingService,
    IEffectivePermissionsRevocationService revocationService,
    IAuthAuditService authAuditService) : IUsersService
{
    private readonly IUsersRepository _users = users ?? throw new ArgumentNullException(nameof(users));
    private readonly IAuthenticationService _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    private readonly IPasswordHashingService _passwordHashingService = passwordHashingService ?? throw new ArgumentNullException(nameof(passwordHashingService));
    private readonly IEffectivePermissionsRevocationService _revocationService = revocationService ?? throw new ArgumentNullException(nameof(revocationService));
    private readonly IAuthAuditService _authAuditService = authAuditService ?? throw new ArgumentNullException(nameof(authAuditService));

    /// <summary>
    /// Retrieves all user accounts from the system.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Read-only list of all UserDto objects representing system users with their roles and permissions</returns>
    /// <remarks>
    /// This method retrieves a complete list of all user accounts in the system, including their roles and associated
    /// permissions. Each UserDto contains identity information, account status, and role/permission details.
    /// </remarks>
    public async Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct)
    {
        return await _users.GetUsersAsync(ct);
    }

    /// <summary>
    /// Creates a new user account with the provided credentials and role assignments.
    /// </summary>
    /// <param name="request">User creation request containing username, email, password, name, and role IDs</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>UserDto representing the newly created user with assigned roles and permissions</returns>
    /// <remarks>
    /// This method creates a new user account with secure password hashing. The password is hashed using
    /// the configured password hashing service before storage. The user is created with IsActive=true and
    /// EmailConfirmed=false by default. User roles are assigned based on the RoleIds provided in the request.
    /// Creation and update timestamps are set to current UTC time. All user creation operations are logged
    /// through ILogger&lt;UsersService&gt; for audit trails.
    /// </remarks>
    public async Task<UserDto> CreateUserAsync(CreateUserRequest request, CancellationToken ct)
    {
        User user = new User
        {
            Id = Guid.NewGuid(),
            Username = request.Username,
            Email = request.Email,
            FirstName = request.FirstName,
            LastName = request.LastName,
            PasswordHash = _passwordHashingService.HashPassword(request.Password),
            IsActive = true,
            EmailConfirmed = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _users.AddUserAsync(user, request.RoleIds, ct);
        await _users.SaveChangesAsync(ct);

        UserDto? created = await _authService.GetUserWithRolesAndPermissionsAsync(user.Id);
        return created!;
    }

    /// <summary>
    /// Updates an existing user account with new profile information and role assignments.
    /// </summary>
    /// <param name="id">Unique identifier of the user to update</param>
    /// <param name="request">Update request containing optional fields: FirstName, LastName, IsActive, RoleIds</param>
    /// <param name="actorUserId">The administrator performing the update; attributed on any resulting session revocation and audit event.</param>
    /// <param name="ipAddress">The IP address the request was made from.</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Updated UserDto with current account information and roles; null if user not found</returns>
    /// <remarks>
    /// This method updates user account information. All fields in the update request are optional and only
    /// provided fields are modified. Supports updating:
    /// - FirstName and LastName: User's display name information
    /// - IsActive: Account activation status
    /// - RoleIds: User's assigned roles and associated permissions
    ///
    /// The update timestamp is automatically set to current UTC time. Role updates trigger a reload of the
    /// user's complete profile including permissions. Returns null if the specified user ID does not exist.
    ///
    /// If RoleIds actually changes the user's active role membership (a role was added or removed --
    /// resubmitting the same set is a no-op), this revokes all of the user's active tokens through
    /// <see cref="IEffectivePermissionsRevocationService"/> -- the same shared fan-out path used when a
    /// role's own permission grants change (#1471) -- and records a <see cref="AuthEventType.RoleAssignmentChanged"/>
    /// audit event (#1454). Revocation happens only after the role change has committed, so a failed
    /// save cannot leave a role change applied without its corresponding fan-out.
    /// </remarks>
    public async Task<UserDto?> UpdateUserAsync(Guid id, UpdateUserRequest request, Guid actorUserId, string? ipAddress, CancellationToken ct)
    {
        User? user = await _users.GetUserEntityAsync(id, ct);
        if (user == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(request.FirstName))
        {
            user.FirstName = request.FirstName;
        }

        if (!string.IsNullOrWhiteSpace(request.LastName))
        {
            user.LastName = request.LastName;
        }

        if (request.IsActive.HasValue)
        {
            user.IsActive = request.IsActive.Value;
        }

        user.UpdatedAt = DateTime.UtcNow;

        // If RoleIds is present, UpdateUserRolesAsync atomically captures the user's pre-update
        // active role set and replaces it (and flushes the User field edits above) inside one
        // serializable transaction, closing the race where two concurrent role updates for the
        // same user could otherwise silently merge into the union of both requests. Otherwise,
        // just persist the field edits as before.
        HashSet<Guid>? beforeRoleIds = null;
        if (request.RoleIds != null)
        {
            List<Guid> previousRoleIds = await _users.UpdateUserRolesAsync(id, request.RoleIds, ct);
            beforeRoleIds = new HashSet<Guid>(previousRoleIds);
        }
        else
        {
            await _users.SaveChangesAsync(ct);
        }

        if (beforeRoleIds is not null)
        {
            // Re-read the actually-persisted role set rather than trusting request.RoleIds verbatim --
            // UpdateUserRolesAsync silently drops role IDs that don't correspond to a real Role, so
            // diffing against the raw request could report a role as "added" that was never assigned.
            List<Guid> persistedRoleIds = await _users.GetActiveRoleIdsAsync(id, ct);
            var afterRoleIds = new HashSet<Guid>(persistedRoleIds);
            if (!beforeRoleIds.SetEquals(afterRoleIds))
            {
                List<Guid> addedRoleIds = afterRoleIds.Except(beforeRoleIds).ToList();
                List<Guid> removedRoleIds = beforeRoleIds.Except(afterRoleIds).ToList();

                IReadOnlyList<RoleDto> allRoles = await _users.GetRolesAsync(ct);
                Dictionary<Guid, string> roleNameById = allRoles.ToDictionary(r => r.Id, r => r.Name);
                List<string> addedRoleNames = addedRoleIds
                    .Select(roleId => roleNameById.TryGetValue(roleId, out string? name) ? name : roleId.ToString())
                    .ToList();
                List<string> removedRoleNames = removedRoleIds
                    .Select(roleId => roleNameById.TryGetValue(roleId, out string? name) ? name : roleId.ToString())
                    .ToList();

                int revokedSessionCount = await _revocationService.RevokeUsersAsync(
                    [id],
                    actorUserId,
                    "User's role assignment changed",
                    ipAddress,
                    ct).ConfigureAwait(false);

                await _authAuditService.LogRoleAssignmentChangedAsync(
                    actorUserId,
                    id,
                    addedRoleNames,
                    removedRoleNames,
                    revokedSessionCount,
                    ipAddress,
                    cancellationToken: ct).ConfigureAwait(false);
            }
        }

        return await _authService.GetUserWithRolesAndPermissionsAsync(user.Id);
    }

    /// <summary>
    /// Deletes a user account from the system, removing all associated roles and permissions.
    /// </summary>
    /// <param name="id">Unique identifier of the user to delete</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if user was successfully deleted; false if user not found</returns>
    /// <remarks>
    /// This method permanently removes a user account from the system, including all role assignments and
    /// associated authentication data. The operation is irreversible. Deletion of active users will revoke their
    /// access to the system. All deletion operations are logged through ILogger&lt;UsersService&gt; for audit trails.
    /// </remarks>
    public async Task<bool> DeleteUserAsync(Guid id, CancellationToken ct)
    {
        User? user = await _users.GetUserEntityAsync(id, ct);
        if (user == null)
        {
            return false;
        }

        await _users.DeleteUserAsync(id, ct);
        await _users.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Retrieves all available roles and their associated permissions.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Read-only list of RoleDto objects representing all system roles with their permissions</returns>
    /// <remarks>
    /// This method retrieves the complete set of roles available in the system along with the permissions
    /// assigned to each role. Used for role management and user assignment operations. Role definitions
    /// determine what actions users can perform within the system.
    /// </remarks>
    public async Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct)
    {
        return await _users.GetRolesAsync(ct);
    }

    /// <summary>
    /// Checks the availability of a username and/or email address for new user registration.
    /// </summary>
    /// <param name="username">Optional username to check for existence; null or whitespace to skip check</param>
    /// <param name="email">Optional email address to check for existence; null or whitespace to skip check</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>UserAvailabilityDto containing nullable booleans indicating existence of provided username/email</returns>
    /// <remarks>
    /// This method checks whether a username and/or email address are already registered in the system.
    /// Both parameters are optional. If a parameter is not provided (null or whitespace), the corresponding
    /// availability result will be null. Returns only the availability results for values explicitly checked.
    /// Used during user registration flow to validate username and email uniqueness constraints.
    /// </remarks>
    public async Task<UserAvailabilityDto> CheckAvailabilityAsync(string? username, string? email, CancellationToken ct)
    {
        bool? usernameExists = null;
        bool? emailExists = null;
        if (!string.IsNullOrWhiteSpace(username))
        {
            usernameExists = await _users.UsernameExistsAsync(username.Trim(), ct);
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            emailExists = await _users.EmailExistsAsync(email.Trim(), ct);
        }

        return new UserAvailabilityDto(usernameExists, emailExists);
    }

    /// <summary>
    /// Changes the password hash for the specified target user.
    /// </summary>
    /// <param name="userId">Target user identifier.</param>
    /// <param name="newPassword">The new plaintext password (hashed before save).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when target user exists and was updated.</returns>
    public async Task<bool> ChangeUserPasswordAsync(Guid userId, string newPassword, CancellationToken ct)
    {
        User? user = await _users.GetUserEntityAsync(userId, ct);
        if (user is null)
        {
            return false;
        }

        user.PasswordHash = _passwordHashingService.HashPassword(newPassword);
        user.UpdatedAt = DateTime.UtcNow;
        await _users.SaveChangesAsync(ct);
        return true;
    }
}
