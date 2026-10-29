// <copyright file="QueueResourceAuthorizationService.cs" company="PlaceholderCompany">
// SPDX-License-Identifier: AGPL-3.0-only
// </copyright>

using System.Security.Claims;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>Resource-scoped authorization for queue jobs, printers, and projects.</summary>
public interface IQueueResourceAuthorizationService
{
    Task<bool> CanAccessJobAsync(
        ClaimsPrincipal principal,
        Guid jobId,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct = default);

    Task<bool> CanAccessPrinterAsync(
        ClaimsPrincipal principal,
        Guid printerId,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct = default);

    Task<bool> CanAccessProjectAsync(
        ClaimsPrincipal principal,
        Guid projectId,
        CancellationToken ct = default);

    Task<bool> CanActorAccessJobAsync(
        string actorSubject,
        Guid jobId,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct = default);

    Task<bool> CanActorAccessPrinterAsync(
        string actorSubject,
        Guid printerId,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct = default);

    Task<bool> CanActorAccessGcodeFileAsync(
        string actorSubject,
        Guid gcodeFileId,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct = default);

    Task<bool> CanActorAccessProjectAsync(
        string actorSubject,
        Guid projectId,
        CancellationToken ct = default);

    Task<IReadOnlySet<Guid>> FilterActorAccessibleJobIdsAsync(
        string actorSubject,
        IReadOnlyCollection<Guid> jobIds,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct = default);

    /// <summary>
    /// Set-based equivalent of <see cref="CanActorAccessPrinterAsync"/>: returns exactly the
    /// candidate IDs for which that method would return <see langword="true"/>, using a bounded
    /// number of queries independent of the candidate count.
    /// </summary>
    Task<IReadOnlySet<Guid>> FilterActorAccessiblePrinterIdsAsync(
        string actorSubject,
        IReadOnlyCollection<Guid> printerIds,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct = default);

    /// <summary>
    /// Set-based equivalent of <see cref="CanActorAccessGcodeFileAsync"/>: returns exactly the
    /// candidate IDs for which that method would return <see langword="true"/>, using a bounded
    /// number of queries independent of the candidate count.
    /// </summary>
    Task<IReadOnlySet<Guid>> FilterActorAccessibleGcodeFileIdsAsync(
        string actorSubject,
        IReadOnlyCollection<Guid> gcodeFileIds,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct = default);

    /// <summary>
    /// Filters a candidate set of printer IDs down to the ones the principal may access at the
    /// given <see cref="PrinterGroupAccessLevel"/>, applying the same PrinterGroup rules as
    /// <see cref="CanAccessPrinterAsync"/> in a single batched query. Used to scope collection
    /// reads (printer list, summary, camera-urls) so restricted printers are not enumerable.
    /// </summary>
    Task<IReadOnlySet<Guid>> FilterAccessiblePrinterIdsAsync(
        ClaimsPrincipal principal,
        IReadOnlyCollection<Guid> printerIds,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct = default);

    /// <summary>
    /// Counts the printers the principal may access at the given
    /// <see cref="PrinterGroupAccessLevel"/>, applying the same enabled-only visibility and
    /// PrinterGroup ACL rules as <see cref="FilterAccessiblePrinterIdsAsync"/> and the default
    /// (non-admin) view of <c>GET /api/printers</c>. Resolved with a single repository-level
    /// <c>COUNT(*)</c> rather than materializing the printer list (issue #2411). Farm admins are
    /// counted against every printer (enabled or not), matching their unfiltered default view of
    /// that same endpoint.
    /// </summary>
    Task<int> CountAccessiblePrintersAsync(
        ClaimsPrincipal principal,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct = default);
}

