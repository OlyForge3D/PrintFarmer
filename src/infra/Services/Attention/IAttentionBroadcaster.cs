using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Dtos.Attention;

namespace Farm.Infrastructure.Services.Attention;

/// <summary>
/// Emits SignalR invalidation events for the attention feed. Clients that receive
/// <see cref="EventName"/> should refetch <c>GET /api/attention</c>; the payload is an
/// invalidation hint (item id + transition), never a second source of item truth.
/// </summary>
/// <remarks>
/// <para>
/// Per epic #705 the event name is lowercase and rides the existing
/// <c>/hubs/printers</c> hub — no duplicate PascalCase event is introduced.
/// </para>
/// <para>
/// Source transitions (failure, maintenance, actions) notify all authenticated clients via
/// <see cref="NotifyChangedAsync"/>; per-user snooze changes are targeted to that user's
/// connections only via <see cref="NotifyUserChangedAsync"/> so one operator's snooze state
/// is never broadcast to everyone.
/// </para>
/// <para>
/// Implementations must honour the #725 <c>attentionEnabled</c> feature gate: when Attention
/// is disabled, no event is emitted.
/// </para>
/// </remarks>
public interface IAttentionBroadcaster
{
    /// <summary>Wire event name; lowercase per SignalR convention.</summary>
    public const string EventName = "attentionchanged";

    /// <summary>
    /// Emit an invalidation event to all connected clients (source-transition changes).
    /// </summary>
    Task NotifyChangedAsync(AttentionChangedPayload payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Emit an invalidation event to a single user's connections only. Used for per-user
    /// snooze changes, which must not be broadcast to other users.
    /// </summary>
    Task NotifyUserChangedAsync(Guid userId, AttentionChangedPayload payload, CancellationToken cancellationToken = default);
}
