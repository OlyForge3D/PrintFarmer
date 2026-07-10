using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Spoolman;

/// <summary>
/// Default <see cref="IFilamentCoverageBroadcaster"/> implementation that
/// emits <c>filamentcoveragechanged</c> events on the shared
/// <see cref="PrinterHub"/> — same pattern used for <c>printerupdated</c>,
/// <c>jobqueueupdate</c>, and other cross-cutting invalidation signals.
/// </summary>
public class FilamentCoverageBroadcaster(
    IHubContext<PrinterHub> hub,
    ILogger<FilamentCoverageBroadcaster> logger)
    : IFilamentCoverageBroadcaster
{
    private const string EventName = "filamentcoveragechanged";

    private readonly IHubContext<PrinterHub> _hub = hub ?? throw new ArgumentNullException(nameof(hub));
    private readonly ILogger<FilamentCoverageBroadcaster> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task BroadcastPrinterChangedAsync(Guid printerId, string reason, CancellationToken ct)
    {
        string safeReason = NormalizeReason(reason);
        try
        {
            FilamentCoverageChangedEvent payload = new(printerId, safeReason, DateTime.UtcNow);
            await _hub.Clients.All.SendAsync(EventName, payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "[FilamentCoverage] Failed to broadcast {Event} for printer {PrinterId} reason={Reason}",
                EventName,
                printerId,
                safeReason);
        }
    }

    public async Task BroadcastFleetChangedAsync(string reason, CancellationToken ct)
    {
        string safeReason = NormalizeReason(reason);
        try
        {
            FilamentCoverageChangedEvent payload = new(null, safeReason, DateTime.UtcNow);
            await _hub.Clients.All.SendAsync(EventName, payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "[FilamentCoverage] Failed to broadcast fleet {Event} reason={Reason}",
                EventName,
                safeReason);
        }
    }

    // Defensive: never send an empty reason on the wire. Unknown reasons fall
    // back to "queueChanged" (the most conservative refetch trigger) rather
    // than a bespoke string a client won't recognize.
    private static string NormalizeReason(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? FilamentCoverageChangeReasons.QueueChanged : reason;
}
