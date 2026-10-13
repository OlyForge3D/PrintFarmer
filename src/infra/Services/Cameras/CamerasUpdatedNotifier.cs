using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Cameras;

/// <summary>
/// Sends the lowercase <c>camerasupdated</c> SignalR event to a printer's authorized group. Shared by
/// background camera sync and user-initiated camera changes so both raise the same contract.
/// </summary>
internal static class CamerasUpdatedNotifier
{
    /// <summary>Lowercase SignalR event raised when a printer's linked cameras change.</summary>
    public const string EventName = "camerasupdated";

    /// <summary>Broadcasts the printer's current camera ids. Failures are logged, never thrown.</summary>
    public static async Task NotifyAsync(
        IHubContext<PrinterHub> hubContext,
        Guid printerId,
        IReadOnlyList<Guid> cameraIds,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            await hubContext.Clients
                .Group(AuthorizedHubGroups.Printer(printerId))
                .SendAsync(EventName, new CamerasUpdatedEvent(printerId, cameraIds), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to broadcast {EventName} for printer {PrinterId}", EventName, printerId);
        }
    }

    /// <summary>
    /// Loads each printer's currently linked camera ids (in printer display order) and broadcasts them.
    /// </summary>
    public static async Task NotifyCurrentLinksAsync(
        IHubContext<PrinterHub> hubContext,
        AppDbContext db,
        IEnumerable<Guid> printerIds,
        ILogger logger,
        CancellationToken ct)
    {
        foreach (Guid printerId in printerIds.Where(id => id != Guid.Empty).Distinct())
        {
            List<Guid> cameraIds = await db.Set<CameraPrinter>()
                .AsNoTracking()
                .Where(l => l.PrinterId == printerId)
                .OrderBy(l => l.SortOrder)
                .ThenBy(l => l.CameraId)
                .Select(l => l.CameraId)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            await NotifyAsync(hubContext, printerId, cameraIds, logger, ct).ConfigureAwait(false);
        }
    }
}
