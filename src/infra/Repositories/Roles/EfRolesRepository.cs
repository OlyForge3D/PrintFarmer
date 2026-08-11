using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Contracts.Roles;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Roles;

public class EfRolesRepository(AppDbContext db) : IRolesRepository
{
    private readonly AppDbContext _db = db;

    public async Task<List<RoleSummaryDto>> GetRoleSummariesAsync(CancellationToken ct = default)
    {
        return await _db.Roles
            .AsNoTracking()
            .Select(r => new RoleSummaryDto
            {
                Id = r.Id,
                Name = r.Name,
                DisplayName = r.DisplayName,
                Description = r.Description,
                IsSystemRole = r.IsSystemRole,
                IsActive = r.IsActive,
                CreatedAt = r.CreatedAt,
                UpdatedAt = r.UpdatedAt,
                MemberCount = r.UserRoles.Count(ur => ur.IsActive),
                PermissionCount = r.RolePermissions.Count(rp => rp.Granted)
            })
            .OrderBy(r => r.Name)
            .ToListAsync(ct);
    }

    public Task<Role?> GetRoleEntityAsync(Guid id, CancellationToken ct = default)
    {
        return _db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<RoleDetailDto?> GetRoleDetailAsync(Guid id, CancellationToken ct = default)
    {
        Role? role = await _db.Roles
            .Include(r => r.RolePermissions)
            .ThenInclude(rp => rp.Resource)
            .Include(r => r.RolePermissions)
            .ThenInclude(rp => rp.Action)
            .AsNoTracking()
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        if (role is null)
        {
            return null;
        }

        int memberCount = await _db.UserRoles.CountAsync(ur => ur.RoleId == id && ur.IsActive, ct);

        return new RoleDetailDto
        {
            Id = role.Id,
            Name = role.Name,
            DisplayName = role.DisplayName,
            Description = role.Description,
            IsSystemRole = role.IsSystemRole,
            IsActive = role.IsActive,
            CreatedAt = role.CreatedAt,
            UpdatedAt = role.UpdatedAt,
            MemberCount = memberCount,
            PermissionCount = role.RolePermissions.Count(rp => rp.Granted),
            Permissions = role.RolePermissions.Select(rp => new PermissionDto
            {
                Resource = rp.Resource.Name,
                Action = rp.Action.Name,
                Granted = rp.Granted
            }).ToList()
        };
    }

    public async Task<bool> NameExistsAsync(string name, Guid? excludeRoleId = null, CancellationToken ct = default)
    {
        string normalized = name.ToLowerInvariant();

        // CA1862 prefers string.Equals(..., StringComparison.OrdinalIgnoreCase), but that pattern
        // is not reliably translated to SQL across all supported providers (SQLite/PostgreSQL/
        // SQL Server); ToLower() comparison is the well-established EF-translatable form here.
#pragma warning disable CA1862
        return await _db.Roles.AnyAsync(
            r => r.Name.ToLower() == normalized && (excludeRoleId == null || r.Id != excludeRoleId),
            ct);
#pragma warning restore CA1862
    }

    public async Task AddRoleAsync(Role role, CancellationToken ct = default)
    {
        _ = _db.Roles.Add(role);
        _ = await Task.FromResult(0);
    }

    public async Task<(Guid ResourceId, Guid ActionId)?> ResolvePermissionAsync(string resource, string action, CancellationToken ct = default)
    {
        Resource? resourceEntity = await _db.Resources.FirstOrDefaultAsync(r => r.Name == resource, ct);
        UserAction? actionEntity = await _db.UserActions.FirstOrDefaultAsync(a => a.Name == action, ct);
        if (resourceEntity is null || actionEntity is null)
        {
            return null;
        }

        return (resourceEntity.Id, actionEntity.Id);
    }

    public async Task AddRolePermissionsAsync(Guid roleId, IEnumerable<(Guid ResourceId, Guid ActionId)> pairs, CancellationToken ct = default)
    {
        foreach ((Guid resourceId, Guid actionId) in pairs)
        {
            _ = _db.RolePermissions.Add(new RolePermission
            {
                Id = Guid.NewGuid(),
                RoleId = roleId,
                ResourceId = resourceId,
                ActionId = actionId,
                Granted = true,
                CreatedAt = DateTime.UtcNow
            });
        }

        _ = await Task.FromResult(0);
    }

    public async Task CopyRolePermissionsAsync(Guid sourceRoleId, Guid targetRoleId, CancellationToken ct = default)
    {
        List<RolePermission> sourcePermissions = await _db.RolePermissions
            .Where(rp => rp.RoleId == sourceRoleId && rp.Granted)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (RolePermission source in sourcePermissions)
        {
            _ = _db.RolePermissions.Add(new RolePermission
            {
                Id = Guid.NewGuid(),
                RoleId = targetRoleId,
                ResourceId = source.ResourceId,
                ActionId = source.ActionId,
                Granted = true,
                CreatedAt = DateTime.UtcNow
            });
        }
    }

    public Task<int> CountActiveMembersAsync(Guid roleId, CancellationToken ct = default)
    {
        return _db.UserRoles.CountAsync(ur => ur.RoleId == roleId && ur.IsActive, ct);
    }

    public async Task ReassignMembersAsync(Guid fromRoleId, Guid toRoleId, CancellationToken ct = default)
    {
        List<UserRole> members = await _db.UserRoles
            .Where(ur => ur.RoleId == fromRoleId)
            .ToListAsync(ct);

        foreach (UserRole membership in members)
        {
            bool alreadyHasTarget = await _db.UserRoles
                .AnyAsync(ur => ur.UserId == membership.UserId && ur.RoleId == toRoleId, ct);

            if (alreadyHasTarget)
            {
                // Avoid violating the unique (UserId, RoleId) constraint — the user already
                // holds the target role, so the stale assignment to the deleted role is
                // simply dropped.
                _ = _db.UserRoles.Remove(membership);
            }
            else
            {
                membership.RoleId = toRoleId;
            }
        }
    }

    public async Task RemoveMembersAsync(Guid roleId, CancellationToken ct = default)
    {
        // EF Core 10: ExecuteDeleteAsync for efficient bulk delete without loading entities.
        // It bypasses the change tracker, so any UserRole entities already tracked for this
        // role (e.g. loaded earlier in the same DbContext/unit of work) must be detached here.
        // Otherwise a later SaveChangesAsync on the same context will try to cascade-delete
        // those stale tracked entries again and throw a DbUpdateConcurrencyException because
        // the rows are already gone.
        _ = await _db.UserRoles.Where(ur => ur.RoleId == roleId).ExecuteDeleteAsync(ct);

        foreach (var entry in _db.ChangeTracker.Entries<UserRole>()
            .Where(e => e.Entity.RoleId == roleId && e.State != Microsoft.EntityFrameworkCore.EntityState.Detached)
            .ToList())
        {
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        }
    }

    public Task DeleteRoleAsync(Role role, CancellationToken ct = default)
    {
        _ = _db.Roles.Remove(role);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        return _db.SaveChangesAsync(ct);
    }

    public async Task<bool> IsAdminEquivalentAsync(Guid roleId, CancellationToken ct = default)
    {
        Role? role = await _db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roleId, ct);
        if (role is null || !role.IsActive)
        {
            return false;
        }

        if (string.Equals(role.Name, PrintFarmerPermissions.FarmAdminRole, StringComparison.Ordinal))
        {
            return true;
        }

        return await HasAdminEquivalentGrantsAsync(roleId, ct);
    }

