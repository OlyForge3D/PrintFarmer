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

        // Resolve/validate everything that can fail *before* adding the role, so a bad
        // CopyFromRoleId or an unknown permission never leaves a half-created, permission-less
        // role committed to the database.
        Guid? copyFromRoleId = null;
        List<(Guid ResourceId, Guid ActionId)>? explicitPairs = null;

        if (request.CopyFromRoleId is { } sourceRoleId)
        {
            Role? sourceRole = await _roles.GetRoleEntityAsync(sourceRoleId, ct);
            if (sourceRole is null)
            {
                throw new RoleManagementException(RoleManagementErrorCode.InvalidPermission, $"Source role {sourceRoleId} for CopyFromRoleId does not exist.");
            }

            copyFromRoleId = sourceRoleId;
        }
        else if (request.Permissions is { Count: > 0 })
        {
            explicitPairs = new List<(Guid ResourceId, Guid ActionId)>();
            foreach (string permission in request.Permissions)
            {
                (string resource, string action) = PrintFarmerPermissions.Split(permission);
                (Guid ResourceId, Guid ActionId)? resolved = await _roles.ResolvePermissionAsync(resource, action, ct);
                if (resolved is null)
                {
                    throw new RoleManagementException(RoleManagementErrorCode.InvalidPermission, $"Unknown permission '{permission}'.");
                }

                explicitPairs.Add(resolved.Value);
            }
        }

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

        if (copyFromRoleId is { } source)
        {
            await _roles.CopyRolePermissionsAsync(source, role.Id, ct);
        }
        else if (explicitPairs is { Count: > 0 })
        {
            await _roles.AddRolePermissionsAsync(role.Id, explicitPairs, ct);
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

        // The D9 guardrail check and the deactivation it protects must commit as one atomic
        // unit under serializable isolation: otherwise two concurrent requests could each pass
        // the check against a different admin-equivalent role and both commit, leaving zero
        // admin coverage. See issue #1448 review discussion.
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = wantsDeactivation
            ? await _roles.BeginSerializableTransactionAsync(ct)
            : null;
        try
        {
            if (wantsDeactivation)
            {
                await EnsureDeactivationDoesNotLockOutAdminsAsync(role, actorUserId, membersRetainAdminAccessViaReassignment: false, ct);
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

            try
            {
                await _roles.SaveChangesAsync(ct);
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (transaction is not null)
            {
                throw new RoleManagementException(
                    RoleManagementErrorCode.ConcurrencyConflict,
                    "Another request changed admin role coverage concurrently. Re-check the current state and retry.",
                    ex);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }

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

        // D8 — deletion is never a silent orphan: a role with members requires either an
        // explicit reassignment target or an explicit cascade opt-in. Resolve/validate the
        // reassignment target up front (before the D9 guardrail) so the guardrail can tell
        // whether members — including a self-lockout-risking actor — will retain
        // admin-equivalent access via the target role rather than lose it outright.
        int memberCount = await _roles.CountActiveMembersAsync(roleId, ct);
        Role? targetRole = null;
        if (memberCount > 0 && reassignToRoleId is { } targetRoleId)
        {
            if (targetRoleId == roleId)
            {
                throw new RoleManagementException(RoleManagementErrorCode.InvalidReassignmentTarget, "Cannot reassign a role's members to itself.");
            }

            targetRole = await _roles.GetRoleEntityAsync(targetRoleId, ct);
            if (targetRole is null || !targetRole.IsActive)
            {
                throw new RoleManagementException(RoleManagementErrorCode.InvalidReassignmentTarget, $"Reassignment target role {targetRoleId} does not exist or is inactive.");
            }
        }

        bool membersRetainAdminAccessViaReassignment = targetRole is not null && await _roles.IsAdminEquivalentAsync(targetRole.Id, ct);

        // The D9 guardrail check and the delete/reassign/cascade it protects must commit as one
        // atomic unit under serializable isolation: otherwise two concurrent requests could each
        // pass the check against a different admin-equivalent role and both commit, leaving zero
        // admin coverage. See issue #1448 review discussion.
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await _roles.BeginSerializableTransactionAsync(ct);
        try
        {
            if (role.IsActive)
            {
                await EnsureDeactivationDoesNotLockOutAdminsAsync(role, actorUserId, membersRetainAdminAccessViaReassignment, ct);
            }

            if (memberCount > 0)
            {
                if (targetRole is not null)
                {
                    await _roles.ReassignMembersAsync(roleId, targetRole.Id, ct);
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

            try
            {
                await _roles.SaveChangesAsync(ct);
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
            {
                throw new RoleManagementException(
                    RoleManagementErrorCode.ConcurrencyConflict,
                    "Another request changed admin role coverage concurrently. Re-check the current state and retry.",
                    ex);
            }

            await transaction.CommitAsync(ct);
        }
        finally
        {
            await transaction.DisposeAsync();
        }

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
    /// administrator of their own last administrative role. <paramref name="membersRetainAdminAccessViaReassignment"/>
    /// is true when the role's members (including a potential self-lockout-risking actor) are
    /// being reassigned to another active, admin-equivalent role as part of the same operation,
    /// in which case they don't actually lose admin-equivalent access and the guardrail is moot.
    /// </summary>
    private async Task EnsureDeactivationDoesNotLockOutAdminsAsync(Role role, Guid actorUserId, bool membersRetainAdminAccessViaReassignment, CancellationToken ct)
    {
        bool isAdminEquivalent = await _roles.IsAdminEquivalentAsync(role.Id, ct);
        if (!isAdminEquivalent || membersRetainAdminAccessViaReassignment)
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
