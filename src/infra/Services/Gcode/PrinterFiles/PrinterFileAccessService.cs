using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Queue;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Gcode.PrinterFiles;

/// <summary>Uses current database authority rather than retained token grants.</summary>
public sealed class PrinterFileAccessService(
    AppDbContext db,
    IUsersRepository users,
    IQueueResourceAuthorizationService resources) : IPrinterFileAccessService
{
    public async Task<bool> CanReadAsync(Guid actorId, Guid printerId, CancellationToken ct = default)
    {
        return await HasPermissionAsync(actorId, PrintFarmerPermissions.PrinterFiles.Read, ct) &&
            await resources.CanActorAccessPrinterAsync(
                actorId.ToString(), printerId, PrinterGroupAccessLevel.View, ct) &&
            await db.Printers.AsNoTracking().AnyAsync(x => x.Id == printerId && x.IsEnabled, ct);
    }

    public async Task<bool> CanActAsync(
        Guid actorId,
        Guid printerId,
        PrinterFileAction action,
        Guid? destinationPrinterId = null,
        CancellationToken ct = default)
    {
        if (!await CanReadAsync(actorId, printerId, ct))
        {
            return false;
        }

        if (action == PrinterFileAction.Delete)
        {
            return await HasPermissionAsync(actorId, PrintFarmerPermissions.PrinterFiles.Delete, ct) &&
                await resources.CanActorAccessPrinterAsync(
                    actorId.ToString(), printerId, PrinterGroupAccessLevel.Manage, ct);
        }

        if (!await HasPermissionAsync(actorId, PrintFarmerPermissions.PrinterFiles.Copy, ct) ||
            !await HasPermissionAsync(actorId, "gcode_library:read", ct) ||
            !await HasPermissionAsync(actorId, "gcode_library:create", ct))
        {
            return false;
        }

        return action == PrinterFileAction.Copy ||
            (action == PrinterFileAction.Queue && destinationPrinterId.HasValue &&
             await HasPermissionAsync(actorId, PrintFarmerPermissions.Queue.Write, ct) &&
             await resources.CanActorAccessPrinterAsync(
                 actorId.ToString(), destinationPrinterId.Value, PrinterGroupAccessLevel.Submit, ct) &&
             await db.Printers.AsNoTracking().AnyAsync(
                 x => x.Id == destinationPrinterId.Value && x.IsEnabled, ct));
    }

    public async Task<bool> CanAccessLocalAsync(
        Guid actorId,
        Guid gcodeFileId,
        PrinterGroupAccessLevel minimumAccess = PrinterGroupAccessLevel.View,
        CancellationToken ct = default) =>
        await HasPermissionAsync(actorId, "gcode_library:read", ct) &&
        await resources.CanActorAccessGcodeFileAsync(actorId.ToString(), gcodeFileId, minimumAccess, ct);

    private async Task<bool> HasPermissionAsync(Guid actorId, string permission, CancellationToken ct)
    {
        if (actorId == Guid.Empty ||
            !await db.Users.AsNoTracking().AnyAsync(user => user.Id == actorId && user.IsActive, ct))
        {
            return false;
        }

        if (await db.UserRoles.AsNoTracking().AnyAsync(
                role => role.UserId == actorId && role.IsActive && role.Role.IsActive &&
                    (role.ExpiresAt == null || role.ExpiresAt > DateTime.UtcNow) &&
                    role.Role.Name == PrintFarmerPermissions.FarmAdminRole, ct))
        {
            return true;
        }

        HashSet<string> granted = (await users.GetGrantedPermissionsAsync(actorId, ct))
            .Select(pair => $"{pair.Resource}:{pair.Action}").ToHashSet(StringComparer.Ordinal);
        HashSet<string> denied = (await users.GetDeniedPermissionsAsync(actorId, ct))
            .Select(pair => $"{pair.Resource}:{pair.Action}").ToHashSet(StringComparer.Ordinal);
        return PrintFarmerPermissions.SetGrantsPermission(granted, denied, permission);
    }
}
