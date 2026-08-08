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

        List<Guid> roles = await _db.UserRoles
            .AsNoTracking()
            .Where(role => role.UserId == userId && role.IsActive)
            .Select(role => role.RoleId)
            .ToListAsync(ct);
        return rules.Any(rule =>
            roles.Contains(rule.RoleId) &&
            rule.AccessLevel >= minimumAccess);
    }

    private Task<bool> IsFarmAdminAsync(Guid userId, CancellationToken ct) =>
        _db.UserRoles
            .AsNoTracking()
            .AnyAsync(
                userRole =>
                    userRole.UserId == userId &&
                    userRole.IsActive &&
                    userRole.Role.Name == PrintFarmerPermissions.FarmAdminRole,
                ct);

    private sealed record JobAccessScope(
        Guid JobId,
        string? CreatorSubject,
        Guid? CalibrationProjectId,
        Guid? PrinterGroupId,
        Guid? GcodeGroupId);

    private sealed record PrinterGroupScope(Guid PrinterId, Guid? PrinterGroupId);
}
