using System.Security.Claims;
using System.Text;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Authentication;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Authentication;

/// <summary>
/// Unit tests for <see cref="TokenRevocationService.IsTokenRevokedAsync"/>, focusing on the
/// sub-second race between a "revoke all" marker and a JWT's second-resolution "nbf" claim.
/// See issue #1470: JWT nbf is only second-resolution, so comparing it against a full-precision
/// RevokedAt timestamp with a strict "&gt;" is ambiguous whenever both events land in the same
/// second.
/// </summary>
public class TokenRevocationServiceTests
{
    private const string SigningKey = "ThisIsASuperSecureKeyForTestingPurposesOnly12345678";
    private const string Issuer = "PrintFarmer";
    private const string Audience = "PrintFarmer";

    private static string CreateToken(Guid userId, DateTime notBefore, DateTime expires)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, userId.ToString())];

        SecurityTokenDescriptor descriptor = new()
        {
            Subject = new ClaimsIdentity(claims),
            NotBefore = notBefore,
            Expires = expires,
            Issuer = Issuer,
            Audience = Audience,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
                SecurityAlgorithms.HmacSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static async Task<(TokenRevocationService Service, AppDbContext Db)> CreateServiceAsync(Guid userId)
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        AppDbContext db = new(options);

        db.Users.Add(new User
        {
            Id = userId,
            Username = $"user_{userId:N}",
            Email = $"user_{userId:N}@example.com",
            PasswordHash = "not-a-real-hash",
            IsActive = true,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        Mock<ILogger<TokenRevocationService>> logger = new();
        Mock<IAuthAuditService> authAuditService = new();
        authAuditService
            .Setup(s => s.LogTokenRevokedAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        TokenRevocationService service = new(db, logger.Object, authAuditService.Object);
        return (service, db);
    }

    [Fact]
    public async Task IsTokenRevokedAsync_MarkerTiesWithFlooredNbf_TreatsTokenAsRevoked()
    {
        // Arrange - the revocation marker's precise timestamp lands exactly on the whole
        // second that the token's nbf claim floors down to. Under the previous strict ">"
        // comparison this tie would NOT be caught, letting the token survive its own
        // revoke-all. It must now be treated as revoked.
        Guid userId = Guid.NewGuid();
        (TokenRevocationService service, AppDbContext db) = await CreateServiceAsync(userId);

        DateTime secondBoundary = new(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        string token = CreateToken(userId, notBefore: secondBoundary, expires: secondBoundary.AddDays(1));

        db.RevokedTokens.Add(new RevokedToken
        {
            Id = Guid.NewGuid(),
            TokenHash = $"ALL_TOKENS_{userId}_{secondBoundary.Ticks}",
            UserId = userId,
            RevokedAt = secondBoundary, // exact tie with the token's floored nbf
            Reason = "revoke all - tie test",
            ExpiresAt = secondBoundary.AddDays(30),
        });
        await db.SaveChangesAsync();

        // Act
        bool isRevoked = await service.IsTokenRevokedAsync(token);

        // Assert
        isRevoked.Should().BeTrue("a revoke-all marker at the exact same second as the token's nbf must close the race deterministically");
    }

    [Fact]
    public async Task IsTokenRevokedAsync_MarkerWithinSameSecondButFractionalBeforeNbf_TreatsTokenAsRevoked()
    {
        // Arrange - marker created slightly before the token's floored nbf second boundary
        // but still within the same wall-clock second window once truncated. This mirrors the
        // real production case where RevokedAt keeps sub-second precision while nbf does not.
        Guid userId = Guid.NewGuid();
        (TokenRevocationService service, AppDbContext db) = await CreateServiceAsync(userId);

        DateTime secondBoundary = new(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        string token = CreateToken(userId, notBefore: secondBoundary, expires: secondBoundary.AddDays(1));

        db.RevokedTokens.Add(new RevokedToken
        {
            Id = Guid.NewGuid(),
            TokenHash = $"ALL_TOKENS_{userId}_{secondBoundary.Ticks}",
            UserId = userId,
            RevokedAt = secondBoundary.AddMilliseconds(400), // same second, later fraction
            Reason = "revoke all - same second test",
            ExpiresAt = secondBoundary.AddDays(30),
        });
        await db.SaveChangesAsync();

        // Act
        bool isRevoked = await service.IsTokenRevokedAsync(token);

        // Assert
        isRevoked.Should().BeTrue();
    }

    [Fact]
    public async Task IsTokenRevokedAsync_MarkerBeforeTokenSecond_DoesNotRevokeToken()
    {
        // Arrange - marker's second bucket is strictly before the token's nbf second bucket,
        // so the token was legitimately (re)issued after the revoke-all completed and must
        // remain valid.
        Guid userId = Guid.NewGuid();
        (TokenRevocationService service, AppDbContext db) = await CreateServiceAsync(userId);

        DateTime revokedSecond = new(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        DateTime tokenSecond = revokedSecond.AddSeconds(1);
        string token = CreateToken(userId, notBefore: tokenSecond, expires: tokenSecond.AddDays(1));

        db.RevokedTokens.Add(new RevokedToken
        {
            Id = Guid.NewGuid(),
            TokenHash = $"ALL_TOKENS_{userId}_{revokedSecond.Ticks}",
            UserId = userId,
            RevokedAt = revokedSecond,
            Reason = "revoke all - earlier second test",
            ExpiresAt = revokedSecond.AddDays(30),
        });
        await db.SaveChangesAsync();

        // Act
        bool isRevoked = await service.IsTokenRevokedAsync(token);

        // Assert
        isRevoked.Should().BeFalse("a token issued in a strictly later second than the revoke-all marker must remain valid");
    }
}
