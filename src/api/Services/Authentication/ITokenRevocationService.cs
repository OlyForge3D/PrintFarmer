using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Services.Authentication;

/// <summary>
/// Service for managing JWT token revocation (force logout)
/// </summary>
public interface ITokenRevocationService
{
    /// <summary>
    /// Revoke a specific JWT token
    /// </summary>
    Task<bool> RevokeTokenAsync(string token, Guid userId, Guid revokedByUserId, string reason, string? ipAddress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revoke all active tokens for a specific user (force logout from all devices)
    /// </summary>
    Task<int> RevokeAllUserTokensAsync(Guid userId, Guid revokedByUserId, string reason, string? ipAddress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a token has been revoked
    /// </summary>
    Task<bool> IsTokenRevokedAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleanup expired revoked tokens (for background job)
    /// </summary>
    Task<int> CleanupExpiredRevocationsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all revoked tokens for a user
    /// </summary>
    Task<List<RevokedToken>> GetUserRevokedTokensAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get token hash from JWT string (for storage/lookup)
    /// </summary>
    string GetTokenHash(string token);
}
