using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services.Authentication;

/// <summary>
/// Service for managing JWT token revocation (force logout)
/// </summary>
public class TokenRevocationService : ITokenRevocationService
{
    private readonly AppDbContext _context;
    private readonly IUnifiedLoggingService _logging;
    private readonly IAuthAuditService _authAuditService;

    public TokenRevocationService(
        AppDbContext context,
        IUnifiedLoggingService logging,
        IAuthAuditService authAuditService)
    {
        _context = context;
        _logging = logging;
        _authAuditService = authAuditService;
    }

    public async Task<bool> RevokeTokenAsync(
        string token,
        Guid userId,
        Guid revokedByUserId,
        string reason,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tokenHash = GetTokenHash(token);

            // Check if already revoked
            var existingRevocation = await _context.RevokedTokens
                .FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash, cancellationToken);

            if (existingRevocation != null)
            {
                _logging.LogWarning($"[TokenRevocation] Token already revoked for UserId: {userId}");
                return false;
            }

            // Get token expiration from JWT
            var expiration = GetTokenExpiration(token);

            var revokedToken = new RevokedToken
            {
                Id = Guid.NewGuid(),
                TokenHash = tokenHash,
                UserId = userId,
                RevokedAt = DateTime.UtcNow,
                RevokedByUserId = revokedByUserId,
                Reason = reason,
                ExpiresAt = expiration,
                IpAddress = ipAddress
            };

            _context.RevokedTokens.Add(revokedToken);
            await _context.SaveChangesAsync(cancellationToken);

            // Audit log the revocation
            await _authAuditService.LogTokenRevokedAsync(userId, revokedByUserId, reason, ipAddress);

            _logging.LogInformation($"[TokenRevocation] Token revoked for UserId: {userId} by Admin: {revokedByUserId}. Reason: {reason}");
            return true;
        }
        catch (Exception ex)
        {
            _logging.LogError(ex, $"[TokenRevocation] Error revoking token for UserId: {userId}");
            return false;
        }
    }

    public async Task<int> RevokeAllUserTokensAsync(
        Guid userId,
        Guid revokedByUserId,
        string reason,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // For revoking all tokens, we create a special marker with userId but no specific token hash
            // This will require checking userId in the middleware

            // Alternative approach: Get all active refresh tokens for the user and revoke them
            var activeRefreshTokens = await _context.RefreshTokens
                .Where(rt => rt.UserId == userId && !rt.IsRevoked && rt.ExpiresAt > DateTime.UtcNow)
                .ToListAsync(cancellationToken);

            int revokedCount = 0;

            foreach (var refreshToken in activeRefreshTokens)
            {
                // Mark refresh token as revoked
                refreshToken.IsRevoked = true;
                refreshToken.RevokedAt = DateTime.UtcNow;
                refreshToken.RevokedByIp = ipAddress ?? "system";
                revokedCount++;
            }

            // Also create a time-based revocation marker for JWT tokens
            // Any JWT issued before this timestamp for this user will be considered revoked
            var revocationMarker = new RevokedToken
            {
                Id = Guid.NewGuid(),
                TokenHash = $"ALL_TOKENS_{userId}_{DateTime.UtcNow.Ticks}", // Special marker
                UserId = userId,
                RevokedAt = DateTime.UtcNow,
                RevokedByUserId = revokedByUserId,
                Reason = $"All tokens revoked: {reason}",
                ExpiresAt = DateTime.UtcNow.AddDays(30), // Keep marker for 30 days
                IpAddress = ipAddress
            };

            _context.RevokedTokens.Add(revocationMarker);
            await _context.SaveChangesAsync(cancellationToken);

            // Audit log the mass revocation
            await _authAuditService.LogTokenRevokedAsync(
                userId,
                revokedByUserId,
                $"All tokens revoked ({revokedCount} refresh tokens). {reason}",
                ipAddress);

            _logging.LogWarning($"[TokenRevocation] All tokens revoked for UserId: {userId} by Admin: {revokedByUserId}. Count: {revokedCount}. Reason: {reason}");
            return revokedCount + 1; // Include the marker
        }
        catch (Exception ex)
        {
            _logging.LogError(ex, $"[TokenRevocation] Error revoking all tokens for UserId: {userId}");
            return 0;
        }
    }

    public async Task<bool> IsTokenRevokedAsync(string token, CancellationToken cancellationToken = default)
    {
        try
        {
            var tokenHash = GetTokenHash(token);

            // Check if specific token is revoked
            var isRevoked = await _context.RevokedTokens
                .AnyAsync(rt => rt.TokenHash == tokenHash, cancellationToken);

            if (isRevoked)
            {
                return true;
            }

            // Check if user has a "revoke all" marker
            var userId = GetUserIdFromToken(token);
            if (userId.HasValue)
            {
                var tokenIssuedAt = GetTokenIssuedAt(token);

                // Check if there's a revocation marker issued after this token
                var hasRevocationMarker = await _context.RevokedTokens
                    .Where(rt => rt.UserId == userId.Value && rt.RevokedAt > tokenIssuedAt)
                    .AnyAsync(rt => rt.TokenHash.StartsWith("ALL_TOKENS_"), cancellationToken);

                if (hasRevocationMarker)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logging.LogError(ex, "[TokenRevocation] Error checking token revocation status");
            // On error, allow the token (fail open) - existing JWT validation will still apply
            return false;
        }
    }

    public async Task<int> CleanupExpiredRevocationsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var expiredRevocations = await _context.RevokedTokens
                .Where(rt => rt.ExpiresAt < DateTime.UtcNow)
                .ToListAsync(cancellationToken);

            _context.RevokedTokens.RemoveRange(expiredRevocations);
            await _context.SaveChangesAsync(cancellationToken);

            _logging.LogInformation($"[TokenRevocation] Cleaned up {expiredRevocations.Count} expired revoked tokens");
            return expiredRevocations.Count;
        }
        catch (Exception ex)
        {
            _logging.LogError(ex, "[TokenRevocation] Error cleaning up expired revocations");
            return 0;
        }
    }

    public async Task<List<RevokedToken>> GetUserRevokedTokensAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await _context.RevokedTokens
            .Where(rt => rt.UserId == userId)
            .OrderByDescending(rt => rt.RevokedAt)
            .ToListAsync(cancellationToken);
    }

    public string GetTokenHash(string token)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private DateTime GetTokenExpiration(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwtToken = handler.ReadJwtToken(token);
            return jwtToken.ValidTo;
        }
        catch
        {
            // Default to 7 days from now if we can't parse the token
            return DateTime.UtcNow.AddDays(7);
        }
    }

    private Guid? GetUserIdFromToken(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwtToken = handler.ReadJwtToken(token);
            var userIdClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == "sub" || c.Type == "userId");

            if (userIdClaim != null && Guid.TryParse(userIdClaim.Value, out var userId))
            {
                return userId;
            }
        }
        catch
        {
            // Ignore parsing errors
        }

        return null;
    }

    private DateTime GetTokenIssuedAt(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwtToken = handler.ReadJwtToken(token);
            return jwtToken.ValidFrom;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }
}
