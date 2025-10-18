using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Web.Shared;
using Farm.Infrastructure.Repositories.Users;
using Farm.Web.Api.Services.Authentication;

namespace Farm.Web.Api.Services.Users
{
    public class UsersService : IUsersService
    {
        private readonly IUsersRepository _users;
        private readonly IAuthenticationService _authService;
        private readonly IPasswordHashingService _passwordHashingService;

        public UsersService(IUsersRepository users, IAuthenticationService authService, IPasswordHashingService passwordHashingService)
        {
            _users = users ?? throw new ArgumentNullException(nameof(users));
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            _passwordHashingService = passwordHashingService ?? throw new ArgumentNullException(nameof(passwordHashingService));
        }

        public async Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct)
        {
            return await _users.GetUsersAsync(ct);
        }

        public async Task<UserDto> CreateUserAsync(Farm.Web.Shared.CreateUserRequest request, CancellationToken ct)
        {
            var user = new User
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

            var created = await _authService.GetUserWithRolesAndPermissionsAsync(user.Id);
            return created!;
        }

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

        public async Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct)
        {
            return await _users.GetRolesAsync(ct);
        }

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
