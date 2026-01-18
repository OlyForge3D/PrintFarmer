using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Authentication;

namespace Farm.Web.Api.Services.Users
{
    /// <summary>
    /// Service for managing user accounts, authentication, and user-related operations.
    /// </summary>
    /// <remarks>
    /// This service provides comprehensive user management capabilities including:
    /// - User CRUD operations (create, read, update, delete)
    /// - Password hashing and validation
    /// - User authentication and identity management
    /// - Logging of all user-related operations for audit trails
    /// All user operations are logged through IUnifiedLoggingService for observability.
    /// </remarks>
    /// <remarks>
    /// Initializes a new instance of the UsersService with required dependencies.
    /// </remarks>
    /// <param name="users">Repository for user data persistence and retrieval</param>
    /// <param name="authService">Service for authentication operations and token management</param>
    /// <param name="passwordHashingService">Service for secure password hashing and verification</param>
    /// <exception cref="ArgumentNullException">Thrown when any required dependency is null</exception>
    public class UsersService(
        IUsersRepository users,
        IAuthenticationService authService,
        IPasswordHashingService passwordHashingService) : IUsersService
    {
        private readonly IUsersRepository _users = users ?? throw new ArgumentNullException(nameof(users));
        private readonly IAuthenticationService _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        private readonly IPasswordHashingService _passwordHashingService = passwordHashingService ?? throw new ArgumentNullException(nameof(passwordHashingService));

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
        /// through IUnifiedLoggingService for audit trails.
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
        /// </remarks>
        public async Task<UserDto?> UpdateUserAsync(Guid id, UpdateUserRequest request, CancellationToken ct)
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

            if (request.RoleIds != null)
            {
                await _users.UpdateUserRolesAsync(id, request.RoleIds, ct);
            }

            await _users.SaveChangesAsync(ct);

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
        /// access to the system. All deletion operations are logged through IUnifiedLoggingService for audit trails.
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
    }
}
