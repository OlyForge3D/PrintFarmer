using System.Threading;
using System.Threading.Tasks;

namespace Farm.Infrastructure.Services.Attention;

/// <summary>
/// Emits SignalR invalidation events for the attention feed. Clients that receive
/// <see cref="EventName"/> should refetch <c>GET /api/attention</c>. No payload is
/// broadcast because the feed is user-scoped; the event is a coalescing "changed" hint.
/// </summary>
/// <remarks>
/// Per epic #705 the event name is lowercase and rides the existing
/// <c>/hubs/printers</c> hub — no duplicate PascalCase event is introduced.
/// </remarks>
public interface IAttentionBroadcaster
{
    /// <summary>Wire event name; lowercase per SignalR convention.</summary>
    public const string EventName = "attentionchanged";

    /// <summary>Emit an invalidation event to all connected clients.</summary>
    Task NotifyChangedAsync(CancellationToken cancellationToken = default);
}
