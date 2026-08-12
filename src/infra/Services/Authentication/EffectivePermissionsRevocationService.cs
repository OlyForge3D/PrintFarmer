namespace Farm.Infrastructure.Services.Authentication;

/// <inheritdoc cref="IEffectivePermissionsRevocationService"/>
public sealed class EffectivePermissionsRevocationService(ITokenRevocationService tokenRevocationService)
    : IEffectivePermissionsRevocationService
{
    private readonly ITokenRevocationService _tokenRevocationService = tokenRevocationService ?? throw new ArgumentNullException(nameof(tokenRevocationService));

    public async Task<int> RevokeUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        Guid actingUserId,
        string reason,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        int revokedUserCount = 0;
        foreach (Guid userId in userIds)
        {
            int revokedTokenCount = await _tokenRevocationService.RevokeAllUserTokensAsync(
                userId,
                actingUserId,
                reason,
                ipAddress,
                cancellationToken).ConfigureAwait(false);
            if (revokedTokenCount > 0)
            {
                revokedUserCount++;
            }
        }

        return revokedUserCount;
    }
}
