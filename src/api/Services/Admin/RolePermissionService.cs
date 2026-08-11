using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services.Admin;

/// <summary>
/// Implements <see cref="IRolePermissionService"/> by joining <see cref="RolePermission"/>
/// rows against the derived permission catalog (#1446), enforcing farm_admin immutability
/// (D6), optimistic concurrency via <see cref="Role.UpdatedAt"/>, and the D9 lockout
/// invariant that at least one active role must retain <c>roles:admin</c>/<c>users:admin</c>.
/// </summary>
public sealed class RolePermissionService : IRolePermissionService
{
    private const string FarmAdminRoleName = "farm_admin";

    private static readonly (string Resource, string Action)[] LockoutGuardedPermissions =
    [
        ("roles", "admin"),
        ("users", "admin"),
    ];

    private readonly AppDbContext _context;
    private readonly IPermissionCatalogService _permissionCatalogService;
    private readonly IAuthAuditService _authAuditService;
    private readonly ITokenRevocationService _tokenRevocationService;

    public RolePermissionService(
        AppDbContext context,
        IPermissionCatalogService permissionCatalogService,
        IAuthAuditService authAuditService,
        ITokenRevocationService tokenRevocationService)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _permissionCatalogService = permissionCatalogService ?? throw new ArgumentNullException(nameof(permissionCatalogService));
        _authAuditService = authAuditService ?? throw new ArgumentNullException(nameof(authAuditService));
        _tokenRevocationService = tokenRevocationService ?? throw new ArgumentNullException(nameof(tokenRevocationService));
    }

    public async Task<RolePermissionsDto?> GetRolePermissionsAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        Role? role = await _context.Roles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == roleId, cancellationToken)
            .ConfigureAwait(false);
        if (role is null)
        {
            return null;
        }

        PermissionCatalogDto catalog = await _permissionCatalogService.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, bool> grantStatus = await LoadGrantStatusAsync(roleId, cancellationToken).ConfigureAwait(false);

        return BuildDto(role, catalog, grantStatus);
    }

    public async Task<RolePermissionUpdateResult> UpdateRolePermissionsAsync(
        Guid roleId,
        UpdateRolePermissionsRequestDto request,
        Guid actingUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The read-check-write sequence below (role lookup, farm_admin/concurrency/catalog
        // validation, the D9 lockout check, and the RolePermission mutation) must commit as one
        // atomic unit under serializable isolation. Without this, two concurrent PUTs against
        // the same role (or against two different roles that each hold the last copy of
        // roles:admin/users:admin) could each read a pre-conflict state, both pass their checks,
        // and both commit -- silently overwriting each other or leaving zero admin coverage.
        // Mirrors the transaction pattern used for role deactivation in RoleManagementService
        // (#1448 review discussion).
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await _context.Database
            .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        Role? role = await _context.Roles
            .Include(r => r.RolePermissions)
                .ThenInclude(rp => rp.Resource)
            .Include(r => r.RolePermissions)
                .ThenInclude(rp => rp.Action)
            .FirstOrDefaultAsync(r => r.Id == roleId, cancellationToken)
            .ConfigureAwait(false);

        if (role is null)
        {
            return new RolePermissionUpdateResult.RoleNotFound();
        }

        if (string.Equals(role.Name, FarmAdminRoleName, StringComparison.Ordinal))
        {
            return new RolePermissionUpdateResult.FarmAdminImmutable();
        }

        if (role.UpdatedAt != request.UpdatedAt)
        {
            return new RolePermissionUpdateResult.ConcurrencyConflict();
        }

        PermissionCatalogDto catalog = await _permissionCatalogService.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, PermissionCatalogEntryDto> catalogByPermission = catalog.Resources
            .SelectMany(group => group.Permissions)
            .ToDictionary(entry => entry.Permission, StringComparer.Ordinal);

        List<string> requested = (request.Permissions ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        List<string> invalid = requested
            .Where(permission => !catalogByPermission.ContainsKey(permission))
            .ToList();
        if (invalid.Count > 0)
        {
            return new RolePermissionUpdateResult.InvalidPermissions(invalid);
        }

        var requestedSet = new HashSet<string>(requested, StringComparer.Ordinal);
        Dictionary<string, RolePermission> existingByPermission = role.RolePermissions
            .ToDictionary(rp => $"{rp.Resource.Name}:{rp.Action.Name}", StringComparer.Ordinal);

        // Full-replacement semantics only apply to permissions the client can actually see and
        // round-trip via GET, i.e. permissions present in the derived catalog. Grants such as
        // roles:admin/users:admin that are not yet catalog-enforced (FR-4) are invisible to the
        // client's request payload; a plain "replace everything not resubmitted" would silently
        // strip them just because the client never had a chance to include them.
        Dictionary<string, RolePermission> catalogVisibleExisting = existingByPermission
            .Where(kv => catalogByPermission.ContainsKey(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        List<string> currentlyGranted = catalogVisibleExisting
            .Where(kv => kv.Value.Granted)
            .Select(kv => kv.Key)
            .ToList();

        List<string> added = requestedSet.Except(currentlyGranted, StringComparer.Ordinal).ToList();
        List<string> removed = currentlyGranted.Except(requestedSet, StringComparer.Ordinal).ToList();

        if (removed.Count > 0)
        {
            List<string> violated = await FindLockoutViolationsAsync(roleId, removed, cancellationToken).ConfigureAwait(false);
            if (violated.Count > 0)
            {
                return new RolePermissionUpdateResult.LockoutViolation(violated);
            }
        }

        if (added.Count == 0 && removed.Count == 0)
        {
            Dictionary<string, bool> unchangedGrantStatus = existingByPermission
                .Where(kv => kv.Value.Granted)
                .ToDictionary(kv => kv.Key, _ => true, StringComparer.Ordinal);
            RolePermissionsDto unchangedDto = BuildDto(role, catalog, unchangedGrantStatus);
            return new RolePermissionUpdateResult.Success(new UpdateRolePermissionsResponseDto
            {
                Role = unchangedDto,
                RevokedSessionCount = 0,
            });
        }

        List<RolePermission> toRemove = catalogVisibleExisting.Values
            .Where(rp => !requestedSet.Contains($"{rp.Resource.Name}:{rp.Action.Name}", StringComparer.Ordinal))
            .ToList();
        _context.RolePermissions.RemoveRange(toRemove);

        DateTime now = DateTime.UtcNow;
        var toRemoveIds = new HashSet<Guid>(toRemove.Select(rp => rp.Id));
        foreach (string permission in requestedSet)
        {
            if (existingByPermission.TryGetValue(permission, out RolePermission? existing) && !toRemoveIds.Contains(existing.Id))
            {
                existing.Granted = true;
                continue;
            }

            PermissionCatalogEntryDto entry = catalogByPermission[permission];
            Resource resource = await _context.Resources
                .FirstAsync(r => r.Name == entry.Resource, cancellationToken)
                .ConfigureAwait(false);
            UserAction action = await _context.UserActions
                .FirstAsync(a => a.Name == entry.Action, cancellationToken)
                .ConfigureAwait(false);

            _context.RolePermissions.Add(new RolePermission
            {
                Id = Guid.NewGuid(),
                RoleId = role.Id,
                ResourceId = resource.Id,
                ActionId = action.Id,
                Granted = true,
                CreatedAt = now,
            });
        }

        role.UpdatedAt = now;

        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A concurrent transaction committed a conflicting change to this role (or to the
            // lockout-guarded permission rows) between our read and our write. Serializable
            // isolation surfaces this as a save/commit failure rather than silent corruption.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new RolePermissionUpdateResult.ConcurrencyConflict();
        }

        int revokedSessionCount = await RevokeSessionsForRoleAsync(roleId, actingUserId, role.Name, ipAddress, cancellationToken)
            .ConfigureAwait(false);

        await _authAuditService.LogRolePermissionsChangedAsync(
            actingUserId,
            role.Id,
            role.Name,
            added,
            removed,
            revokedSessionCount,
            ipAddress,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        Dictionary<string, bool> newGrantStatus = requestedSet.ToDictionary(p => p, _ => true, StringComparer.Ordinal);
        RolePermissionsDto dto = BuildDto(role, catalog, newGrantStatus);

        return new RolePermissionUpdateResult.Success(new UpdateRolePermissionsResponseDto
        {
            Role = dto,
            RevokedSessionCount = revokedSessionCount,
        });
    }

    /// <summary>
    /// D9: a permission removal must not strip the last <em>active</em> role holding a
    /// lockout-guarded permission (<c>roles:admin</c> / <c>users:admin</c>). Checked against
    /// raw <see cref="RolePermission"/> rows rather than the enforced catalog, since these two
    /// permissions are not yet gated by <c>[RequirePermission]</c> on any endpoint (FR-4,
    /// separate future work).
    /// </summary>
    private async Task<List<string>> FindLockoutViolationsAsync(Guid roleId, List<string> removedPermissions, CancellationToken cancellationToken)
    {
        List<string> violated = [];

        foreach ((string resourceName, string actionName) in LockoutGuardedPermissions)
        {
            string permissionKey = $"{resourceName}:{actionName}";
            if (!removedPermissions.Contains(permissionKey, StringComparer.Ordinal))
            {
                continue;
            }

            bool otherActiveRoleHolds = await _context.RolePermissions
                .AsNoTracking()
                .Where(rp => rp.RoleId != roleId
                    && rp.Granted
                    && rp.Role.IsActive
                    && rp.Resource.Name == resourceName
                    && rp.Action.Name == actionName)
                .AnyAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!otherActiveRoleHolds)
            {
                violated.Add(permissionKey);
            }
        }

        return violated;
    }

    private async Task<int> RevokeSessionsForRoleAsync(
        Guid roleId,
        Guid actingUserId,
        string roleName,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        DateTime now = DateTime.UtcNow;
        List<Guid> affectedUserIds = await _context.UserRoles
            .AsNoTracking()
            .Where(ur => ur.RoleId == roleId && ur.IsActive && (ur.ExpiresAt == null || ur.ExpiresAt > now))
            .Select(ur => ur.UserId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        int revokedUserCount = 0;
        foreach (Guid userId in affectedUserIds)
        {
            int revokedTokenCount = await _tokenRevocationService.RevokeAllUserTokensAsync(
                userId,
                actingUserId,
                $"Role '{roleName}' permissions changed",
                ipAddress,
                cancellationToken).ConfigureAwait(false);
            if (revokedTokenCount > 0)
            {
                revokedUserCount++;
            }
        }

        return revokedUserCount;
    }

    private async Task<Dictionary<string, bool>> LoadGrantStatusAsync(Guid roleId, CancellationToken cancellationToken)
    {
        return await _context.RolePermissions
            .AsNoTracking()
            .Where(rp => rp.RoleId == roleId)
            .Select(rp => new { Permission = rp.Resource.Name + ":" + rp.Action.Name, rp.Granted })
            .ToDictionaryAsync(x => x.Permission, x => x.Granted, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);
    }

    private static RolePermissionsDto BuildDto(Role role, PermissionCatalogDto catalog, Dictionary<string, bool> grantStatus)
    {
        List<RolePermissionResourceGroupDto> resourceGroups = catalog.Resources
            .Select(group => new RolePermissionResourceGroupDto
            {
                Resource = group.Resource,
                DisplayName = group.DisplayName,
                Description = group.Description,
                Permissions = group.Permissions.Select(entry => new RolePermissionEntryDto
                {
                    Resource = entry.Resource,
                    Action = entry.Action,
                    Permission = entry.Permission,
                    ActionDisplayName = entry.ActionDisplayName,
                    ActionDescription = entry.ActionDescription,
                    ImpliedByAdmin = entry.ImpliedByAdmin,
                    Status = grantStatus.TryGetValue(entry.Permission, out bool granted)
                        ? (granted ? RolePermissionGrantStatus.Granted : RolePermissionGrantStatus.Denied)
                        : RolePermissionGrantStatus.Absent,
                }).ToList(),
            })
            .ToList();

        return new RolePermissionsDto
        {
            RoleId = role.Id,
            RoleName = role.Name,
            RoleDisplayName = role.DisplayName,
            IsSystemRole = role.IsSystemRole,
            IsEditable = !string.Equals(role.Name, FarmAdminRoleName, StringComparison.Ordinal),
            UpdatedAt = role.UpdatedAt,
            Resources = resourceGroups,
        };
    }
}
