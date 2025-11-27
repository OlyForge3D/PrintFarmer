using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Web.Shared;

namespace Farm.Infrastructure.Repositories.Users;

public interface IUsersRepository
{
    Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default);
    Task<User?> GetUserEntityAsync(Guid id, CancellationToken ct = default);
    Task<bool> AnyUserByUsernameOrEmailAsync(string username, string email, CancellationToken ct = default);
    Task AddUserAsync(User user, IEnumerable<Guid>? roleIds, CancellationToken ct = default);
    Task UpdateUserRolesAsync(Guid userId, IEnumerable<Guid> roleIds, CancellationToken ct = default);
    Task DeleteUserAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct = default);
    Task<bool> UsernameExistsAsync(string username, CancellationToken ct = default);
    Task<bool> EmailExistsAsync(string email, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);

    // Setup-specific methods
    Task<bool> HasAdminUsersAsync(CancellationToken ct = default);
    Task<User?> GetAdminByUsernameAndEmailAsync(string username, string email, CancellationToken ct = default);
    Task<Role?> GetRoleByNameAsync(string roleName, CancellationToken ct = default);
    Task<PasswordPolicyEntity?> GetPasswordPolicyAsync(CancellationToken ct = default);
    Task AddUserWithRoleAsync(User user, Guid roleId, CancellationToken ct = default);

    // Authentication-specific additions
    Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default);
    Task<User?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<bool> UsernameExistsStrictAsync(string username, CancellationToken ct = default); // mirror direct check
    Task<bool> EmailExistsStrictAsync(string email, CancellationToken ct = default);
    Task<List<string>> GetActiveRoleNamesAsync(Guid userId, CancellationToken ct = default);
    Task<List<(string Resource, string Action)>> GetGrantedPermissionsAsync(Guid userId, CancellationToken ct = default);
    Task<bool> UpdatePasswordAsync(Guid userId, string currentPassword, string newPasswordHash, CancellationToken ct = default);

    // Password reset methods
    Task CreatePasswordResetTokenAsync(PasswordResetToken token, CancellationToken ct = default);
    Task<PasswordResetToken?> GetPasswordResetTokenAsync(string token, CancellationToken ct = default);

    // Email confirmation methods
    Task<User?> GetByEmailConfirmationTokenAsync(string token, CancellationToken ct = default);
}
