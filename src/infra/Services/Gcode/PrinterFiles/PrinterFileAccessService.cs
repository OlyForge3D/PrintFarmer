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

    public async Task<PrinterFileCatalogAuthority> GetCatalogAuthorityAsync(
        Guid actorId,
        IReadOnlyCollection<Guid> printerIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(printerIds);
        if (printerIds.Count == 0)
        {
            return PrinterFileCatalogAuthority.None;
        }

        PermissionSnapshot permissions = await LoadPermissionsAsync(actorId, ct);
        if (!permissions.Grants(PrintFarmerPermissions.PrinterFiles.Read))
        {
            return PrinterFileCatalogAuthority.None;
        }

        string actor = actorId.ToString();
        Guid[] viewable = (await resources.FilterActorAccessiblePrinterIdsAsync(
            actor, printerIds, PrinterGroupAccessLevel.View, ct)).ToArray();
        HashSet<Guid> readable = viewable.Length == 0
            ? []
            : (await db.Printers.AsNoTracking()
                .Where(x => viewable.Contains(x.Id) && x.IsEnabled)
                .Select(x => x.Id)
                .ToListAsync(ct)).ToHashSet();
        if (readable.Count == 0)
        {
            return PrinterFileCatalogAuthority.None;
        }

        IReadOnlySet<Guid> manageGrants = await resources.FilterActorAccessiblePrinterIdsAsync(
            actor, readable, PrinterGroupAccessLevel.Manage, ct);
        HashSet<Guid> manageable = readable.Where(manageGrants.Contains).ToHashSet();
        bool canCopy = permissions.Grants(PrintFarmerPermissions.PrinterFiles.Copy) &&
            permissions.Grants("gcode_library:read") &&
            permissions.Grants("gcode_library:create");
        bool canDelete = permissions.Grants(PrintFarmerPermissions.PrinterFiles.Delete);
        return new PrinterFileCatalogAuthority(
            readable,
            manageable,
            canCopy ? readable : new HashSet<Guid>(),
            canDelete ? manageable : new HashSet<Guid>());
    }

    public async Task<IReadOnlySet<Guid>> FilterAccessibleLocalAsync(
        Guid actorId,
        IReadOnlyCollection<Guid> gcodeFileIds,
        PrinterGroupAccessLevel minimumAccess = PrinterGroupAccessLevel.View,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gcodeFileIds);
        if (gcodeFileIds.Count == 0 ||
            !(await LoadPermissionsAsync(actorId, ct)).Grants("gcode_library:read"))
        {
            return new HashSet<Guid>();
        }

        return await resources.FilterActorAccessibleGcodeFileIdsAsync(
            actorId.ToString(), gcodeFileIds, minimumAccess, ct);
    }

    private async Task<bool> HasPermissionAsync(Guid actorId, string permission, CancellationToken ct) =>
        (await LoadPermissionsAsync(actorId, ct)).Grants(permission);

    private async Task<PermissionSnapshot> LoadPermissionsAsync(Guid actorId, CancellationToken ct)
    {
        if (actorId == Guid.Empty ||
            !await db.Users.AsNoTracking().AnyAsync(user => user.Id == actorId && user.IsActive, ct))
        {
            return PermissionSnapshot.Nothing;
        }

        if (await db.UserRoles.AsNoTracking().AnyAsync(
                role => role.UserId == actorId && role.IsActive && role.Role.IsActive &&
                    (role.ExpiresAt == null || role.ExpiresAt > DateTime.UtcNow) &&
                    role.Role.Name == PrintFarmerPermissions.FarmAdminRole, ct))
        {
            return PermissionSnapshot.FarmAdmin;
        }

        HashSet<string> granted = (await users.GetGrantedPermissionsAsync(actorId, ct))
            .Select(pair => $"{pair.Resource}:{pair.Action}").ToHashSet(StringComparer.Ordinal);
        HashSet<string> denied = (await users.GetDeniedPermissionsAsync(actorId, ct))
            .Select(pair => $"{pair.Resource}:{pair.Action}").ToHashSet(StringComparer.Ordinal);
        return new PermissionSnapshot(IsFarmAdmin: false, granted, denied);
    }

    /// <summary>One request's view of an actor's live permissions; never retained across calls.</summary>
    private sealed record PermissionSnapshot(
        bool IsFarmAdmin,
        IReadOnlySet<string> Granted,
        IReadOnlySet<string> Denied)
    {
        public static PermissionSnapshot Nothing { get; } =
            new(false, new HashSet<string>(), new HashSet<string>());

        public static PermissionSnapshot FarmAdmin { get; } =
            new(true, new HashSet<string>(), new HashSet<string>());

        public bool Grants(string permission) =>
            IsFarmAdmin || PrintFarmerPermissions.SetGrantsPermission(Granted, Denied, permission);
    }
}
