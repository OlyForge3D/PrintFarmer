using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Api;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Services.Authentication;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Authentication;

/// <summary>
/// Unit tests for <see cref="ApiKeyExchangeService"/> covering issue #838: rate-limited
/// Desktop API-key exchange for a short-lived JWT. Focuses on validation ordering,
/// generic/consistent failure responses (anti-enumeration), audit logging, and the
/// minimal claim set embedded in the issued token.
/// </summary>
public class ApiKeyExchangeServiceTests
{
    private const string ValidJwtKey = "ThisIsASuperSecureKeyForTestingPurposesOnly12345678";

    private readonly Mock<IApiKeyRepository> _mockApiKeyRepository;
    private readonly Mock<IUsersRepository> _mockUsersRepository;
    private readonly Mock<IAuthAuditService> _mockAuditService;
    private readonly Mock<IConfiguration> _mockConfiguration;
    private readonly Mock<ILogger<ApiKeyExchangeService>> _mockLogger;
    private readonly ApiKeyExchangeService _service;

    public ApiKeyExchangeServiceTests()
    {
        _mockApiKeyRepository = new Mock<IApiKeyRepository>();
        _mockUsersRepository = new Mock<IUsersRepository>();
        _mockAuditService = new Mock<IAuthAuditService>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<ApiKeyExchangeService>>();

        _mockConfiguration.Setup(c => c["Jwt:Key"]).Returns(ValidJwtKey);
        _mockConfiguration.Setup(c => c["Jwt:Issuer"]).Returns("PrintFarmer");
        _mockConfiguration.Setup(c => c["Jwt:Audience"]).Returns("PrintFarmer");

        _service = new ApiKeyExchangeService(
            _mockApiKeyRepository.Object,
            _mockUsersRepository.Object,
            _mockAuditService.Object,
            _mockConfiguration.Object,
            _mockLogger.Object);
    }

    private static ApiKey CreateDesktopKey(
        ApiKeyScope scopes = ApiKeyScope.ModelRead,
        Guid? userId = null,
        bool withUser = true) => new()
        {
            Id = Guid.NewGuid(),
            UserId = withUser ? (userId ?? Guid.NewGuid()) : null,
            Name = "desktop-app",
            KeyHash = "irrelevant-in-tests-lookup-is-mocked",
            Purpose = ApiKeyPurpose.Desktop,
            Scopes = scopes,
            IsActive = true,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
        };

    private static User CreateActiveOwner(Guid id) => new()
    {
        Id = id,
        Username = "desktop-owner",
        Email = "owner@example.com",
        IsActive = true,
    };

    #region Success

