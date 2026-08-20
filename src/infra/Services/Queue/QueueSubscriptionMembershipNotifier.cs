using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Default <see cref="IQueueSubscriptionMembershipNotifier"/> implementation: broadcasts
/// directly via <see cref="IHubContext{PrinterHub}"/> to <see cref="AuthorizedHubGroups.QueueReaders"/>.
/// </summary>
public sealed class QueueSubscriptionMembershipNotifier(
    IHubContext<PrinterHub> hub,
    ILogger<QueueSubscriptionMembershipNotifier> logger) : IQueueSubscriptionMembershipNotifier
{
    public async Task NotifyMembershipChangedAsync(CancellationToken ct = default)
    {
        try
        {
            await hub.Clients.Group(AuthorizedHubGroups.QueueReaders)
                .SendAsync("queueresourceschanged", ct);
        }
        catch (Exception ex)
        {
            // A failed hint broadcast should never fail the mutation that triggered it --
            // the caller (e.g. PrinterGroupService) has already committed its own change.
            // Worst case, a client's next reconciliation (reconnect or another membership
            // change) catches up.
            logger.LogWarning(ex, "[QueueSubscriptionMembershipNotifier] Failed to broadcast queueresourceschanged hint");
        }
    }
}