/// <summary>
/// Evaluates ownership and printer-group ACLs from the database. Only explicitly allowlisted
/// durable system actors bypass user resource checks.
/// </summary>
public sealed class QueueResourceAuthorizationService(AppDbContext db)
    : IQueueResourceAuthorizationService
{
    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public Task<bool> CanAccessJobAsync(
        ClaimsPrincipal principal,
        Guid jobId,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (PrintFarmerPermissions.IsFarmAdmin(principal))
        {
            return Task.FromResult(true);
        }

        return PrintFarmerPermissions.TryGetUserId(principal, out Guid userId)
            ? CanUserAccessJobAsync(userId, jobId, minimumAccess, ct)
            : Task.FromResult(false);
    }

    public Task<bool> CanAccessPrinterAsync(
        ClaimsPrincipal principal,
        Guid printerId,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (PrintFarmerPermissions.IsFarmAdmin(principal))
        {
            return Task.FromResult(true);
        }

        return PrintFarmerPermissions.TryGetUserId(principal, out Guid userId)
            ? CanUserAccessPrinterAsync(userId, printerId, minimumAccess, ct)
            : Task.FromResult(false);
    }

    public async Task<bool> CanAccessProjectAsync(
        ClaimsPrincipal principal,
        Guid projectId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (PrintFarmerPermissions.IsFarmAdmin(principal))
        {
            return true;
        }

        return PrintFarmerPermissions.TryGetUserId(principal, out Guid userId) &&
            await _db.CalibrationProjects
                .AsNoTracking()
                .AnyAsync(
                    project => project.Id == projectId && project.OwnerUserId == userId,
                    ct);
    }

    public async Task<bool> CanActorAccessJobAsync(
        string actorSubject,
        Guid jobId,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct = default)
    {
        if (QueueActorIdentity.IsTrustedSystemActor(actorSubject))
        {
            return true;
        }

        return Guid.TryParse(actorSubject, out Guid userId) &&
            await CanUserAccessJobAsync(userId, jobId, minimumAccess, ct);
    }

    public async Task<bool> CanActorAccessPrinterAsync(
        string actorSubject,
        Guid printerId,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct = default)
    {
        if (QueueActorIdentity.IsTrustedSystemActor(actorSubject))
        {
            return true;
        }

        return Guid.TryParse(actorSubject, out Guid userId) &&
            (await IsFarmAdminAsync(userId, ct) ||
             await CanUserAccessPrinterAsync(userId, printerId, minimumAccess, ct));
    }

    public async Task<bool> CanActorAccessProjectAsync(
        string actorSubject,
        Guid projectId,
        CancellationToken ct = default)
    {
        if (QueueActorIdentity.IsTrustedSystemActor(actorSubject))
        {
            return true;
        }

        return Guid.TryParse(actorSubject, out Guid userId) &&
            (await IsFarmAdminAsync(userId, ct) ||
             await _db.CalibrationProjects
                 .AsNoTracking()
                 .AnyAsync(
                     project =>
                         project.Id == projectId &&
                         project.OwnerUserId == userId,
                     ct));
    }

    public async Task<bool> CanActorAccessGcodeFileAsync(
        string actorSubject,
        Guid gcodeFileId,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(actorSubject, out Guid userId))
        {
            return false;
        }

        var file = await _db.GcodeFiles.AsNoTracking().Where(x => x.Id == gcodeFileId)
            .Select(x => new { x.PrinterGroupId, x.CalibrationProjectId }).SingleOrDefaultAsync(ct);
        return file is not null &&
            (await IsFarmAdminAsync(userId, ct) ||
             ((!file.PrinterGroupId.HasValue ||
               await CanUserAccessGroupAsync(userId, file.PrinterGroupId.Value, minimumAccess, ct)) &&
              (!file.CalibrationProjectId.HasValue ||
               await CanActorAccessProjectAsync(actorSubject, file.CalibrationProjectId.Value, ct))));
    }

    public async Task<IReadOnlySet<Guid>> FilterActorAccessibleJobIdsAsync(
        string actorSubject,
        IReadOnlyCollection<Guid> jobIds,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct = default)
    {
        if (jobIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        Guid[] distinctJobIds = jobIds.Distinct().ToArray();
        if (QueueActorIdentity.IsTrustedSystemActor(actorSubject))
        {
            return distinctJobIds.ToHashSet();
        }

        if (!Guid.TryParse(actorSubject, out Guid userId))
        {
            return new HashSet<Guid>();
        }

        if (await IsFarmAdminAsync(userId, ct))
        {
            return distinctJobIds.ToHashSet();
        }

        List<JobAccessScope> scopes = await _db.PrintJobs
            .AsNoTracking()
            .Where(job => distinctJobIds.Contains(job.Id))
            .Select(job => new JobAccessScope(
                job.Id,
                job.CreatorSubject,
                job.CalibrationProjectId,
                job.AssignedPrinter == null ? null : job.AssignedPrinter.PrinterGroupId,
                job.GcodeFile == null ? null : job.GcodeFile.PrinterGroupId))
            .ToListAsync(ct);
        Guid[] groupIds = scopes
            .SelectMany(scope => new[] { scope.PrinterGroupId, scope.GcodeGroupId })
            .Where(groupId => groupId.HasValue)
            .Select(groupId => groupId!.Value)
            .Distinct()
            .ToArray();
        List<PrinterGroupAccess> rules = groupIds.Length == 0
            ? []
            : await _db.PrinterGroupAccesses
                .AsNoTracking()
                .Where(rule => groupIds.Contains(rule.PrinterGroupId))
                .ToListAsync(ct);
        HashSet<Guid> userRoles = (await _db.UserRoles
                .AsNoTracking()
                .Where(role => role.UserId == userId && role.IsActive)
                .Select(role => role.RoleId)
                .ToListAsync(ct))
            .ToHashSet();
        Guid[] projectIds = scopes
            .Where(scope => scope.CalibrationProjectId.HasValue)
            .Select(scope => scope.CalibrationProjectId!.Value)
            .Distinct()
            .ToArray();
        HashSet<Guid> ownedProjects = (await _db.CalibrationProjects
                .AsNoTracking()
                .Where(project =>
                     project.OwnerUserId == userId &&
                     projectIds.Contains(project.Id))
                .Select(project => project.Id)
                .ToListAsync(ct))
            .ToHashSet();
        Dictionary<Guid, List<PrinterGroupAccess>> rulesByGroup = rules
            .GroupBy(rule => rule.PrinterGroupId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var allowed = new HashSet<Guid>();
        foreach (JobAccessScope scope in scopes)
        {
            if (scope.CalibrationProjectId is Guid projectId &&
                !ownedProjects.Contains(projectId))
            {
                continue;
            }

            Guid[] scopedGroups = new[] { scope.PrinterGroupId, scope.GcodeGroupId }
                .Where(groupId => groupId.HasValue)
                .Select(groupId => groupId!.Value)
                .Distinct()
                .ToArray();
            bool groupsAllowed = scopedGroups.All(groupId =>
                !rulesByGroup.TryGetValue(groupId, out List<PrinterGroupAccess>? groupRules) ||
                groupRules.Count == 0 ||
                groupRules.Any(rule =>
                     userRoles.Contains(rule.RoleId) &&
                     rule.AccessLevel >= minimumAccess));
            if (!groupsAllowed)
            {
                continue;
            }

            bool creatorAllowed =
                scope.CalibrationProjectId.HasValue ||
                !Guid.TryParse(scope.CreatorSubject, out Guid creatorId) ||
                creatorId == userId ||
                scopedGroups.Length > 0;
            if (creatorAllowed)
            {
                _ = allowed.Add(scope.JobId);
            }
        }

        return allowed;
    }

    private async Task<bool> CanUserAccessJobAsync(
        Guid userId,
        Guid jobId,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct)
    {
        IReadOnlySet<Guid> allowed = await FilterActorAccessibleJobIdsAsync(
            userId.ToString(),
            [jobId],
            minimumAccess,
            ct);
        return allowed.Contains(jobId);
    }

    public async Task<IReadOnlySet<Guid>> FilterActorAccessiblePrinterIdsAsync(
        string actorSubject,
        IReadOnlyCollection<Guid> printerIds,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(printerIds);
        if (printerIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        Guid[] distinctPrinterIds = printerIds.Distinct().ToArray();
        if (QueueActorIdentity.IsTrustedSystemActor(actorSubject))
        {
            return distinctPrinterIds.ToHashSet();
        }

        if (!Guid.TryParse(actorSubject, out Guid userId))
        {
            return new HashSet<Guid>();
        }

        // Farm admins are allowed regardless of existence, matching the single-printer check.
        if (await IsFarmAdminAsync(userId, ct))
        {
            return distinctPrinterIds.ToHashSet();
        }

        List<PrinterGroupScope> scopes = await _db.Printers
            .AsNoTracking()
            .Where(printer => distinctPrinterIds.Contains(printer.Id))
            .Select(printer => new PrinterGroupScope(printer.Id, printer.PrinterGroupId))
            .ToListAsync(ct);
        GroupAccessSnapshot groups = await LoadGroupAccessAsync(
            userId,
            scopes.Select(scope => scope.PrinterGroupId),
            ct);
        return scopes
            .Where(scope => groups.Allows(scope.PrinterGroupId, minimumAccess))
            .Select(scope => scope.PrinterId)
            .ToHashSet();
    }

    public async Task<IReadOnlySet<Guid>> FilterActorAccessibleGcodeFileIdsAsync(
        string actorSubject,
        IReadOnlyCollection<Guid> gcodeFileIds,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gcodeFileIds);
        if (gcodeFileIds.Count == 0 || !Guid.TryParse(actorSubject, out Guid userId))
        {
            return new HashSet<Guid>();
        }

        Guid[] distinctFileIds = gcodeFileIds.Distinct().ToArray();
        List<GcodeFileScope> files = await _db.GcodeFiles
            .AsNoTracking()
            .Where(file => distinctFileIds.Contains(file.Id))
            .Select(file => new GcodeFileScope(file.Id, file.PrinterGroupId, file.CalibrationProjectId))
            .ToListAsync(ct);
        if (files.Count == 0)
        {
            return new HashSet<Guid>();
        }

        if (await IsFarmAdminAsync(userId, ct))
        {
            return files.Select(file => file.FileId).ToHashSet();
        }

        GroupAccessSnapshot groups = await LoadGroupAccessAsync(
            userId,
            files.Select(file => file.PrinterGroupId),
            ct);
        Guid[] projectIds = files
            .Where(file => file.CalibrationProjectId.HasValue)
            .Select(file => file.CalibrationProjectId!.Value)
            .Distinct()
            .ToArray();
        HashSet<Guid> ownedProjects = projectIds.Length == 0
            ? []
            : (await _db.CalibrationProjects
                .AsNoTracking()
                .Where(project => projectIds.Contains(project.Id) && project.OwnerUserId == userId)
                .Select(project => project.Id)
                .ToListAsync(ct))
            .ToHashSet();
        return files
            .Where(file =>
                groups.Allows(file.PrinterGroupId, minimumAccess) &&
                (!file.CalibrationProjectId.HasValue ||
                 ownedProjects.Contains(file.CalibrationProjectId.Value)))
            .Select(file => file.FileId)
            .ToHashSet();
    }

    public async Task<IReadOnlySet<Guid>> FilterAccessiblePrinterIdsAsync(
        ClaimsPrincipal principal,
        IReadOnlyCollection<Guid> printerIds,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (printerIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        Guid[] distinctPrinterIds = printerIds.Distinct().ToArray();
        if (PrintFarmerPermissions.IsFarmAdmin(principal))
        {
            return distinctPrinterIds.ToHashSet();
        }

        if (!PrintFarmerPermissions.TryGetUserId(principal, out Guid userId))
        {
            return new HashSet<Guid>();
        }

        List<PrinterGroupScope> scopes = await _db.Printers
            .AsNoTracking()
            .Where(printer => distinctPrinterIds.Contains(printer.Id))
            .Select(printer => new PrinterGroupScope(printer.Id, printer.PrinterGroupId))
            .ToListAsync(ct);
        Guid[] groupIds = scopes
            .Where(scope => scope.PrinterGroupId.HasValue)
            .Select(scope => scope.PrinterGroupId!.Value)
            .Distinct()
            .ToArray();
        List<PrinterGroupAccess> rules = groupIds.Length == 0
            ? []
            : await _db.PrinterGroupAccesses
                .AsNoTracking()
                .Where(rule => groupIds.Contains(rule.PrinterGroupId))
                .ToListAsync(ct);
        HashSet<Guid> userRoles = (await _db.UserRoles
                .AsNoTracking()
                .Where(role => role.UserId == userId && role.IsActive)
                .Select(role => role.RoleId)
                .ToListAsync(ct))
            .ToHashSet();
        Dictionary<Guid, List<PrinterGroupAccess>> rulesByGroup = rules
            .GroupBy(rule => rule.PrinterGroupId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var allowed = new HashSet<Guid>();
        foreach (PrinterGroupScope scope in scopes)
        {
            bool groupAllowed = !scope.PrinterGroupId.HasValue ||
                !rulesByGroup.TryGetValue(scope.PrinterGroupId.Value, out List<PrinterGroupAccess>? groupRules) ||
                groupRules.Count == 0 ||
                groupRules.Any(rule =>
                    userRoles.Contains(rule.RoleId) &&
                    rule.AccessLevel >= minimumAccess);
            if (groupAllowed)
            {
                _ = allowed.Add(scope.PrinterId);
            }
        }

        return allowed;
    }

    public async Task<int> CountAccessiblePrintersAsync(
        ClaimsPrincipal principal,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (PrintFarmerPermissions.IsFarmAdmin(principal))
        {
            return await _db.Printers.CountAsync(ct);
        }

        if (!PrintFarmerPermissions.TryGetUserId(principal, out Guid userId))
        {
            return 0;
        }

        HashSet<Guid> userRoles = (await _db.UserRoles
                .AsNoTracking()
                .Where(role => role.UserId == userId && role.IsActive)
                .Select(role => role.RoleId)
                .ToListAsync(ct))
            .ToHashSet();

        return await _db.Printers
            .AsNoTracking()
            .Where(printer => printer.IsEnabled)
            .Where(printer =>
                !printer.PrinterGroupId.HasValue ||
                !_db.PrinterGroupAccesses.Any(rule => rule.PrinterGroupId == printer.PrinterGroupId.Value) ||
                _db.PrinterGroupAccesses.Any(rule =>
                    rule.PrinterGroupId == printer.PrinterGroupId.Value &&
                    userRoles.Contains(rule.RoleId) &&
                    rule.AccessLevel >= minimumAccess))
            .CountAsync(ct);
    }

    private async Task<bool> CanUserAccessPrinterAsync(
        Guid userId,
        Guid printerId,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct)
    {
        Guid? groupId = await _db.Printers
            .AsNoTracking()
            .Where(printer => printer.Id == printerId)
            .Select(printer => printer.PrinterGroupId)
            .SingleOrDefaultAsync(ct);
        bool printerExists = await _db.Printers
            .AsNoTracking()
            .AnyAsync(printer => printer.Id == printerId, ct);
        return printerExists &&
            (!groupId.HasValue ||
             await CanUserAccessGroupAsync(userId, groupId.Value, minimumAccess, ct));
    }

    private async Task<bool> CanUserAccessGroupAsync(
        Guid userId,
        Guid groupId,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct)
    {
        List<PrinterGroupAccess> rules = await _db.PrinterGroupAccesses
            .AsNoTracking()
            .Where(rule => rule.PrinterGroupId == groupId)
            .ToListAsync(ct);
        if (rules.Count == 0)
        {
            return true;
        }

        List<Guid> roles = await ActiveRoleIds(userId).ToListAsync(ct);
        return rules.Any(rule =>
            roles.Contains(rule.RoleId) &&
            rule.AccessLevel >= minimumAccess);
    }

    /// <summary>
    /// Loads the ACL rules for the given groups and, only when any exist, the user's live
    /// role assignments, so per-resource group checks can be answered without further queries.
    /// </summary>
    private async Task<GroupAccessSnapshot> LoadGroupAccessAsync(
        Guid userId,
        IEnumerable<Guid?> groupIds,
        CancellationToken ct)
    {
        Guid[] distinctGroupIds = groupIds
            .Where(groupId => groupId.HasValue)
            .Select(groupId => groupId!.Value)
            .Distinct()
            .ToArray();
        if (distinctGroupIds.Length == 0)
        {
            return GroupAccessSnapshot.Unrestricted;
        }

        List<PrinterGroupAccess> rules = await _db.PrinterGroupAccesses
            .AsNoTracking()
            .Where(rule => distinctGroupIds.Contains(rule.PrinterGroupId))
            .ToListAsync(ct);
        if (rules.Count == 0)
        {
            return GroupAccessSnapshot.Unrestricted;
        }

        HashSet<Guid> roles = (await ActiveRoleIds(userId).ToListAsync(ct)).ToHashSet();
        return new GroupAccessSnapshot(
            rules.GroupBy(rule => rule.PrinterGroupId)
                .ToDictionary(group => group.Key, group => group.ToList()),
            roles);
    }

    private IQueryable<Guid> ActiveRoleIds(Guid userId) =>
        _db.UserRoles
            .AsNoTracking()
            .Where(role => role.UserId == userId && role.IsActive && role.Role.IsActive &&
                (role.ExpiresAt == null || role.ExpiresAt > DateTime.UtcNow))
            .Select(role => role.RoleId);

    private Task<bool> IsFarmAdminAsync(Guid userId, CancellationToken ct) =>
        _db.UserRoles
            .AsNoTracking()
            .AnyAsync(
                userRole =>
                    userRole.UserId == userId &&
                    userRole.IsActive &&
                    userRole.Role.IsActive &&
                    (userRole.ExpiresAt == null || userRole.ExpiresAt > DateTime.UtcNow) &&
                    userRole.Role.Name == PrintFarmerPermissions.FarmAdminRole,
                ct);

    private sealed record JobAccessScope(
        Guid JobId,
        string? CreatorSubject,
        Guid? CalibrationProjectId,
        Guid? PrinterGroupId,
        Guid? GcodeGroupId);

    private sealed record PrinterGroupScope(Guid PrinterId, Guid? PrinterGroupId);

    private sealed record GcodeFileScope(Guid FileId, Guid? PrinterGroupId, Guid? CalibrationProjectId);

    /// <summary>Evaluates group ACLs exactly like <c>CanUserAccessGroupAsync</c>, from preloaded data.</summary>
    private sealed class GroupAccessSnapshot(
        IReadOnlyDictionary<Guid, List<PrinterGroupAccess>> rulesByGroup,
        IReadOnlySet<Guid> activeRoleIds)
    {
        public static GroupAccessSnapshot Unrestricted { get; } =
            new(new Dictionary<Guid, List<PrinterGroupAccess>>(), new HashSet<Guid>());

        public bool Allows(Guid? groupId, PrinterGroupAccessLevel minimumAccess) =>
            !groupId.HasValue ||
            !rulesByGroup.TryGetValue(groupId.Value, out List<PrinterGroupAccess>? rules) ||
            rules.Any(rule => activeRoleIds.Contains(rule.RoleId) && rule.AccessLevel >= minimumAccess);
    }
}
