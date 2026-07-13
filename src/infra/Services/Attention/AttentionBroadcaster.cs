using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Notifications.NativePush;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Attention;

/// <summary>
/// <see cref="IAttentionBroadcaster"/> implementation that emits
/// <see cref="IAttentionBroadcaster.EventName"/> on the existing <see cref="PrinterHub"/>.
/// </summary>
/// <remarks>
/// Registered as a singleton (it is invoked from both scoped request paths and background
/// scopes). The scoped <see cref="IOperatorFeatureGate"/> is resolved per call through
/// <see cref="IServiceScopeFactory"/> — the pattern documented in
/// <c>docs/OPERATOR_FEATURE_GATES.md</c> for singleton consumers — so a disabled Attention
/// feature (#725) emits no events regardless of which source triggered the change.
///
/// After a successful SignalR broadcast, the same singleton hands the change off to the
/// native-push <see cref="INativePushDispatcher"/> (#708) via a fire-and-forget background
/// task tied to <see cref="IHostApplicationLifetime.ApplicationStopping"/>. Producers must
/// never block on APNs latency: an APNs outage would otherwise pin every attention-emitting
/// request thread. The dispatcher owns its own gating, per-user category preferences,
/// dedupe, and rate limit.
/// </remarks>
public sealed class AttentionBroadcaster(
    IHubContext<PrinterHub> hubContext,
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime lifetime,
    ILogger<AttentionBroadcaster> logger) : IAttentionBroadcaster
{
    private readonly IHubContext<PrinterHub> _hub = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly IHostApplicationLifetime _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
    private readonly ILogger<AttentionBroadcaster> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task NotifyChangedAsync(AttentionChangedPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!AttentionEnabled())
        {
            return;
        }

        try
        {
            await _hub.Clients.All.SendAsync(IAttentionBroadcaster.EventName, payload, cancellationToken);
        }
        catch (Exception ex)
        {
            // Broadcast failure must never break the caller's write path.
            _logger.LogWarning(ex, "[AttentionBroadcaster] Failed to emit '{Event}'", IAttentionBroadcaster.EventName);
        }

        QueueNativePushFanOut(payload, targetUserId: null);
    }

    /// <inheritdoc />
    public async Task NotifyUserChangedAsync(Guid userId, AttentionChangedPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!AttentionEnabled())
        {
            return;
        }

        try
        {
            await _hub.Clients
                .User(userId.ToString("D", CultureInfo.InvariantCulture))
                .SendAsync(IAttentionBroadcaster.EventName, payload, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AttentionBroadcaster] Failed to emit user-targeted '{Event}'", IAttentionBroadcaster.EventName);
        }

        QueueNativePushFanOut(payload, userId);
    }

    private void QueueNativePushFanOut(AttentionChangedPayload payload, Guid? targetUserId)
    {
        // Fire-and-forget: producers (SignalR broadcast, background sources) must never
        // wait on APNs/relay latency. The task is anchored to the app lifetime rather
        // than the caller's request cancellation so a client disconnect does not abort
        // an in-flight delivery. Exceptions are swallowed and logged.
        CancellationToken shutdownToken = _lifetime.ApplicationStopping;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
                    INativePushDispatcher? dispatcher = scope.ServiceProvider.GetService<INativePushDispatcher>();
                    if (dispatcher is null)
                    {
                        return;
                    }

                    await dispatcher.DispatchAsync(payload.ItemId, payload.ChangeKind, targetUserId, shutdownToken);
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                {
                    // App is shutting down — swallow.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[AttentionBroadcaster] Native-push dispatch failed for itemId={ItemId}", payload.ItemId);
                }
            },
            shutdownToken);
    }

    private bool AttentionEnabled()
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            IOperatorFeatureGate gate = scope.ServiceProvider.GetRequiredService<IOperatorFeatureGate>();
            return gate.IsEnabled(OperatorFeature.Attention);
        }
        catch (Exception ex)
        {
            // If the gate cannot be resolved/evaluated, fall back to the documented default
            // (Attention enabled) rather than silently dropping all realtime updates.
            _logger.LogWarning(ex, "[AttentionBroadcaster] Feature-gate check failed; assuming enabled");
            return true;
        }
    }
}
