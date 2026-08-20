using System.Text.Json;
using System.Text.RegularExpressions;
using Farm.Infrastructure.Contracts.Roles;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Roles;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Services.Queue;

namespace Farm.Infrastructure.Services.Roles;

/// <inheritdoc cref="IRoleManagementService"/>
public partial class RoleManagementService(
    IRolesRepository roles,
    IAuthAuditService authAuditService,
    IQueueSubscriptionMembershipNotifier? membershipNotifier = null) : IRoleManagementService
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
            // Deduplicate resolved pairs: a caller-supplied duplicate permission string (or two
            // strings resolving to the same resource/action pair) would otherwise violate the
            // unique (RoleId, ResourceId, ActionId) index on the second SaveChangesAsync below,
            // leaving the role committed with an incomplete permission set and no audit entry.
            explicitPairs = new List<(Guid ResourceId, Guid ActionId)>();
            HashSet<(Guid ResourceId, Guid ActionId)> seen = [];
            foreach (string permission in request.Permissions)
            {
                (string resource, string action) = PrintFarmerPermissions.Split(permission);
                (Guid ResourceId, Guid ActionId)? resolved = await _roles.ResolvePermissionAsync(resource, action, ct);
                if (resolved is null)
                {
                    throw new RoleManagementException(RoleManagementErrorCode.InvalidPermission, $"Unknown permission '{permission}'.");
                }

                if (seen.Add(resolved.Value))
                {
                    explicitPairs.Add(resolved.Value);
                }
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

        try
        {
            await _roles.SaveChangesAsync(ct);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
        {
            // The app-level uniqueness check in ValidateNewNameAsync is not itself atomic with
            // this insert: two concurrent creates for the same name can both pass validation,
            // and only the database's unique index on Role.Name ultimately rejects the loser.
            // Translate that into the same domain error a non-concurrent duplicate would get,
            // instead of letting a raw DbUpdateException bubble up as an unhandled 500.
            if (await _roles.NameExistsAsync(name, excludeRoleId: null, ct))
            {
                throw new RoleManagementException(
                    RoleManagementErrorCode.InvalidName,
                    $"A role named '{name}' already exists.",
                    ex);
            }

            throw;
        }

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

        // Whether this request *might* deactivate the role, based on the caller's intent alone
        // (not yet on role.IsActive, which may be stale by the time a transaction opens below).
        bool requestsDeactivation = request.IsActive is false;

        // The D9 guardrail check and the deactivation it protects must commit as one atomic
        // unit under serializable isolation: otherwise two concurrent requests could each pass
        // the check against a different admin-equivalent role and both commit, leaving zero
        // admin coverage. See issue #1448 review discussion.
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = requestsDeactivation
            ? await _roles.BeginSerializableTransactionAsync(ct)
            : null;
        RoleDetailDto? before;
        try
        {
            // Reload role.IsActive inside the transaction: the entity above was loaded
            // before the transaction started, so it may be stale relative to the
            // transaction's consistent serializable snapshot (e.g. concurrently
            // reactivated/deactivated by another request in the meantime).
            if (requestsDeactivation && !await _roles.ReloadRoleAsync(role, ct))
            {
                throw new RoleManagementException(RoleManagementErrorCode.NotFound, $"Role {roleId} was not found.");
            }

            // Captured after the (optional) reload above, so the audit trail's "before" state
            // reflects what was actually about to be overwritten rather than a value read
            // before this request's consistency scope began. See issue #1448 review discussion.
            before = await _roles.GetRoleDetailAsync(roleId, ct);

            bool wantsDeactivation = requestsDeactivation && role.IsActive;

            // D6 — system roles cannot be renamed (checked above), deleted, or deactivated.
            // DisplayName/Description remain editable for system roles.
            if (role.IsSystemRole && wantsDeactivation)
            {
                throw new RoleManagementException(RoleManagementErrorCode.SystemRoleProtected, $"System role '{role.Name}' cannot be deactivated.");
            }

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

        // D8/D9 — the member count, reassignment-target validation and admin-equivalence, the
        // lockout guardrail, and the delete/reassign/cascade mutation it protects must all
        // execute against one consistent snapshot under serializable isolation. Reading any of
        // this state before the transaction starts would leave a window where a concurrent
        // request changes membership or the reassignment target's state between the read and
        // the transaction, invalidating the decision made from stale data. See issue #1448
        // review discussion.
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await _roles.BeginSerializableTransactionAsync(ct);
        RoleDetailDto? before;
        bool memberCountMutated = false;
        try
        {
            // Reload role.IsActive inside the transaction: the entity above was loaded before
            // the transaction started, so it may be stale relative to the transaction's
            // consistent serializable snapshot (e.g. concurrently reactivated after being read
            // as inactive, which would otherwise let this delete skip the guardrail entirely).
            if (!await _roles.ReloadRoleAsync(role, ct))
            {
                throw new RoleManagementException(RoleManagementErrorCode.NotFound, $"Role {roleId} was not found.");
            }

            // Captured after the reload above, so the audit trail's "before" state reflects
            // what was actually about to be deleted rather than a value read before this
            // request's consistency scope began. See issue #1448 review discussion.
            before = await _roles.GetRoleDetailAsync(roleId, ct);

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

            if (role.IsActive)
            {
                await EnsureDeactivationDoesNotLockOutAdminsAsync(role, actorUserId, membersRetainAdminAccessViaReassignment, ct);
            }

            if (memberCount > 0)
            {
                if (targetRole is not null)
                {
                    await _roles.ReassignMembersAsync(roleId, targetRole.Id, ct);
                    memberCountMutated = true;
                }
                else if (cascade)
                {
                    await _roles.RemoveMembersAsync(roleId, ct);
                    memberCountMutated = true;
                }
                else
                {
                    throw new RoleManagementException(
                        RoleManagementErrorCode.HasMembers,
                        $"Role '{role.Name}' has {memberCount} member(s). Pass reassignTo={{roleId}} or cascade=true to proceed.")
                    {
                        MemberCount = memberCount
                    };
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

        // #1731: reassigning or removing every member of a deleted role changes which
        // printers/groups those users are authorized to see, exactly like a single-user
        // role change does -- notify whenever members were actually mutated.
        if (memberCountMutated && membershipNotifier is not null)
        {
            await membershipNotifier.NotifyMembershipChangedAsync(ct);
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
