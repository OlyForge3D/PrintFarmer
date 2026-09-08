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
/// Connections automatically join only printers they may view. Unscoped scans are
/// reserved for farm administrators.
/// </summary>
[Authorize]
public class NfcHub(
    ILogger<NfcHub> logger,
    AppDbContext db,
    IQueueResourceAuthorizationService resourceAuthorization) : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (PrintFarmerPermissions.IsFarmAdmin(Context.User!))
        {
            await Groups.AddToGroupAsync(
                Context.ConnectionId, AuthorizedHubGroups.Administrators, Context.ConnectionAborted);
        }
        else
        {
            // Existing NFC clients listen immediately without an explicit subscription call.
            Guid[] printerIds = await db.Printers.AsNoTracking()
                .Select(p => p.Id).ToArrayAsync(Context.ConnectionAborted);
            IReadOnlySet<Guid> allowed = await resourceAuthorization.FilterAccessiblePrinterIdsAsync(
                Context.User!, printerIds, PrinterGroupAccessLevel.View, Context.ConnectionAborted);
            foreach (Guid printerId in allowed)
            {
                await Groups.AddToGroupAsync(
                    Context.ConnectionId, AuthorizedHubGroups.Printer(printerId), Context.ConnectionAborted);
            }
        }

        logger.LogDebug("Client connected to NfcHub: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    /// <summary>Subscribes to a printer after checking its view permission.</summary>
    public async Task SubscribeToPrinterAsync(string printerId)
    {
        if (!Guid.TryParse(printerId, out Guid id))
        {
            throw new HubException("invalid_resource_id");
        }

        if (!await resourceAuthorization.CanAccessPrinterAsync(
                Context.User!, id, PrinterGroupAccessLevel.View, Context.ConnectionAborted))
        {
            throw new HubException("resource_forbidden");
        }

        if (PrintFarmerPermissions.IsFarmAdmin(Context.User!))
        {
            // Administrators already receive every scan through their exclusive group.
            return;
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId, AuthorizedHubGroups.Printer(id), Context.ConnectionAborted);
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
