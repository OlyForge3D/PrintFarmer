using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Users;

/// <summary>
/// Repository interface for user and role management operations.
/// Provides CRUD operations for users, role assignments, and authentication-related queries.
/// </summary>
public interface IUsersRepository
{
    /// <summary>
    /// Gets all users as DTOs.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of user DTOs.</returns>
    Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets a user entity by ID.
    /// </summary>
    /// <param name="id">The user's unique identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The user entity, or null if not found.</returns>
    Task<User?> GetUserEntityAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Checks if any user exists with the given username or email.
    /// </summary>
    /// <param name="username">The username to check.</param>
    /// <param name="email">The email to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if a user exists with either username or email.</returns>
    Task<bool> AnyUserByUsernameOrEmailAsync(string username, string email, CancellationToken ct = default);

    /// <summary>
    /// Adds a new user with optional role assignments.
    /// </summary>
    /// <param name="user">The user entity to add.</param>
    /// <param name="roleIds">Optional collection of role IDs to assign.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddUserAsync(User user, IEnumerable<Guid>? roleIds, CancellationToken ct = default);

    /// <summary>
    /// Updates the roles assigned to a user.
    /// </summary>
    /// <param name="userId">The user's unique identifier.</param>
    /// <param name="roleIds">The new set of role IDs to assign.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateUserRolesAsync(Guid userId, IEnumerable<Guid> roleIds, CancellationToken ct = default);

    /// <summary>
    /// Deletes a user by ID.
    /// </summary>
    /// <param name="id">The user's unique identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteUserAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Gets all available roles as DTOs.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of role DTOs.</returns>
    Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct = default);

    /// <summary>
    /// Checks if a username already exists.
    /// </summary>
    /// <param name="username">The username to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the username exists.</returns>
    Task<bool> UsernameExistsAsync(string username, CancellationToken ct = default);

    /// <summary>
    /// Checks if an email already exists.
    /// </summary>
    /// <param name="email">The email to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the email exists.</returns>
    Task<bool> EmailExistsAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Persists pending changes to the database.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Checks if any users with administrator role exist.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if at least one admin user exists.</returns>
    Task<bool> HasAdminUsersAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets an admin user by username and email combination.
    /// </summary>
    /// <param name="username">The username to match.</param>
    /// <param name="email">The email to match.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The admin user if found, otherwise null.</returns>
    Task<User?> GetAdminByUsernameAndEmailAsync(string username, string email, CancellationToken ct = default);

    /// <summary>
    /// Gets a role entity by its name.
    /// </summary>
    /// <param name="roleName">The role name to find.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The role entity if found, otherwise null.</returns>
    Task<Role?> GetRoleByNameAsync(string roleName, CancellationToken ct = default);

    /// <summary>
    /// Gets the current password policy configuration.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The password policy entity if configured, otherwise null.</returns>
    Task<PasswordPolicyEntity?> GetPasswordPolicyAsync(CancellationToken ct = default);

    /// <summary>
    /// Adds a new user with a specific role assignment.
    /// </summary>
    /// <param name="user">The user entity to add.</param>
    /// <param name="roleId">The role ID to assign.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddUserWithRoleAsync(User user, Guid roleId, CancellationToken ct = default);

    /// <summary>
    /// Gets a user by username for authentication.
    /// </summary>
    /// <param name="username">The username to find.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The user if found, otherwise null.</returns>
    Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default);

    /// <summary>
    /// Gets a user by email address.
    /// </summary>
    /// <param name="email">The email to find.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The user if found, otherwise null.</returns>
    Task<User?> GetByEmailAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Checks if a username exists using strict case-sensitive matching.
    /// </summary>
    /// <param name="username">The username to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the username exists.</returns>
    Task<bool> UsernameExistsStrictAsync(string username, CancellationToken ct = default);

    /// <summary>
    /// Checks if an email exists using strict case-sensitive matching.
    /// </summary>
    /// <param name="email">The email to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the email exists.</returns>
    Task<bool> EmailExistsStrictAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Gets the names of all active roles for a user.
    /// </summary>
    /// <param name="userId">The user's unique identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of role names assigned to the user.</returns>
    Task<List<string>> GetActiveRoleNamesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Gets all granted permissions for a user based on their roles.
    /// </summary>
    /// <param name="userId">The user's unique identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of resource-action permission tuples.</returns>
    Task<List<(string Resource, string Action)>> GetGrantedPermissionsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Updates a user's password after verifying the current password.
    /// </summary>
    /// <param name="userId">The user's unique identifier.</param>
    /// <param name="currentPassword">The current password for verification.</param>
    /// <param name="newPasswordHash">The new password hash to set.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the password was updated successfully.</returns>
    Task<bool> UpdatePasswordAsync(Guid userId, string currentPassword, string newPasswordHash, CancellationToken ct = default);

    /// <summary>
    /// Creates a password reset token for a user.
    /// </summary>
    /// <param name="token">The password reset token entity.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CreatePasswordResetTokenAsync(PasswordResetToken token, CancellationToken ct = default);

    /// <summary>
    /// Gets a password reset token by its token value.
    /// </summary>
    /// <param name="token">The token string to find.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The password reset token if found and valid, otherwise null.</returns>
    Task<PasswordResetToken?> GetPasswordResetTokenAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// Gets a user by their email confirmation token.
    /// </summary>
    /// <param name="token">The email confirmation token.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The user if the token is valid, otherwise null.</returns>
    Task<User?> GetByEmailConfirmationTokenAsync(string token, CancellationToken ct = default);
}
