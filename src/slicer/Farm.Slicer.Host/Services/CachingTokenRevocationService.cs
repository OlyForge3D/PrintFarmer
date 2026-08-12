using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Authentication;
using Microsoft.Extensions.Caching.Memory;

namespace Farm.Slicer.Host.Services;

/// <summary>
/// Wraps <see cref="TokenRevocationService"/> with a short-TTL in-memory cache for
/// <see cref="ITokenRevocationService.IsTokenRevokedAsync"/>.
/// </summary>
/// <remarks>
/// <see cref="Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents.OnTokenValidated"/> runs
/// on every request to this host - including every streamed chunk of an <c>/api/artifacts</c>
/// download and every <c>/hubs/slicer</c> SignalR message - so checking revocation status with an
/// unconditional database round-trip per request would be a meaningful hot-path cost (#1469). This
/// cache trades a short (few-second) worst-case delay in observing a forced revocation for
/// eliminating that per-request query in the common (not-revoked) case. All other members delegate
/// directly to the inner service and are never cached.
/// </remarks>
public sealed class CachingTokenRevocationService(
    TokenRevocationService inner,
    IMemoryCache cache) : ITokenRevocationService
{
    /// <summary>
    /// How long a revocation-status lookup is trusted before re-checking the database. Kept short
    /// so a "revoke all tokens" action becomes effective on this host within a bounded, small
    /// window rather than the token's full remaining lifetime.
    /// </summary>
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private const string CacheKeyPrefix = "Farm.Slicer.Host.TokenRevocation:";

    private readonly TokenRevocationService _inner = inner;
    private readonly IMemoryCache _cache = cache;

    public async Task<bool> IsTokenRevokedAsync(string token, CancellationToken cancellationToken = default)
    {
        string cacheKey = CacheKeyPrefix + _inner.GetTokenHash(token);
        if (_cache.TryGetValue(cacheKey, out bool cachedResult))
        {
            return cachedResult;
        }

        bool isRevoked = await _inner.IsTokenRevokedAsync(token, cancellationToken);
        _ = _cache.Set(cacheKey, isRevoked, CacheTtl);
        return isRevoked;
    }

    public Task<bool> RevokeTokenAsync(
        string token,
        Guid userId,
        Guid revokedByUserId,
        string reason,
        string? ipAddress = null,
        CancellationToken cancellationToken = default) =>
        _inner.RevokeTokenAsync(token, userId, revokedByUserId, reason, ipAddress, cancellationToken);

    public Task<int> RevokeAllUserTokensAsync(
        Guid userId,
        Guid revokedByUserId,
        string reason,
        string? ipAddress = null,
        CancellationToken cancellationToken = default) =>
        _inner.RevokeAllUserTokensAsync(userId, revokedByUserId, reason, ipAddress, cancellationToken);

    public Task<int> CleanupExpiredRevocationsAsync(CancellationToken cancellationToken = default) =>
        _inner.CleanupExpiredRevocationsAsync(cancellationToken);

    public Task<List<RevokedToken>> GetUserRevokedTokensAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _inner.GetUserRevokedTokensAsync(userId, cancellationToken);

    public string GetTokenHash(string token) => _inner.GetTokenHash(token);
}
