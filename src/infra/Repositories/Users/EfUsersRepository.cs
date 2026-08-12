using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Users;

public class EfUsersRepository(AppDbContext db) : IUsersRepository
{
    private readonly AppDbContext _db = db;

    public async Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default)
    {
        List<User> users = await _db.Users
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .AsNoTracking()
            .AsSplitQuery()
            .ToListAsync(ct);

        return users.Select(u => new UserDto
        {
            Id = u.Id,
            Username = u.Username,
            Email = u.Email,
            FirstName = u.FirstName,
            LastName = u.LastName,
            IsActive = u.IsActive,
            EmailConfirmed = u.EmailConfirmed,
            LastLogin = u.LastLogin,
            CreatedAt = u.CreatedAt,
            Roles = u.UserRoles.Where(ur => ur.IsActive).Select(ur => ur.Role.Name).ToList(),
            Permissions = new List<string>()
        }).ToList();
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

    public async Task<RoleAssignmentDiff> UpdateUserRolesAsync(Guid userId, IEnumerable<Guid> roleIds, CancellationToken ct = default)
    {
        // The read-check-write-reread below (capture the user's current active role assignment,
        // replace it, then re-read the resulting active role assignment) commits as one unit
        // under serializable isolation together with any other tracked changes on this context
        // (e.g. the caller's pending User field edits), via the single SaveChangesAsync call
        // inside the transaction. Without this, two concurrent role updates for the same user
        // could each read a pre-conflict "before" role set, both pass diff/no-op checks, and both
        // commit -- silently merging into the union of both requests' role sets rather than
        // either admin's intended final state. The "after" state is also read from inside this
        // same transaction (before commit) rather than by the caller afterwards, so a third,
        // unrelated concurrent update to this user's roles can't be attributed to this request's
        // diff/audit entry. Mirrors the transaction pattern
        // RolePermissionService.UpdateRolePermissionsAsync uses for role permission changes
        // (#1454 review discussion).
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await _db.Database
            .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);

        List<Guid> beforeRoleIds = await GetActiveRoleIdsAsync(userId, ct);

        // EF Core 10: Use ExecuteDeleteAsync for efficient bulk delete without loading entities
        await _db.UserRoles.Where(ur => ur.UserId == userId).ExecuteDeleteAsync(ct);
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

        await _db.SaveChangesAsync(ct);
        List<Guid> afterRoleIds = await GetActiveRoleIdsAsync(userId, ct);
        await transaction.CommitAsync(ct);

        return new RoleAssignmentDiff(beforeRoleIds, afterRoleIds);
    }

    public async Task DeleteUserAsync(Guid id, CancellationToken ct = default)
    {
        User? user = await _db.Users.FindAsync(new object[] { id }, cancellationToken: ct);
        if (user == null)
        {
            return;
        }

        // EF Core 10: Use ExecuteDeleteAsync for efficient bulk delete without loading entities
        await _db.UserRoles.Where(ur => ur.UserId == id).ExecuteDeleteAsync(ct);
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
            .AsSplitQuery()
            .ToListAsync(ct);

        return roles.Select(r => new RoleDto
        {
            Id = r.Id,
            Name = r.Name,
            DisplayName = r.DisplayName,
            Description = r.Description,
            IsSystemRole = r.IsSystemRole,
            IsActive = r.IsActive,
            Permissions = r.RolePermissions.Select(rp => new PermissionDto
            {
                Resource = rp.Resource.Name,
                Action = rp.Action.Name,
                Granted = rp.Granted
            }).ToList()
        }).ToList();
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
            .FirstOrDefaultAsync(
                u =>
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
        _ = await _db.SaveChangesAsync(ct);
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

    public async Task<List<Guid>> GetActiveRoleIdsAsync(Guid userId, CancellationToken ct = default)
    {
        return await _db.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId && ur.IsActive && (ur.ExpiresAt == null || ur.ExpiresAt > DateTime.UtcNow))
            .Select(ur => ur.RoleId)
            .ToListAsync(ct);
    }

    public async Task<List<(string Resource, string Action)>> GetGrantedPermissionsAsync(Guid userId, CancellationToken ct = default)
    {
        // Grant/deny precedence: an explicit deny (Granted == false) on any of the user's
        // active roles suppresses a permission even if another active role grants it.
        // See docs/ROLE_PERMISSION_PRECEDENCE.md and issue #1450.
        List<(string Resource, string Action, bool Granted)> rows = await GetRolePermissionRowsAsync(userId, ct);

        return rows
            .GroupBy(rp => (rp.Resource, rp.Action))
            .Where(g => g.All(rp => rp.Granted))
            .Select(g => (g.Key.Resource, g.Key.Action))
            .ToList();
    }

    public async Task<List<(string Resource, string Action)>> GetDeniedPermissionsAsync(Guid userId, CancellationToken ct = default)
    {
        // Mirror of GetGrantedPermissionsAsync: a (resource, action) pair is explicitly
        // denied when at least one of the user's active roles has Granted == false for it,
        // regardless of whether another active role also grants it (the deny still wins,
        // per docs/ROLE_PERMISSION_PRECEDENCE.md). Callers use this to keep the
        // resource:admin implication (PrintFarmerPermissions.ImpliesViaResourceAdmin) from
        // silently overriding an explicit per-action deny.
        List<(string Resource, string Action, bool Granted)> rows = await GetRolePermissionRowsAsync(userId, ct);

        return rows
            .GroupBy(rp => (rp.Resource, rp.Action))
            .Where(g => g.Any(rp => !rp.Granted))
            .Select(g => (g.Key.Resource, g.Key.Action))
            .ToList();
    }

    private async Task<List<(string Resource, string Action, bool Granted)>> GetRolePermissionRowsAsync(Guid userId, CancellationToken ct)
    {
        return await _db.UserRoles
            .Where(ur => ur.UserId == userId && ur.IsActive && (ur.ExpiresAt == null || ur.ExpiresAt > DateTime.UtcNow))
            .SelectMany(ur => ur.Role.RolePermissions)
            .Select(rp => new ValueTuple<string, string, bool>(rp.Resource.Name, rp.Action.Name, rp.Granted))
            .ToListAsync(ct);
    }

    public async Task<bool> UpdatePasswordAsync(Guid userId, string currentPassword, string newPasswordHash, CancellationToken ct = default)
    {
        User? user = await _db.Users.FindAsync(new object[] { userId }, cancellationToken: ct);
        if (user == null)
        {
            return false;
        }

        // Current password check is done in service; repository only updates if hash differs
        if (user.PasswordHash == newPasswordHash)
        {
            return true; // no change needed
        }

        user.PasswordHash = newPasswordHash ?? string.Empty;
        user.UpdatedAt = DateTime.UtcNow;
        _ = await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task CreatePasswordResetTokenAsync(PasswordResetToken token, CancellationToken ct = default)
    {
        _ = _db.PasswordResetTokens.Add(token);
        _ = await _db.SaveChangesAsync(ct);
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
