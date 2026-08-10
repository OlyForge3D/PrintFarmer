namespace Farm.Infrastructure.Services.ServerIdentity;

/// <summary>
/// Resolves the stable, opaque identity of this PrintFarmer server installation. See
/// <c>docs/OPERATOR_NATIVE_PUSH.md</c> and issue #1407.
/// </summary>
public interface IServerIdentityService
{
    /// <summary>
    /// Returns this server's canonical <c>serverId</c>, generating and persisting one on
    /// first call if none exists yet. The value is stable across restarts, config
    /// reloads, token rotation, and repeated calls — it is never regenerated once
    /// persisted.
    /// </summary>
    Task<Guid> GetOrCreateServerIdAsync(CancellationToken cancellationToken = default);
}
