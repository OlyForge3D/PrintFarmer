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
    /// registration version is rotated atomically, the row is re-activated, and its
    /// consecutive-failure counter is reset.
    /// </summary>
    /// <remarks>
    /// Also performs an atomic ownership transfer (#705): the installation id is
    /// scoped to <c>(userId, installationId)</c>, not globally unique, so a prior
    /// account's row for this same installation can still be active (e.g. its
    /// logout unregister call failed or never arrived). Any such row — for a
    /// different <c>userId</c> — is deactivated as part of the same save, so an
    /// installation can never remain active for two accounts at once.
    /// </remarks>
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
    /// counter. The write is applied only when <paramref name="registrationVersion"/> is
    /// still current; stale provider outcomes are no-ops. Never re-activates a manually
    /// deactivated row here; that only happens on upsert.
    /// </summary>
    Task RecordSuccessAsync(
        Guid deviceTokenId,
        long registrationVersion,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an explicitly device-token-attributed terminal failure: bumps
    /// <c>LastFailureAt</c> and the consecutive-failure counter, and soft-deactivates the row
    /// when the counter reaches <paramref name="failureThreshold"/>. Provider-wide transient
    /// failures never call this method. The write is applied only when
    /// <paramref name="registrationVersion"/> is still current; stale provider outcomes are
    /// no-ops.
    /// </summary>
    Task RecordFailureAsync(
        Guid deviceTokenId,
        long registrationVersion,
        DateTime nowUtc,
        int failureThreshold,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard-deletes the exact registration identified by <paramref name="deviceTokenId"/>
    /// after APNs reports <c>410 Gone</c>, <c>BadDeviceToken</c>, or <c>Unregistered</c>.
    /// Provider token text is not an identity: the same value can legitimately exist in
    /// another APNs environment, topic, installation, or user registration. The delete is
    /// applied only when <paramref name="registrationVersion"/> is still current.
    /// </summary>
    Task<bool> InvalidateAsync(
        Guid deviceTokenId,
        long registrationVersion,
        CancellationToken cancellationToken = default);
}
