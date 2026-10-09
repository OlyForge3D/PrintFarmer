using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Authentication;

/// <summary>Manages sliding refresh-token sessions and their rotation.</summary>
public sealed class RefreshTokenService(
    AppDbContext context,
    IAuthenticationService authenticationService,
    IAuthAuditService authAuditService,
    ILogger<RefreshTokenService> logger) : IRefreshTokenService
{
    private const int RefreshTokenLifetimeDays = 30;
    private const int RefreshTokenByteLength = 32;
    private const string InvalidRefreshTokenError = "Invalid refresh token.";
    private readonly AppDbContext _context = context;
    private readonly IAuthenticationService _authenticationService = authenticationService;
    private readonly IAuthAuditService _authAuditService = authAuditService;
    private readonly ILogger<RefreshTokenService> _logger = logger;

    public async Task<(string Token, DateTime ExpiresAt)> CreateAsync(
        Guid userId,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        DateTime now = DateTime.UtcNow;
        (string rawToken, string tokenHash) = GenerateToken();
        DateTime expiresAt = now.AddDays(RefreshTokenLifetimeDays);

        _context.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Token = tokenHash,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            CreatedByIp = NormalizeIp(ipAddress)
        });
        await _context.SaveChangesAsync(cancellationToken);
        return (rawToken, expiresAt);
    }

    public async Task<AuthenticationResult> RotateAsync(
        string token,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetHash(token, out string tokenHash))
        {
            return InvalidResult();
        }

        try
        {
            return await RotateCoreAsync(tokenHash, ipAddress, cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            // Disposing the core transaction rolls it back before the request audit can save.
            _context.ChangeTracker.Clear();
            _logger.LogWarning("Refresh token rotation failed due to a database conflict ({ExceptionType})", exception.GetType().Name);
            return InvalidResult();
        }
    }

    private async Task<AuthenticationResult> RotateCoreAsync(
        string tokenHash,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        RefreshToken? refreshToken = await _context.RefreshTokens
            .Include(candidate => candidate.User)
            .SingleOrDefaultAsync(candidate => candidate.Token == tokenHash, cancellationToken);

        if (refreshToken is null || !HashesEqual(refreshToken.Token, tokenHash))
        {
            await transaction.RollbackAsync(cancellationToken);
            return InvalidResult();
        }

        DateTime now = DateTime.UtcNow;
        if (refreshToken.IsRevoked)
        {
            if (refreshToken.ReplacedByToken is not null)
            {
                await RevokeActiveTokensAsync(refreshToken.UserId, now, ipAddress, cancellationToken);
                _context.RevokedTokens.Add(new RevokedToken
                {
                    Id = Guid.NewGuid(),
                    TokenHash = $"ALL_TOKENS_{refreshToken.UserId}_{now.Ticks}",
                    UserId = refreshToken.UserId,
                    RevokedAt = now,
                    RevokedByUserId = refreshToken.UserId,
                    Reason = "All tokens revoked: rotated refresh token reuse",
                    ExpiresAt = now.AddDays(30),
                    IpAddress = NormalizeIp(ipAddress)
                });
                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                _logger.LogWarning("Rotated refresh token reuse detected for user {UserId}; active sessions revoked", refreshToken.UserId);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return InvalidResult();
        }

        if (refreshToken.ExpiresAt <= now || !refreshToken.User.IsActive ||
            (refreshToken.User.LockoutEnd.HasValue && refreshToken.User.LockoutEnd.Value > now) ||
            await IsRevokedByRevokeAllAsync(refreshToken.UserId, refreshToken.CreatedAt, cancellationToken))
        {
            refreshToken.IsRevoked = true;
            refreshToken.RevokedAt = now;
            refreshToken.RevokedByIp = NormalizeIp(ipAddress);
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return InvalidResult();
        }

        string accessToken = await _authenticationService.GenerateJwtTokenAsync(refreshToken.User);
        Farm.Infrastructure.Contracts.Auth.UserDto? user =
            await _authenticationService.GetUserWithRolesAndPermissionsAsync(refreshToken.UserId);
        if (user is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return InvalidResult();
        }

        (string nextRawToken, string nextTokenHash) = GenerateToken();
        DateTime nextExpiresAt = now.AddDays(RefreshTokenLifetimeDays);
        refreshToken.IsRevoked = true;
        refreshToken.RevokedAt = now;
        refreshToken.RevokedByIp = NormalizeIp(ipAddress);
        refreshToken.ReplacedByToken = nextTokenHash;
        _context.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = refreshToken.UserId,
            Token = nextTokenHash,
            CreatedAt = now,
            ExpiresAt = nextExpiresAt,
            CreatedByIp = NormalizeIp(ipAddress)
        });

        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await _authAuditService.LogRefreshTokenAsync(refreshToken.UserId, ipAddress, cancellationToken: cancellationToken);

        return new AuthenticationResult(
            Success: true,
            Token: accessToken,
            ExpiresAt: now.AddDays(7),
            User: user,
            RefreshToken: nextRawToken,
            RefreshTokenExpires: nextExpiresAt);
    }

    public async Task<bool> RevokeAsync(
        string token,
        Guid userId,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetHash(token, out string tokenHash))
        {
            return false;
        }

        await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        RefreshToken? refreshToken = await _context.RefreshTokens
            .SingleOrDefaultAsync(candidate => candidate.Token == tokenHash && candidate.UserId == userId, cancellationToken);
        if (refreshToken is null || !HashesEqual(refreshToken.Token, tokenHash))
        {
            return false;
        }

        HashSet<string> visited = new(StringComparer.Ordinal) { tokenHash };
        while (refreshToken.IsRevoked && refreshToken.ReplacedByToken is not null)
        {
            string replacementHash = refreshToken.ReplacedByToken;
            if (!visited.Add(replacementHash))
            {
                return false;
            }

            refreshToken = await _context.RefreshTokens.SingleOrDefaultAsync(
                candidate => candidate.Token == replacementHash && candidate.UserId == userId,
                cancellationToken);
            if (refreshToken is null || !HashesEqual(refreshToken.Token, replacementHash))
            {
                return false;
            }
        }

        if (refreshToken.IsRevoked)
        {
            return false;
        }

        refreshToken.IsRevoked = true;
        refreshToken.RevokedAt = DateTime.UtcNow;
        refreshToken.RevokedByIp = NormalizeIp(ipAddress);
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task RevokeActiveTokensAsync(Guid userId, DateTime now, string? ipAddress, CancellationToken cancellationToken)
    {
        List<RefreshToken> activeTokens = await _context.RefreshTokens
            .Where(candidate => candidate.UserId == userId && !candidate.IsRevoked && candidate.ExpiresAt > now)
            .ToListAsync(cancellationToken);

        foreach (RefreshToken activeToken in activeTokens)
        {
            activeToken.IsRevoked = true;
            activeToken.RevokedAt = now;
            activeToken.RevokedByIp = NormalizeIp(ipAddress);
        }
    }

    private async Task<bool> IsRevokedByRevokeAllAsync(Guid userId, DateTime tokenCreatedAt, CancellationToken cancellationToken)
    {
        List<DateTime> markerTimes = await _context.RevokedTokens
            .Where(revocation => revocation.UserId == userId && revocation.TokenHash.StartsWith("ALL_TOKENS_"))
            .Select(revocation => revocation.RevokedAt)
            .ToListAsync(cancellationToken);

        return markerTimes.Any(marker => marker >= tokenCreatedAt);
    }

    private static (string RawToken, string TokenHash) GenerateToken()
    {
        byte[] tokenBytes = RandomNumberGenerator.GetBytes(RefreshTokenByteLength);
        return (EncodeBase64Url(tokenBytes), Convert.ToHexString(SHA256.HashData(tokenBytes)).ToLowerInvariant());
    }

    private static bool TryGetHash(string token, out string tokenHash)
    {
        tokenHash = string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            byte[] tokenBytes = DecodeBase64Url(token);
            if (tokenBytes.Length != RefreshTokenByteLength ||
                !string.Equals(EncodeBase64Url(tokenBytes), token, StringComparison.Ordinal))
            {
                return false;
            }

            tokenHash = Convert.ToHexString(SHA256.HashData(tokenBytes)).ToLowerInvariant();
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool HashesEqual(string storedHash, string suppliedHash) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(storedHash),
            Encoding.ASCII.GetBytes(suppliedHash));

    private static string EncodeBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] DecodeBase64Url(string value)
    {
        string base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += (base64.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Invalid base64url string.")
        };
        return Convert.FromBase64String(base64);
    }

    private static string NormalizeIp(string? ipAddress) =>
        string.IsNullOrWhiteSpace(ipAddress) ? "unknown" : ipAddress[..Math.Min(ipAddress.Length, 45)];

    private static AuthenticationResult InvalidResult() =>
        new(false, Error: InvalidRefreshTokenError);
}
