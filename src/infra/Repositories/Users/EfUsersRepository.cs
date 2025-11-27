using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Shared;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Users;

public class EfUsersRepository : IUsersRepository
{
    private readonly AppDbContext _db;

    public EfUsersRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default)
    {
        List<User> users = await _db.Users
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .AsNoTracking()
            .ToListAsync(ct);

        return users.Select(u => new UserDto(
            u.Id,
            u.Username,
            u.Email,
            u.FirstName,
            u.LastName,
            u.IsActive,
            u.EmailConfirmed,
            u.LastLogin,
            u.CreatedAt,
            u.UserRoles.Where(ur => ur.IsActive).Select(ur => ur.Role.Name).ToArray(),
            Array.Empty<string>())).ToList();
    }

    public async Task<User?> GetUserEntityAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Users.FindAsync(new object[] { id }, cancellationToken: ct);
    }

    public async Task<bool> AnyUserByUsernameOrEmailAsync(string username, string email, CancellationToken ct = default)
    {
        return await _db.Users.AnyAsync(u => u.Username == username || u.Email == email, ct);
    }

    public async Task AddUserAsync(User user, IEnumerable<Guid>? roleIds, CancellationToken ct = default)
    {
        _ = _db.Users.Add(user);
        if (roleIds is { })
        {
            foreach (Guid roleId in roleIds)
            {
                Role? role = await _db.Roles.FindAsync(new object?[] { roleId }, cancellationToken: ct);
                if (role != null)
                {
                    _ = _db.UserRoles.Add(new UserRole
                    {
                        Id = Guid.NewGuid(),
                        UserId = user.Id,
                        RoleId = roleId,
                        AssignedAt = DateTime.UtcNow,
                        IsActive = true
                    });
                }
            }
        }
    }

    public async Task UpdateUserRolesAsync(Guid userId, IEnumerable<Guid> roleIds, CancellationToken ct = default)
    {
        List<UserRole> existingRoles = await _db.UserRoles.Where(ur => ur.UserId == userId).ToListAsync(ct);
        _db.UserRoles.RemoveRange(existingRoles);
        foreach (Guid roleId in roleIds)
        {
            Role? role = await _db.Roles.FindAsync(new object?[] { roleId }, cancellationToken: ct);
            if (role != null)
            {
                _ = _db.UserRoles.Add(new UserRole
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    RoleId = roleId,
                    AssignedAt = DateTime.UtcNow,
                    IsActive = true
                });
            }
        }
    }

    public async Task DeleteUserAsync(Guid id, CancellationToken ct = default)
    {
        User? user = await _db.Users.FindAsync(new object[] { id }, cancellationToken: ct);
        if (user == null)
        {
            return;
        }
        List<UserRole> userRoles = await _db.UserRoles.Where(ur => ur.UserId == id).ToListAsync(ct);
        _db.UserRoles.RemoveRange(userRoles);
        _ = _db.Users.Remove(user);
    }

    public async Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct = default)
    {
        List<Role> roles = await _db.Roles
            .Include(r => r.RolePermissions)
            .ThenInclude(rp => rp.Resource)
            .Include(r => r.RolePermissions)
            .ThenInclude(rp => rp.Action)
            .AsNoTracking()
            .ToListAsync(ct);

        return roles.Select(r => new RoleDto(
            r.Id,
            r.Name,
            r.DisplayName,
            r.Description,
            r.IsSystemRole,
            r.IsActive,
            r.CreatedAt,
            r.RolePermissions.Select(rp => new RolePermissionDto(
                rp.Id,
                rp.RoleId,
                rp.ResourceId,
                rp.ActionId,
                rp.Resource.Name,
                rp.Action.Name,
                rp.Granted
            )).ToArray())).ToList();
    }

    public Task<bool> UsernameExistsAsync(string username, CancellationToken ct = default)
    {
        return _db.Users.AnyAsync(x => x.Username == username, ct);
    }

    public Task<bool> EmailExistsAsync(string email, CancellationToken ct = default)
    {
        return _db.Users.AnyAsync(x => x.Email == email, ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        return _db.SaveChangesAsync(ct);
    }

    public Task<bool> HasAdminUsersAsync(CancellationToken ct = default)
    {
        return _db.Users
            .AnyAsync(u => u.UserRoles.Any(ur => ur.Role.Name == "farm_admin" && ur.IsActive), ct);
    }

    public Task<User?> GetAdminByUsernameAndEmailAsync(string username, string email, CancellationToken ct = default)
    {
        return _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u =>
                u.Username == username && u.Email == email &&
                u.UserRoles.Any(ur => ur.Role.Name == "farm_admin" && ur.IsActive), ct);
    }

    public Task<Role?> GetRoleByNameAsync(string roleName, CancellationToken ct = default)
    {
        return _db.Roles.FirstOrDefaultAsync(r => r.Name == roleName, ct);
    }

    public Task<PasswordPolicyEntity?> GetPasswordPolicyAsync(CancellationToken ct = default)
    {
        return _db.PasswordPolicies.OrderBy(p => p.Id).FirstOrDefaultAsync(ct);
    }

    public async Task AddUserWithRoleAsync(User user, Guid roleId, CancellationToken ct = default)
    {
        _ = _db.Users.Add(user);
        _ = _db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RoleId = roleId,
            AssignedAt = DateTime.UtcNow,
            IsActive = true
        });
        await _db.SaveChangesAsync(ct);
    }

    public Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default)
        => _db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);

    public Task<User?> GetByEmailAsync(string email, CancellationToken ct = default)
        => _db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

    public Task<bool> UsernameExistsStrictAsync(string username, CancellationToken ct = default)
        => _db.Users.AnyAsync(u => u.Username == username, ct);

    public Task<bool> EmailExistsStrictAsync(string email, CancellationToken ct = default)
        => _db.Users.AnyAsync(u => u.Email == email, ct);

    public async Task<List<string>> GetActiveRoleNamesAsync(Guid userId, CancellationToken ct = default)
    {
        return await _db.UserRoles
            .Where(ur => ur.UserId == userId && ur.IsActive && (ur.ExpiresAt == null || ur.ExpiresAt > DateTime.UtcNow))
            .Select(ur => ur.Role.Name)
            .ToListAsync(ct);
    }

    public async Task<List<(string Resource, string Action)>> GetGrantedPermissionsAsync(Guid userId, CancellationToken ct = default)
    {
        return await _db.UserRoles
            .Where(ur => ur.UserId == userId && ur.IsActive && (ur.ExpiresAt == null || ur.ExpiresAt > DateTime.UtcNow))
            .SelectMany(ur => ur.Role.RolePermissions)
            .Where(rp => rp.Granted)
            .Select(rp => new ValueTuple<string, string>(rp.Resource.Name, rp.Action.Name))
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<bool> UpdatePasswordAsync(Guid userId, string currentPassword, string newPasswordHash, CancellationToken ct = default)
    {
        Console.WriteLine($"[EfUsersRepository] UpdatePasswordAsync called for UserId={userId}");
        User? user = await _db.Users.FindAsync(new object[] { userId }, cancellationToken: ct);
        Console.WriteLine($"[EfUsersRepository] User found={user != null}");
        if (user == null)
        {
            return false;
        }
        // Current password check is done in service; repository only updates if hash differs
        string existingPreview = user.PasswordHash is not null && user.PasswordHash.Length > 10 ? user.PasswordHash.Substring(0, 10) : (user.PasswordHash ?? "(null)");
        string newPreview = newPasswordHash is not null && newPasswordHash.Length > 10 ? newPasswordHash.Substring(0, 10) : (newPasswordHash ?? "(null)");
        Console.WriteLine($"[EfUsersRepository] ExistingHashPreview={existingPreview} NewHashPreview={newPreview}");
        if (user.PasswordHash == newPasswordHash)
        {
            return true; // no change needed
        }
        user.PasswordHash = newPasswordHash ?? string.Empty;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        Console.WriteLine($"[EfUsersRepository] Password updated for UserId={userId}");
        return true;
    }

    public async Task CreatePasswordResetTokenAsync(PasswordResetToken token, CancellationToken ct = default)
    {
        _ = _db.PasswordResetTokens.Add(token);
        await _db.SaveChangesAsync(ct);
    }

    public Task<PasswordResetToken?> GetPasswordResetTokenAsync(string token, CancellationToken ct = default)
    {
        return _db.PasswordResetTokens
            .Include(prt => prt.User)
            .FirstOrDefaultAsync(prt => prt.Token == token, ct);
    }

    public Task<User?> GetByEmailConfirmationTokenAsync(string token, CancellationToken ct = default)
    {
        return _db.Users
            .FirstOrDefaultAsync(u => u.EmailConfirmationToken == token, ct);
    }
}
