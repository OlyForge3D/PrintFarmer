using System.Text.Json;
using System.Text.RegularExpressions;
using Farm.Infrastructure.Contracts.Roles;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Roles;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Authentication;

namespace Farm.Infrastructure.Services.Roles;

/// <inheritdoc cref="IRoleManagementService"/>
public partial class RoleManagementService(
    IRolesRepository roles,
    IAuthAuditService authAuditService) : IRoleManagementService
{
    private readonly IRolesRepository _roles = roles ?? throw new ArgumentNullException(nameof(roles));
    private readonly IAuthAuditService _authAuditService = authAuditService ?? throw new ArgumentNullException(nameof(authAuditService));

    private const string ReservedSystemPrefix = "farm_";

    [GeneratedRegex("^[a-z][a-z0-9_]{2,49}$")]
    private static partial Regex NameSlugRegex();

    public async Task<IReadOnlyList<RoleSummaryDto>> GetRolesAsync(CancellationToken ct = default)
    {
        return await _roles.GetRoleSummariesAsync(ct);
    }

    public Task<RoleDetailDto?> GetRoleAsync(Guid roleId, CancellationToken ct = default)
    {
        return _roles.GetRoleDetailAsync(roleId, ct);
    }

    public async Task<RoleDetailDto> CreateRoleAsync(CreateCustomRoleRequest request, Guid actorUserId, string? ipAddress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string name = (request.Name ?? string.Empty).Trim().ToLowerInvariant();
        await ValidateNewNameAsync(name, ct);

        Role role = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = (request.DisplayName ?? string.Empty).Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            IsSystemRole = false,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _roles.AddRoleAsync(role, ct);
        await _roles.SaveChangesAsync(ct);

        if (request.CopyFromRoleId is { } sourceRoleId)
        {
            Role? sourceRole = await _roles.GetRoleEntityAsync(sourceRoleId, ct);
            if (sourceRole is null)
            {
                throw new RoleManagementException(RoleManagementErrorCode.InvalidPermission, $"Source role {sourceRoleId} for CopyFromRoleId does not exist.");
            }

            await _roles.CopyRolePermissionsAsync(sourceRoleId, role.Id, ct);
        }
        else if (request.Permissions is { Count: > 0 })
        {
            List<(Guid ResourceId, Guid ActionId)> pairs = new();
            foreach (string permission in request.Permissions)
            {
                (string resource, string action) = PrintFarmerPermissions.Split(permission);
                (Guid ResourceId, Guid ActionId)? resolved = await _roles.ResolvePermissionAsync(resource, action, ct);
                if (resolved is null)
                {
                    throw new RoleManagementException(RoleManagementErrorCode.InvalidPermission, $"Unknown permission '{permission}'.");
                }

                pairs.Add(resolved.Value);
            }

            await _roles.AddRolePermissionsAsync(role.Id, pairs, ct);
        }

        await _roles.SaveChangesAsync(ct);

        RoleDetailDto? created = await _roles.GetRoleDetailAsync(role.Id, ct);

        await _authAuditService.LogRoleManagementEventAsync(
            actorUserId,
            role.Id,
            role.Name,
            AuthEventType.RoleCreated,
            beforeJson: null,
            afterJson: JsonSerializer.Serialize(created),
            ipAddress,
            cancellationToken: ct);

        return created!;
    }

    public async Task<RoleDetailDto> UpdateRoleAsync(Guid roleId, UpdateCustomRoleRequest request, Guid actorUserId, string? ipAddress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Role? role = await _roles.GetRoleEntityAsync(roleId, ct);
        if (role is null)
        {
            throw new RoleManagementException(RoleManagementErrorCode.NotFound, $"Role {roleId} was not found.");
        }

        // D7 — names are immutable once created. A caller that echoes the DTO back with the
        // same name is fine; any actual change is rejected.
        if (!string.IsNullOrWhiteSpace(request.Name) &&
            !string.Equals(request.Name.Trim(), role.Name, StringComparison.Ordinal))
        {
            throw new RoleManagementException(RoleManagementErrorCode.NameIsImmutable, "Role name is immutable and cannot be changed after creation.");
        }

        RoleDetailDto? before = await _roles.GetRoleDetailAsync(roleId, ct);

        bool wantsDeactivation = request.IsActive is false && role.IsActive;

        // D6 — system roles cannot be renamed (checked above), deleted, or deactivated.
        // DisplayName/Description remain editable for system roles.
        if (role.IsSystemRole && wantsDeactivation)
        {
            throw new RoleManagementException(RoleManagementErrorCode.SystemRoleProtected, $"System role '{role.Name}' cannot be deactivated.");
        }

        if (wantsDeactivation)
        {
            await EnsureDeactivationDoesNotLockOutAdminsAsync(role, actorUserId, ct);
        }

        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            role.DisplayName = request.DisplayName.Trim();
        }

        if (request.Description is not null)
        {
            role.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        }

        if (request.IsActive.HasValue)
        {
            role.IsActive = request.IsActive.Value;
        }

        role.UpdatedAt = DateTime.UtcNow;
        await _roles.SaveChangesAsync(ct);

        RoleDetailDto? after = await _roles.GetRoleDetailAsync(roleId, ct);

        await _authAuditService.LogRoleManagementEventAsync(
            actorUserId,
            roleId,
            role.Name,
            AuthEventType.RoleUpdated,
            beforeJson: JsonSerializer.Serialize(before),
            afterJson: JsonSerializer.Serialize(after),
            ipAddress,
            cancellationToken: ct);

        return after!;
    }

    public async Task DeleteRoleAsync(Guid roleId, Guid? reassignToRoleId, bool cascade, Guid actorUserId, string? ipAddress, CancellationToken ct = default)
    {
        Role? role = await _roles.GetRoleEntityAsync(roleId, ct);
        if (role is null)
        {
            throw new RoleManagementException(RoleManagementErrorCode.NotFound, $"Role {roleId} was not found.");
        }

        // D6 — system roles are permanently protected from deletion.
        if (role.IsSystemRole)
        {
            throw new RoleManagementException(RoleManagementErrorCode.SystemRoleProtected, $"System role '{role.Name}' cannot be deleted.");
        }

        RoleDetailDto? before = await _roles.GetRoleDetailAsync(roleId, ct);

        // D9 — evaluate the lockout guardrails against the role's current state, before any
        // members are moved or removed.
        if (role.IsActive)
        {
            await EnsureDeactivationDoesNotLockOutAdminsAsync(role, actorUserId, ct);
        }

        // D8 — deletion is never a silent orphan: a role with members requires either an
        // explicit reassignment target or an explicit cascade opt-in.
        int memberCount = await _roles.CountActiveMembersAsync(roleId, ct);
        if (memberCount > 0)
        {
            if (reassignToRoleId is { } targetRoleId)
            {
                if (targetRoleId == roleId)
                {
                    throw new RoleManagementException(RoleManagementErrorCode.InvalidReassignmentTarget, "Cannot reassign a role's members to itself.");
                }

                Role? targetRole = await _roles.GetRoleEntityAsync(targetRoleId, ct);
                if (targetRole is null || !targetRole.IsActive)
                {
                    throw new RoleManagementException(RoleManagementErrorCode.InvalidReassignmentTarget, $"Reassignment target role {targetRoleId} does not exist or is inactive.");
                }

                await _roles.ReassignMembersAsync(roleId, targetRoleId, ct);
            }
            else if (cascade)
            {
                await _roles.RemoveMembersAsync(roleId, ct);
            }
            else
            {
                throw new RoleManagementException(
                    RoleManagementErrorCode.HasMembers,
                    $"Role '{role.Name}' has {memberCount} member(s). Pass reassignTo={{roleId}} or cascade=true to proceed.");
            }
        }

        await _roles.DeleteRoleAsync(role, ct);
        await _roles.SaveChangesAsync(ct);

        await _authAuditService.LogRoleManagementEventAsync(
            actorUserId,
            roleId,
            role.Name,
            AuthEventType.RoleDeleted,
            beforeJson: JsonSerializer.Serialize(before),
            afterJson: null,
            ipAddress,
            cancellationToken: ct);
    }

    /// <summary>
    /// D9 — refuses a deactivation/deletion that would leave the system with no active,
    /// admin-equivalent role held by any active user, or that would strip the acting
    /// administrator of their own last administrative role.
    /// </summary>
    private async Task EnsureDeactivationDoesNotLockOutAdminsAsync(Role role, Guid actorUserId, CancellationToken ct)
    {
        bool isAdminEquivalent = await _roles.IsAdminEquivalentAsync(role.Id, ct);
        if (!isAdminEquivalent)
        {
            return;
        }

        bool actorIsMember = await _roles.UserIsActiveMemberOfRoleAsync(actorUserId, role.Id, ct);
        if (actorIsMember)
        {
            bool actorHasOtherAdminRole = await _roles.UserHasOtherActiveAdminEquivalentRoleAsync(actorUserId, role.Id, ct);
            if (!actorHasOtherAdminRole)
            {
                throw new RoleManagementException(
                    RoleManagementErrorCode.SelfLockout,
                    $"Cannot remove your own last administrative role ('{role.Name}'). Assign yourself another admin-equivalent role first.");
            }
        }

        bool otherAdminCoverageExists = await _roles.HasOtherActiveAdminCoverageAsync(role.Id, ct);
        if (!otherAdminCoverageExists)
        {
            throw new RoleManagementException(
                RoleManagementErrorCode.LastAdminRole,
                $"Role '{role.Name}' is the last active role granting roles:admin and users:admin to an active user. At least one must remain.");
        }
    }

    private async Task ValidateNewNameAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || !NameSlugRegex().IsMatch(name))
        {
            throw new RoleManagementException(
                RoleManagementErrorCode.InvalidName,
                "Role name must match ^[a-z][a-z0-9_]{2,49}$ (lowercase letters, digits, underscores; 3-50 characters, starting with a letter).");
        }

        if (name.StartsWith(ReservedSystemPrefix, StringComparison.Ordinal))
        {
            throw new RoleManagementException(
                RoleManagementErrorCode.InvalidName,
                $"Role names cannot use the reserved '{ReservedSystemPrefix}' prefix.");
        }

        if (await _roles.NameExistsAsync(name, excludeRoleId: null, ct))
        {
            throw new RoleManagementException(
                RoleManagementErrorCode.InvalidName,
                $"A role named '{name}' already exists.");
        }
    }
}
