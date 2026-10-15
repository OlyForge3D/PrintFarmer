using Farm.Infrastructure;

namespace Farm.Infrastructure.Services.Authentication;

/// <summary>Creates, rotates, and revokes securely stored refresh tokens.</summary>
public interface IRefreshTokenService
{
    /// <summary>Creates a 30-day sliding refresh token for an authenticated user.</summary>
    Task<(string Token, DateTime ExpiresAt)> CreateAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Rotates a refresh token, returning a new access and refresh token on success.</summary>
    Task<AuthenticationResult> RotateAsync(string token, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Revokes a refresh token only when it belongs to the specified user.</summary>
    Task<bool> RevokeAsync(string token, Guid userId, string? ipAddress, CancellationToken cancellationToken = default);
}
