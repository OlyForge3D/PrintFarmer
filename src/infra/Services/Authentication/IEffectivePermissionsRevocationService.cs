namespace Farm.Infrastructure.Services.Authentication;

/// <summary>
/// Single shared fan-out path for "a set of users' effective permissions just changed, kill
/// their live tokens." Both revocation triggers funnel through this one code path:
/// <list type="bullet">
/// <item>a role's own permission grants change (<c>RolePermissionService</c>, #1471), which
/// affects every currently-active holder of that role;</item>
/// <item>a user's role assignment changes -- a role added or removed from a user
/// (<c>UsersService</c>, #1454), which affects that single user.</item>
/// </list>
/// Keeping exactly one implementation means any future hardening of the fan-out (e.g. the
/// fail-open/fail-closed behavior of the underlying revocation check) only needs to be applied
/// once.
/// </summary>
public interface IEffectivePermissionsRevocationService
{
    /// <summary>
    /// Revokes all active tokens for each user in <paramref name="userIds"/>.
    /// </summary>
    /// <param name="userIds">The users whose effective permissions changed.</param>
    /// <param name="actingUserId">The administrator who performed the change that triggered this revocation.</param>
    /// <param name="reason">The reason recorded against each revoked token.</param>
    /// <param name="ipAddress">The IP address the triggering change was made from.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The number of distinct users who had at least one active token revoked.</returns>
    Task<int> RevokeUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        Guid actingUserId,
        string reason,
        string? ipAddress,
        CancellationToken cancellationToken = default);
}