    private async Task<bool> HasAdminEquivalentGrantsAsync(Guid roleId, CancellationToken ct)
    {
        List<string> grantedResourcesForAdmin = await _db.RolePermissions
            .Where(rp => rp.RoleId == roleId && rp.Granted && rp.Action.Name == "admin")
            .Select(rp => rp.Resource.Name)
            .ToListAsync(ct);

        return grantedResourcesForAdmin.Contains("roles") && grantedResourcesForAdmin.Contains("users");
    }

    public async Task<bool> HasOtherActiveAdminCoverageAsync(Guid excludeRoleId, CancellationToken ct = default)
    {
        List<Role> activeRoles = await _db.Roles
            .Where(r => r.IsActive && r.Id != excludeRoleId)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (Role role in activeRoles)
        {
            bool isAdminEquivalent = string.Equals(role.Name, PrintFarmerPermissions.FarmAdminRole, StringComparison.Ordinal)
                || await HasAdminEquivalentGrantsAsync(role.Id, ct);

            if (!isAdminEquivalent)
            {
                continue;
            }

            bool hasActiveEnabledMember = await _db.UserRoles.AnyAsync(
                ur => ur.RoleId == role.Id
                    && ur.IsActive
                    && (ur.ExpiresAt == null || ur.ExpiresAt > DateTime.UtcNow)
                    && ur.User.IsActive,
                ct);

            if (hasActiveEnabledMember)
            {
                return true;
            }
        }

        return false;
    }

    public async Task<bool> UserHasOtherActiveAdminEquivalentRoleAsync(Guid userId, Guid excludeRoleId, CancellationToken ct = default)
    {
        List<Guid> otherRoleIds = await _db.UserRoles
            .Where(ur => ur.UserId == userId
                && ur.IsActive
                && ur.RoleId != excludeRoleId
                && (ur.ExpiresAt == null || ur.ExpiresAt > DateTime.UtcNow))
            .Select(ur => ur.RoleId)
            .ToListAsync(ct);

        foreach (Guid roleId in otherRoleIds)
        {
            if (await IsAdminEquivalentAsync(roleId, ct))
            {
                return true;
            }
        }

        return false;
    }

    public Task<bool> UserIsActiveMemberOfRoleAsync(Guid userId, Guid roleId, CancellationToken ct = default)
    {
        return _db.UserRoles.AnyAsync(
            ur => ur.UserId == userId
                && ur.RoleId == roleId
                && ur.IsActive
                && (ur.ExpiresAt == null || ur.ExpiresAt > DateTime.UtcNow),
            ct);
    }
}
