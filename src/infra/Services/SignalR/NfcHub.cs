using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Queue;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.SignalR;

/// <summary>
/// SignalR hub for broadcasting NFC tag scan events to connected clients.
/// Clients subscribe to nfctagread and nfctagunknown events.
/// Requires an authenticated user — spool and printer identifiers are farm-private data,
/// consistent with the other farm hubs (PrinterHub, HarvestHub, MaintenanceHub).
/// </summary>
[Authorize]
public class NfcHub(
    AppDbContext db,
    IQueueResourceAuthorizationService resourceAuthorization,
    ILogger<NfcHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        Guid[] printerIds = await db.Printers
            .AsNoTracking()
            .Select(printer => printer.Id)
            .ToArrayAsync(Context.ConnectionAborted);
        IReadOnlySet<Guid> accessiblePrinterIds =
            await resourceAuthorization.FilterAccessiblePrinterIdsAsync(
                Context.User!,
                printerIds,
                PrinterGroupAccessLevel.View,
                Context.ConnectionAborted);

        foreach (Guid printerId in accessiblePrinterIds)
        {
            await Groups.AddToGroupAsync(
                Context.ConnectionId,
                AuthorizedHubGroups.Printer(printerId),
                Context.ConnectionAborted);
        }

        logger.LogDebug("Client connected to NfcHub: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogDebug("Client disconnected from NfcHub: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// SignalR event names for NfcHub. All names are lowercase per SignalR conventions.
/// </summary>
public static class NfcHubEvents
{
    /// <summary>
    /// Emitted when a known tag is scanned.
    /// Payload: { tagUid, spoolId, spoolName, printerId, trayId, readAt }
    /// </summary>
    public const string TagRead = "nfctagread";

    /// <summary>
    /// Emitted when an unrecognized tag is scanned (no binding found).
    /// Payload: { tagUid, printerId, readAt }
    /// </summary>
    public const string TagUnknown = "nfctagunknown";
}
