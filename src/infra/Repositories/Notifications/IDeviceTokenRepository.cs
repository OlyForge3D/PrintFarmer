using Farm.Infrastructure.Domain.Notifications;

namespace Farm.Infrastructure.Repositories.Notifications;

/// <summary>
/// Persistence for native push device-token registrations. See
/// <c>docs/OPERATOR_NATIVE_PUSH.md</c> and issue #708.
/// </summary>
public interface IDeviceTokenRepository
{
    /// <summary>
    /// Idempotently registers or updates the device token for the given
    /// <c>(userId, installationId)</c>. Any existing row for the same installation is
    /// updated in-place (token, platform, environment, bundle id, activation) so re-launch
    /// or provider-refreshed tokens do not leave orphan rows. On any successful upsert the
    /// row is re-activated and its consecutive-failure counter is reset.
    /// </summary>
    Task<DeviceToken> UpsertAsync(
        Guid userId,
        string installationId,
        string token,
        string platform,
        string environment,
        string? appBundleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the device token row for the given <c>(userId, installationId)</c>. Returns
    /// <c>true</c> when a row was removed.
    /// </summary>
    Task<bool> DeleteByInstallationAsync(Guid userId, string installationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the active device tokens for the given user, or an empty list.
    /// </summary>
    Task<IReadOnlyList<DeviceToken>> GetActiveByUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the distinct user ids for which at least one active device token exists.
    /// Used by the delivery fan-out to bound the per-event work.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetActiveTokenOwnersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a successful send: bumps <c>LastUsedAt</c> and resets the consecutive-failure
    /// counter. Never re-activates a manually deactivated row here; that only happens on
    /// upsert.
    /// </summary>
    Task RecordSuccessAsync(Guid deviceTokenId, DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a transient failure: bumps <c>LastFailureAt</c> and the consecutive-failure
    /// counter, and soft-deactivates the row when the counter reaches <paramref name="failureThreshold"/>.
    /// </summary>
    Task RecordFailureAsync(Guid deviceTokenId, DateTime nowUtc, int failureThreshold, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard-deletes every row whose <c>Token</c> matches the given provider-invalidated
    /// value (raised by APNs <c>410 Gone</c> / <c>BadDeviceToken</c> / <c>Unregistered</c>).
    /// Returns the number of rows removed.
    /// </summary>
    Task<int> InvalidateByTokenAsync(string providerToken, CancellationToken cancellationToken = default);
}