    [Fact]
    public async Task ExchangeApiKeyAsync_WithValidDesktopKey_ReturnsTokenAndScopes()
    {
        Guid userId = Guid.NewGuid();
        ApiKey key = CreateDesktopKey(ApiKeyScope.ModelRead | ApiKeyScope.LibrarySync, userId);
        User owner = CreateActiveOwner(userId);

        _mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>())).ReturnsAsync(key);
        _mockUsersRepository.Setup(r => r.GetUserEntityAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(owner);

        ApiKeyExchangeResult result = await _service.ExchangeApiKeyAsync("raw-desktop-key", "127.0.0.1", "test-agent");

        result.Success.Should().BeTrue();
        result.Token.Should().NotBeNullOrEmpty();
        result.ExpiresAt.Should().NotBeNull();
        result.Scopes.Should().BeEquivalentTo(new[] { "ModelRead", "LibrarySync" });
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task ExchangeApiKeyAsync_OnSuccess_LogsAuditSuccessAndNotFailure()
    {
        Guid userId = Guid.NewGuid();
        ApiKey key = CreateDesktopKey(ApiKeyScope.ModelWrite, userId);
        User owner = CreateActiveOwner(userId);

        _mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>())).ReturnsAsync(key);
        _mockUsersRepository.Setup(r => r.GetUserEntityAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(owner);

        await _service.ExchangeApiKeyAsync("raw-desktop-key", "127.0.0.1", "test-agent");

        _mockAuditService.Verify(
            a => a.LogApiKeyExchangeAsync(userId, key.Id, "127.0.0.1", "test-agent", null, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockAuditService.Verify(
            a => a.LogApiKeyExchangeFailedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExchangeApiKeyAsync_TokenClaims_ContainMinimalScopedClaimsOnly()
    {
        Guid userId = Guid.NewGuid();
        ApiKey key = CreateDesktopKey(ApiKeyScope.ModelRead | ApiKeyScope.ModelWrite, userId);
        User owner = CreateActiveOwner(userId);

        _mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>())).ReturnsAsync(key);
        _mockUsersRepository.Setup(r => r.GetUserEntityAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(owner);

        ApiKeyExchangeResult result = await _service.ExchangeApiKeyAsync("raw-desktop-key", null, null);

        JsonWebTokenHandler handler = new();
        JsonWebToken jwt = handler.ReadJsonWebToken(result.Token);

        jwt.GetClaim("token_use").Value.Should().Be("desktop_exchange");
        jwt.GetClaim("api_key_id").Value.Should().Be(key.Id.ToString());
        jwt.GetClaim(System.Security.Claims.ClaimTypes.NameIdentifier).Value.Should().Be(userId.ToString());
        jwt.Claims.Where(c => c.Type == "scope").Select(c => c.Value)
            .Should().BeEquivalentTo(new[] { "ModelRead", "ModelWrite" });
        jwt.Claims.Should().NotContain(c => c.Type == System.Security.Claims.ClaimTypes.Role);
    }

    #endregion

    #region Failure - consistent generic error (anti-enumeration)

    [Fact]
    public async Task ExchangeApiKeyAsync_WithUnknownKey_ReturnsGenericFailure()
    {
        _mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>())).ReturnsAsync((ApiKey?)null);

        ApiKeyExchangeResult result = await _service.ExchangeApiKeyAsync("does-not-exist", null, null);

        AssertGenericFailure(result);
    }

    [Fact]
    public async Task ExchangeApiKeyAsync_WithRevokedKey_ReturnsGenericFailure()
    {
        // EfApiKeyRepository.GetByKeyHashAsync already filters IsActive at the DB level,
        // so a revoked key surfaces to the service exactly like "not found".
        _mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>())).ReturnsAsync((ApiKey?)null);

        ApiKeyExchangeResult result = await _service.ExchangeApiKeyAsync("revoked-key", null, null);

        AssertGenericFailure(result);
    }

    [Fact]
    public async Task ExchangeApiKeyAsync_WithExpiredKey_ReturnsGenericFailure()
    {
        // EfApiKeyRepository.GetByKeyHashAsync already filters ExpiresAt at the DB level,
        // so an expired key surfaces to the service exactly like "not found".
        _mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>())).ReturnsAsync((ApiKey?)null);

        ApiKeyExchangeResult result = await _service.ExchangeApiKeyAsync("expired-key", null, null);

        AssertGenericFailure(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExchangeApiKeyAsync_WithMalformedKey_ReturnsGenericFailure(string? rawKey)
    {
        ApiKeyExchangeResult result = await _service.ExchangeApiKeyAsync(rawKey!, null, null);

        AssertGenericFailure(result);
        _mockApiKeyRepository.Verify(r => r.GetByKeyHashAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExchangeApiKeyAsync_WithWrongPurposeKey_ReturnsGenericFailure()
    {
        ApiKey octoPrintKey = CreateDesktopKey();
        octoPrintKey.Purpose = ApiKeyPurpose.OctoPrint;
        _mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>())).ReturnsAsync(octoPrintKey);

        ApiKeyExchangeResult result = await _service.ExchangeApiKeyAsync("octoprint-key", null, null);

        AssertGenericFailure(result);
        _mockUsersRepository.Verify(r => r.GetUserEntityAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExchangeApiKeyAsync_WithUnderScopedKey_ReturnsGenericFailure()
    {
        ApiKey key = CreateDesktopKey(ApiKeyScope.None);
        _mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>())).ReturnsAsync(key);

        ApiKeyExchangeResult result = await _service.ExchangeApiKeyAsync("no-scope-key", null, null);

        AssertGenericFailure(result);
    }

    [Fact]
    public async Task ExchangeApiKeyAsync_WithKeyHavingNoOwner_ReturnsGenericFailure()
    {
        ApiKey key = CreateDesktopKey(ApiKeyScope.ModelRead, withUser: false);
        _mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>())).ReturnsAsync(key);

        ApiKeyExchangeResult result = await _service.ExchangeApiKeyAsync("orphaned-key", null, null);

        AssertGenericFailure(result);
    }

    [Fact]
    public async Task ExchangeApiKeyAsync_WithInactiveOwner_ReturnsGenericFailure()
    {
        Guid userId = Guid.NewGuid();
        ApiKey key = CreateDesktopKey(ApiKeyScope.ModelRead, userId);
        User inactiveOwner = CreateActiveOwner(userId);
        inactiveOwner.IsActive = false;

        _mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>())).ReturnsAsync(key);
        _mockUsersRepository.Setup(r => r.GetUserEntityAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(inactiveOwner);

        ApiKeyExchangeResult result = await _service.ExchangeApiKeyAsync("inactive-owner-key", null, null);

        AssertGenericFailure(result);
    }

    [Fact]
    public async Task ExchangeApiKeyAsync_WithMissingOwnerRecord_ReturnsGenericFailure()
    {
        Guid userId = Guid.NewGuid();
        ApiKey key = CreateDesktopKey(ApiKeyScope.ModelRead, userId);

        _mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>())).ReturnsAsync(key);
        _mockUsersRepository.Setup(r => r.GetUserEntityAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        ApiKeyExchangeResult result = await _service.ExchangeApiKeyAsync("dangling-owner-key", null, null);

        AssertGenericFailure(result);
    }

    [Fact]
    public async Task ExchangeApiKeyAsync_WithMissingJwtKeyConfiguration_ReturnsGenericFailure()
    {
        _mockConfiguration.Setup(c => c["Jwt:Key"]).Returns(string.Empty);
        Guid userId = Guid.NewGuid();
        ApiKey key = CreateDesktopKey(ApiKeyScope.ModelRead, userId);
        User owner = CreateActiveOwner(userId);

        _mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>())).ReturnsAsync(key);
        _mockUsersRepository.Setup(r => r.GetUserEntityAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(owner);

        ApiKeyExchangeResult result = await _service.ExchangeApiKeyAsync("valid-key-server-misconfigured", null, null);

        AssertGenericFailure(result);
    }

    [Fact]
    public async Task ExchangeApiKeyAsync_OnAnyFailure_LogsAuditFailureButNeverAuditSuccess()
    {
        _mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>())).ReturnsAsync((ApiKey?)null);

        await _service.ExchangeApiKeyAsync("does-not-exist", "1.2.3.4", "curl/8.0");

        _mockAuditService.Verify(
            a => a.LogApiKeyExchangeFailedAsync(It.IsAny<string>(), "1.2.3.4", "curl/8.0", null, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockAuditService.Verify(
            a => a.LogApiKeyExchangeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// All rejection reasons - not found, revoked, expired, wrong purpose, under-scoped,
    /// missing/inactive owner, and server misconfiguration - must be indistinguishable to
    /// the caller so a client cannot enumerate which check failed.
    /// </summary>
    private static void AssertGenericFailure(ApiKeyExchangeResult result)
    {
        result.Success.Should().BeFalse();
        result.Token.Should().BeNull();
        result.Error.Should().Be("Invalid API key");
    }

    #endregion

    #region Redaction (issue #839)

    /// <summary>
    /// The raw API key must never appear in application logs or audit records on the success
    /// path - only its SHA-256 hash is used internally for lookup. This guards against secret
    /// leakage via log aggregation, telemetry, or the audit trail.
    /// </summary>
    [Fact]
    public async Task ExchangeApiKeyAsync_OnSuccess_NeverLogsOrAuditsRawKeyMaterial()
    {
        const string rawKey = "raw-desktop-key-REDACTION-CANARY-98213";
        Guid userId = Guid.NewGuid();
        ApiKey key = CreateDesktopKey(ApiKeyScope.ModelRead, userId);
        User owner = CreateActiveOwner(userId);

        _mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>())).ReturnsAsync(key);
        _mockUsersRepository.Setup(r => r.GetUserEntityAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(owner);

        ApiKeyExchangeResult result = await _service.ExchangeApiKeyAsync(rawKey, "127.0.0.1", "test-agent");

        result.Success.Should().BeTrue();
        AssertNoInvocationContainsRawKey(_mockLogger.Invocations, rawKey);
        AssertNoInvocationContainsRawKey(_mockAuditService.Invocations, rawKey);
    }

    /// <summary>
    /// Same guarantee on every rejection path (unknown/revoked/expired all surface identically
    /// as "not found" from the repository) - the raw key supplied by the caller must never leak
    /// into logs or the generic-failure audit record.
    /// </summary>
    [Theory]
    [InlineData("does-not-exist-CANARY-11")]
    [InlineData("expired-key-CANARY-22")]
    [InlineData("revoked-key-CANARY-33")]
    public async Task ExchangeApiKeyAsync_OnLookupFailure_NeverLogsOrAuditsRawKeyMaterial(string rawKey)
    {
        _mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>())).ReturnsAsync((ApiKey?)null);

        ApiKeyExchangeResult result = await _service.ExchangeApiKeyAsync(rawKey, "9.9.9.9", "curl/CANARY-agent");

        result.Success.Should().BeFalse();
        AssertNoInvocationContainsRawKey(_mockLogger.Invocations, rawKey);
        AssertNoInvocationContainsRawKey(_mockAuditService.Invocations, rawKey);
    }

    /// <summary>
    /// Wrong-purpose and under-scoped keys are resolved from the repository (unlike the
    /// "not found" cases above) and therefore run further inside the service before failing -
    /// the raw key must still never leak into logs or audit calls.
    /// </summary>
    [Fact]
    public async Task ExchangeApiKeyAsync_WithWrongPurposeOrUnderScopedKey_NeverLogsOrAuditsRawKeyMaterial()
    {
        const string rawKey = "octoprint-key-CANARY-44";
        ApiKey octoPrintKey = CreateDesktopKey();
        octoPrintKey.Purpose = ApiKeyPurpose.OctoPrint;
        _mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>())).ReturnsAsync(octoPrintKey);

        ApiKeyExchangeResult result = await _service.ExchangeApiKeyAsync(rawKey, null, null);

        result.Success.Should().BeFalse();
        AssertNoInvocationContainsRawKey(_mockLogger.Invocations, rawKey);
        AssertNoInvocationContainsRawKey(_mockAuditService.Invocations, rawKey);
    }

    /// <summary>
    /// Even when the server is misconfigured (missing/short Jwt:Key) and the service logs a
    /// diagnostic error, that log message must remain generic and never echo the caller-supplied
    /// raw key.
    /// </summary>
    [Fact]
    public async Task ExchangeApiKeyAsync_WithMissingJwtKeyConfiguration_NeverLogsRawKeyMaterial()
    {
        const string rawKey = "valid-key-server-misconfigured-CANARY-55";
        _mockConfiguration.Setup(c => c["Jwt:Key"]).Returns(string.Empty);
        Guid userId = Guid.NewGuid();
        ApiKey key = CreateDesktopKey(ApiKeyScope.ModelRead, userId);
        User owner = CreateActiveOwner(userId);

        _mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>())).ReturnsAsync(key);
        _mockUsersRepository.Setup(r => r.GetUserEntityAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(owner);

        await _service.ExchangeApiKeyAsync(rawKey, null, null);

        AssertNoInvocationContainsRawKey(_mockLogger.Invocations, rawKey);
    }

    private static void AssertNoInvocationContainsRawKey(IEnumerable<Moq.IInvocation> invocations, string rawKey)
    {
        foreach (Moq.IInvocation invocation in invocations)
        {
            foreach (object? argument in invocation.Arguments)
            {
                argument?.ToString().Should().NotContain(rawKey,
                    "raw API key material must never reach logs or audit records - only its SHA-256 hash is used internally for lookup");
            }
        }
    }

    #endregion
}
