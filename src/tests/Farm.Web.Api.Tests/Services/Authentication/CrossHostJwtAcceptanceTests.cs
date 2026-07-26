using System.Security.Claims;
using System.Text;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Api;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Services.Authentication;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Authentication;

/// <summary>
/// Verifies the "cross-host JWT acceptance" guarantee behind issue #838/#839: a token minted by
/// <see cref="ApiKeyExchangeService"/> (which only ever runs in the main API host) must validate
/// successfully against the standalone <c>Farm.Slicer.Host</c>'s JWT bearer scheme, which is
/// configured independently but with the *same* <c>Jwt:Key</c>/<c>Jwt:Issuer</c>/<c>Jwt:Audience</c>
/// values (see <c>Farm.Slicer.Host/Program.cs</c>). Rather than standing up a second ASP.NET Core
/// host in-process, this test reconstructs the exact <see cref="TokenValidationParameters"/> the
/// slicer host builds and asserts the exchanged token passes them - proving the two hosts agree on
/// the token format/signature/claims without the fragility of a second <c>WebApplicationFactory</c>.
/// </summary>
public class CrossHostJwtAcceptanceTests
{
    private const string SharedJwtKey = "ThisIsASuperSecureKeyForTestingPurposesOnly12345678";
    private const string SharedIssuer = "PrintFarmer";
    private const string SharedAudience = "PrintFarmer";

    [Fact]
    public async Task DesktopExchangeToken_ValidatesAgainstSlicerHostTokenValidationParameters()
    {
        // Arrange: mint a token exactly as the main API's exchange endpoint would.
        Guid userId = Guid.NewGuid();
        ApiKey key = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "desktop-app",
            KeyHash = "irrelevant-lookup-is-mocked",
            Purpose = ApiKeyPurpose.Desktop,
            Scopes = ApiKeyScope.ModelRead | ApiKeyScope.LibrarySync,
            IsActive = true,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
        };
        User owner = new() { Id = userId, Username = "desktop-owner", Email = "owner@example.com", IsActive = true };

        var mockApiKeyRepository = new Mock<IApiKeyRepository>();
        var mockUsersRepository = new Mock<IUsersRepository>();
        var mockAuditService = new Mock<IAuthAuditService>();
        var mockConfiguration = new Mock<IConfiguration>();
        var mockLogger = new Mock<ILogger<ApiKeyExchangeService>>();

        mockConfiguration.Setup(c => c["Jwt:Key"]).Returns(SharedJwtKey);
        mockConfiguration.Setup(c => c["Jwt:Issuer"]).Returns(SharedIssuer);
        mockConfiguration.Setup(c => c["Jwt:Audience"]).Returns(SharedAudience);
        mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>())).ReturnsAsync(key);
        mockUsersRepository.Setup(r => r.GetUserEntityAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(owner);

        var exchangeService = new ApiKeyExchangeService(
            mockApiKeyRepository.Object, mockUsersRepository.Object, mockAuditService.Object,
            mockConfiguration.Object, mockLogger.Object);

        ApiKeyExchangeResult result = await exchangeService.ExchangeApiKeyAsync("raw-desktop-key", "127.0.0.1", "test-agent");
        result.Success.Should().BeTrue();

        // Act: validate the token using the exact TokenValidationParameters that
        // Farm.Slicer.Host/Program.cs constructs for its "Bearer" scheme when Jwt:Key is
        // configured (same Jwt:Key/Issuer/Audience sourced from shared configuration, as the
        // slicer host is designed to share JWT signing config with the main API).
        var slicerHostValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SharedJwtKey)),
            ValidateIssuer = true,
            ValidIssuer = SharedIssuer,
            ValidateAudience = true,
            ValidAudience = SharedAudience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        };

        var handler = new JsonWebTokenHandler();
        Microsoft.IdentityModel.Tokens.TokenValidationResult validationResult =
            await handler.ValidateTokenAsync(result.Token, slicerHostValidationParameters);

        // Assert: the slicer host would accept the token and see the same scoped claims.
        validationResult.Exception.Should().BeNull();
        validationResult.IsValid.Should().BeTrue();
        ClaimsIdentity identity = validationResult.ClaimsIdentity;
        identity.FindFirst("token_use")?.Value.Should().Be("desktop_exchange");
        identity.FindFirst("api_key_id")?.Value.Should().Be(key.Id.ToString());
        identity.FindFirst(ClaimTypes.NameIdentifier)?.Value.Should().Be(userId.ToString());
        identity.FindAll("scope").Select(c => c.Value).Should().BeEquivalentTo(new[] { "ModelRead", "LibrarySync" });
    }

    /// <summary>
    /// If the slicer host is deployed with a *different* Jwt:Key than the main API (a
    /// misconfiguration, not the designed shared-secret scenario), the exchanged token must be
    /// rejected - proving the "same shared secret" precondition is load-bearing and the slicer
    /// host doesn't accept arbitrary tokens.
    /// </summary>
    [Fact]
    public async Task DesktopExchangeToken_WithMismatchedSlicerHostKey_FailsValidation()
    {
        Guid userId = Guid.NewGuid();
        ApiKey key = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "desktop-app",
            KeyHash = "irrelevant-lookup-is-mocked",
            Purpose = ApiKeyPurpose.Desktop,
            Scopes = ApiKeyScope.ModelRead,
            IsActive = true,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
        };
        User owner = new() { Id = userId, Username = "desktop-owner", Email = "owner@example.com", IsActive = true };

        var mockApiKeyRepository = new Mock<IApiKeyRepository>();
        var mockUsersRepository = new Mock<IUsersRepository>();
        var mockAuditService = new Mock<IAuthAuditService>();
        var mockConfiguration = new Mock<IConfiguration>();
        var mockLogger = new Mock<ILogger<ApiKeyExchangeService>>();

        mockConfiguration.Setup(c => c["Jwt:Key"]).Returns(SharedJwtKey);
        mockConfiguration.Setup(c => c["Jwt:Issuer"]).Returns(SharedIssuer);
        mockConfiguration.Setup(c => c["Jwt:Audience"]).Returns(SharedAudience);
        mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>())).ReturnsAsync(key);
        mockUsersRepository.Setup(r => r.GetUserEntityAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(owner);

        var exchangeService = new ApiKeyExchangeService(
            mockApiKeyRepository.Object, mockUsersRepository.Object, mockAuditService.Object,
            mockConfiguration.Object, mockLogger.Object);

        ApiKeyExchangeResult result = await exchangeService.ExchangeApiKeyAsync("raw-desktop-key", null, null);
        result.Success.Should().BeTrue();

        var mismatchedKeyValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("ADifferentSecureKeyNotSharedWithMainApi9999999999")),
            ValidateIssuer = true,
            ValidIssuer = SharedIssuer,
            ValidateAudience = true,
            ValidAudience = SharedAudience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        };

        var handler = new JsonWebTokenHandler();
        Microsoft.IdentityModel.Tokens.TokenValidationResult validationResult =
            await handler.ValidateTokenAsync(result.Token, mismatchedKeyValidationParameters);

        validationResult.IsValid.Should().BeFalse();
        validationResult.Exception.Should().NotBeNull();
    }
}
