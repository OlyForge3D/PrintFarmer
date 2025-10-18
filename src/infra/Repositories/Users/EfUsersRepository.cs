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
        var users = await _db.Users
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
        var existingRoles = await _db.UserRoles.Where(ur => ur.UserId == userId).ToListAsync(ct);
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
        var user = await _db.Users.FindAsync(new object[] { id }, cancellationToken: ct);
        if (user == null) return;
        var userRoles = await _db.UserRoles.Where(ur => ur.UserId == id).ToListAsync(ct);
        _db.UserRoles.RemoveRange(userRoles);
        _ = _db.Users.Remove(user);
    }

    public async Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct = default)
    {
        var roles = await _db.Roles
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
}
