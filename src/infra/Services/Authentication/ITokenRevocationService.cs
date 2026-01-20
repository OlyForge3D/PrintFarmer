using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Authentication;

/// <summary>
/// Service for managing JWT token revocation (force logout)
/// </summary>
public interface ITokenRevocationService
{
    /// <summary>
    /// Revoke a specific JWT token
    /// </summary>
    /// <param name="token">The JWT token to revoke</param>
    /// <param name="userId">The user ID who owns the token</param>
    /// <param name="revokedByUserId">The user ID who is revoking the token</param>
    /// <param name="reason">The reason for revocation</param>
    /// <param name="ipAddress">Optional IP address of the request</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    Task<bool> RevokeTokenAsync(string token, Guid userId, Guid revokedByUserId, string reason, string? ipAddress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revoke all active tokens for a specific user (force logout from all devices)
    /// </summary>
    /// <param name="userId">The user ID whose tokens should be revoked</param>
    /// <param name="revokedByUserId">The user ID who is revoking the tokens</param>
    /// <param name="reason">The reason for revocation</param>
    /// <param name="ipAddress">Optional IP address of the request</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    Task<int> RevokeAllUserTokensAsync(Guid userId, Guid revokedByUserId, string reason, string? ipAddress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a token has been revoked
    /// </summary>
    /// <param name="token">The JWT token to check</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    Task<bool> IsTokenRevokedAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleanup expired revoked tokens (for background job)
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    Task<int> CleanupExpiredRevocationsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all revoked tokens for a user
    /// </summary>
    /// <param name="userId">The user ID to get revoked tokens for</param>
    /// <param name="cancellationToken">Cancellation token for async operation</param>
    Task<List<RevokedToken>> GetUserRevokedTokensAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get token hash from JWT string (for storage/lookup)
    /// </summary>
    /// <param name="token">The JWT token to hash</param>
    string GetTokenHash(string token);
}
