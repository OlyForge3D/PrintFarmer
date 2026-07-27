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
            (await IsFarmAdminAsync(userId, ct) ||
             await CanUserAccessJobAsync(userId, jobId, minimumAccess, ct));
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

    private async Task<bool> CanUserAccessJobAsync(
        Guid userId,
        Guid jobId,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct)
    {
        var job = await _db.PrintJobs
            .AsNoTracking()
            .Where(candidate => candidate.Id == jobId)
            .Select(candidate => new
            {
                candidate.CreatorSubject,
                candidate.CalibrationProjectId,
                PrinterGroupId = candidate.AssignedPrinter != null
                    ? candidate.AssignedPrinter.PrinterGroupId
                    : null,
                GcodeGroupId = candidate.GcodeFile != null
                    ? candidate.GcodeFile.PrinterGroupId
                    : null,
            })
            .SingleOrDefaultAsync(ct);
        if (job is null)
        {
            return false;
        }

        if (job.CalibrationProjectId.HasValue)
        {
            bool ownsCalibration = await _db.CalibrationProjects
                .AsNoTracking()
                .AnyAsync(
                    project =>
                        project.Id == job.CalibrationProjectId.Value &&
                        project.OwnerUserId == userId,
                    ct);
            if (!ownsCalibration)
            {
                return false;
            }
        }

        Guid[] groupIds = new[] { job.PrinterGroupId, job.GcodeGroupId }
            .Where(groupId => groupId.HasValue)
            .Select(groupId => groupId!.Value)
            .Distinct()
            .ToArray();
        foreach (Guid groupId in groupIds)
        {
            if (!await CanUserAccessGroupAsync(userId, groupId, minimumAccess, ct))
            {
                return false;
            }
        }

        // A creator does not bypass either source or destination group boundary. Conversely,
        // authorized group collaborators may operate standard shared jobs. Calibration
        // ownership was enforced above through the authoritative project owner.
        return job.CalibrationProjectId.HasValue ||
            !Guid.TryParse(job.CreatorSubject, out Guid creatorId) ||
            creatorId == userId ||
            groupIds.Length > 0;
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
}
