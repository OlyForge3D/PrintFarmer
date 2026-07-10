using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Attention;

/// <summary>
/// <see cref="IAttentionBroadcaster"/> implementation that emits
/// <see cref="IAttentionBroadcaster.EventName"/> on the existing
/// <see cref="PrinterHub"/>.
/// </summary>
public sealed class AttentionBroadcaster(
    IHubContext<PrinterHub> hubContext,
    ILogger<AttentionBroadcaster> logger) : IAttentionBroadcaster
{
    private readonly IHubContext<PrinterHub> _hub = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
    private readonly ILogger<AttentionBroadcaster> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task NotifyChangedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _hub.Clients.All.SendAsync(IAttentionBroadcaster.EventName, cancellationToken);
        }
        catch (Exception ex)
        {
            // Broadcast failure must never break the caller's write path.
            _logger.LogWarning(ex, "[AttentionBroadcaster] Failed to emit '{Event}'", IAttentionBroadcaster.EventName);
        }
    }
}
