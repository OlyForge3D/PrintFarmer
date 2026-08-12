using Farm.Infrastructure;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Api;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Security;
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
            a => a.LogApiKeyExchangeAsync(userId, key.Id, "127.0.0.1", "test-agent", It.IsAny<ApiKeyExchangeScopeAudit?>(), null, It.IsAny<CancellationToken>()),
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
            a => a.LogApiKeyExchangeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<ApiKeyExchangeScopeAudit?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
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

    #region Least-privilege permission claims (owner-authorization intersection)

    /// <summary>
    /// Stubs the owner's live authorization. Roles/grants are only queried on the exchange path,
    /// so tests covering legacy model-only keys never need to set these up.
    /// </summary>
    private void SetOwnerAuthorization(
        Guid userId,
        IEnumerable<string>? roles = null,
        IEnumerable<string>? permissions = null,
        IEnumerable<string>? denied = null)
    {
        _mockUsersRepository
            .Setup(r => r.GetActiveRoleNamesAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. roles ?? []]);
        _mockUsersRepository
            .Setup(r => r.GetGrantedPermissionsAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. (permissions ?? []).Select(p =>
            {
                (string resource, string action) = PrintFarmerPermissions.Split(p);
                return (resource, action);
            })]);
        _mockUsersRepository
            .Setup(r => r.GetDeniedPermissionsAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. (denied ?? []).Select(p =>
            {
                (string resource, string action) = PrintFarmerPermissions.Split(p);
                return (resource, action);
            })]);
    }

    private (JsonWebToken Jwt, ApiKeyExchangeResult Result) ExchangeAndRead(ApiKey key, User owner)
    {
        _mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>())).ReturnsAsync(key);
        _mockUsersRepository.Setup(r => r.GetUserEntityAsync(owner.Id, It.IsAny<CancellationToken>())).ReturnsAsync(owner);

        ApiKeyExchangeResult result = _service.ExchangeApiKeyAsync("raw-desktop-key", null, null).GetAwaiter().GetResult();
        result.Success.Should().BeTrue();

        JsonWebTokenHandler handler = new();
        return (handler.ReadJsonWebToken(result.Token), result);
    }

    private static IEnumerable<string> PermissionClaims(JsonWebToken jwt) =>
        jwt.Claims.Where(c => c.Type == PrintFarmerPermissions.ClaimType).Select(c => c.Value);

    private static IEnumerable<string> ScopeClaims(JsonWebToken jwt) =>
        jwt.Claims.Where(c => c.Type == "scope").Select(c => c.Value);

    /// <summary>
    /// Every selected flag must emit its scope claim <b>and</b> exactly its one mapped permission.
    /// </summary>
    [Theory]
    [InlineData(ApiKeyScope.CalibrationRead, "CalibrationRead", "calibration:read")]
    [InlineData(ApiKeyScope.CalibrationCreate, "CalibrationCreate", "calibration:create")]
    [InlineData(ApiKeyScope.CalibrationUpdate, "CalibrationUpdate", "calibration:update")]
    [InlineData(ApiKeyScope.CalibrationDelete, "CalibrationDelete", "calibration:delete")]
    [InlineData(ApiKeyScope.CalibrationGenerate, "CalibrationGenerate", "calibration:generate")]
    [InlineData(ApiKeyScope.CalibrationPublish, "CalibrationPublish", "calibration:publish")]
    [InlineData(ApiKeyScope.SlicingSubmit, "SlicingSubmit", "slicing:submit")]
    [InlineData(ApiKeyScope.SlicingReadArtifact, "SlicingReadArtifact", "slicing:read-artifact")]
    [InlineData(ApiKeyScope.QueueRead, "QueueRead", "queue:read")]
    [InlineData(ApiKeyScope.QueueWrite, "QueueWrite", "queue:write")]
    [InlineData(ApiKeyScope.QueueStart, "QueueStart", "queue:start")]
    [InlineData(ApiKeyScope.QueueCancel, "QueueCancel", "queue:cancel")]
    [InlineData(ApiKeyScope.QueueAcknowledgeBedClear, "QueueAcknowledgeBedClear", "queue:acknowledge-bed-clear")]
    public void ExchangeApiKeyAsync_EachAuthorizedScope_EmitsItsScopeAndExactlyItsPermission(
        ApiKeyScope scope,
        string expectedScopeName,
        string expectedPermission)
    {
        Guid userId = Guid.NewGuid();
        SetOwnerAuthorization(userId, permissions: [expectedPermission]);

        (JsonWebToken jwt, ApiKeyExchangeResult result) =
            ExchangeAndRead(CreateDesktopKey(scope, userId), CreateActiveOwner(userId));

        ScopeClaims(jwt).Should().Equal(expectedScopeName);
        PermissionClaims(jwt).Should().Equal(expectedPermission);
        result.Scopes.Should().Equal(expectedScopeName);
        jwt.Claims.Should().NotContain(c => c.Type == System.Security.Claims.ClaimTypes.Role);
    }

    /// <summary>
    /// Deny wiring is load-bearing on the <b>exchange</b> path too. An owner whose
    /// <c>calibration:admin</c> grant is overridden by an explicit deny on one action must lose
    /// exactly that scope - in both claim families - while the neighboring scope the same admin
    /// grant covers survives. Removing <c>GetDeniedPermissionsAsync</c> from this service would
    /// mint <c>calibration:publish</c> against an operator's explicit deny.
    /// </summary>
    [Fact]
    public void ExchangeApiKeyAsync_ResourceAdminOwnerWithExplicitDeny_DropsOnlyTheDeniedScope()
    {
        Guid userId = Guid.NewGuid();
        SetOwnerAuthorization(
            userId,
            permissions: ["calibration:admin"],
            denied: [PrintFarmerPermissions.Calibration.Publish]);

        (JsonWebToken jwt, ApiKeyExchangeResult result) = ExchangeAndRead(
            CreateDesktopKey(ApiKeyScope.CalibrationRead | ApiKeyScope.CalibrationPublish, userId),
            CreateActiveOwner(userId));

        ScopeClaims(jwt).Should().Equal("CalibrationRead");
        PermissionClaims(jwt).Should().Equal(PrintFarmerPermissions.Calibration.Read);
        result.Scopes.Should().Equal("CalibrationRead");
        _mockUsersRepository.Verify(
            r => r.GetDeniedPermissionsAsync(userId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A permission the owner holds but did not select on the key must never be minted.
    /// </summary>
    [Fact]
    public void ExchangeApiKeyAsync_UnselectedOwnerPermissions_AreNeverEmitted()
    {
        Guid userId = Guid.NewGuid();
        SetOwnerAuthorization(userId, permissions:
        [
            PrintFarmerPermissions.Calibration.Read,
            PrintFarmerPermissions.Calibration.Delete,
            PrintFarmerPermissions.Queue.Start,
            PrintFarmerPermissions.Slicing.Promote,
        ]);

        (JsonWebToken jwt, _) = ExchangeAndRead(
            CreateDesktopKey(ApiKeyScope.CalibrationRead, userId),
            CreateActiveOwner(userId));

        PermissionClaims(jwt).Should().Equal(PrintFarmerPermissions.Calibration.Read);
        ScopeClaims(jwt).Should().Equal("CalibrationRead");
    }

    /// <summary>
    /// An ordinary owner without the live grant cannot mint the permission - the whole exchange
    /// fails when nothing survives the intersection.
    /// </summary>
    [Fact]
    public async Task ExchangeApiKeyAsync_OwnerWithoutLiveGrant_CannotMintPermission()
    {
        Guid userId = Guid.NewGuid();
        SetOwnerAuthorization(userId);
        ApiKey key = CreateDesktopKey(ApiKeyScope.CalibrationRead, userId);

        _mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>())).ReturnsAsync(key);
        _mockUsersRepository.Setup(r => r.GetUserEntityAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateActiveOwner(userId));

        ApiKeyExchangeResult result = await _service.ExchangeApiKeyAsync("raw-desktop-key", null, null);

        AssertGenericFailure(result);
    }

    /// <summary>
    /// Downgrade, not hard failure: revoking a calibration grant must not break unrelated model
    /// sync, and the revoked scope must disappear from both claim families.
    /// </summary>
    [Fact]
    public void ExchangeApiKeyAsync_AfterRevocation_DowngradesInsteadOfBreakingModelScopes()
    {
        Guid userId = Guid.NewGuid();
        SetOwnerAuthorization(userId);

        (JsonWebToken jwt, ApiKeyExchangeResult result) = ExchangeAndRead(
            CreateDesktopKey(ApiKeyScope.ModelRead | ApiKeyScope.LibrarySync | ApiKeyScope.CalibrationRead, userId),
            CreateActiveOwner(userId));

        ScopeClaims(jwt).Should().BeEquivalentTo(new[] { "ModelRead", "LibrarySync" });
        ScopeClaims(jwt).Should().NotContain("CalibrationRead");
        PermissionClaims(jwt).Should().BeEmpty();
        result.Scopes.Should().BeEquivalentTo(new[] { "ModelRead", "LibrarySync" });
    }

    /// <summary>
    /// The revocation must be observed on the next exchange, not cached from the first.
    /// </summary>
    [Fact]
    public async Task ExchangeApiKeyAsync_RevocationBetweenExchanges_RemovesTheClaimOnReExchange()
    {
        Guid userId = Guid.NewGuid();
        ApiKey key = CreateDesktopKey(ApiKeyScope.ModelRead | ApiKeyScope.CalibrationRead, userId);
        _mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>())).ReturnsAsync(key);
        _mockUsersRepository.Setup(r => r.GetUserEntityAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateActiveOwner(userId));

        SetOwnerAuthorization(userId, permissions: [PrintFarmerPermissions.Calibration.Read]);
        ApiKeyExchangeResult before = await _service.ExchangeApiKeyAsync("raw-desktop-key", null, null);
        before.Scopes.Should().Contain("CalibrationRead");

        SetOwnerAuthorization(userId);
        ApiKeyExchangeResult after = await _service.ExchangeApiKeyAsync("raw-desktop-key", null, null);

        after.Success.Should().BeTrue();
        after.Scopes.Should().NotContain("CalibrationRead");
        JsonWebToken jwt = new JsonWebTokenHandler().ReadJsonWebToken(after.Token);
        PermissionClaims(jwt).Should().BeEmpty();
    }

    /// <summary>
    /// A farm_admin owner authorizes the selected permission but must never lend the token the
    /// admin role - otherwise every permission check would be bypassed wholesale.
    /// </summary>
    [Fact]
    public void ExchangeApiKeyAsync_FarmAdminOwner_GetsOnlySelectedPermissionAndNoAdminRole()
    {
        Guid userId = Guid.NewGuid();
        SetOwnerAuthorization(userId, roles: [PrintFarmerPermissions.FarmAdminRole]);

        (JsonWebToken jwt, _) = ExchangeAndRead(
            CreateDesktopKey(ApiKeyScope.CalibrationRead, userId),
            CreateActiveOwner(userId));

        PermissionClaims(jwt).Should().Equal(PrintFarmerPermissions.Calibration.Read);
        jwt.Claims.Should().NotContain(c => c.Type == System.Security.Claims.ClaimTypes.Role);
        jwt.Claims.Should().NotContain(c => c.Value == PrintFarmerPermissions.FarmAdminRole);
        PermissionClaims(jwt).Should().NotContain(PrintFarmerPermissions.Calibration.Delete);
    }

    /// <summary>
    /// Every pre-existing key - stored as 1, 2, 4 or the frozen aggregate 7 - must yield zero
    /// permission claims and must never expand into the bogus scope name "All".
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(7)]
    public void ExchangeApiKeyAsync_LegacyNumericKeys_YieldNoPermissionClaims(int storedScopes)
    {
        Guid userId = Guid.NewGuid();

        (JsonWebToken jwt, ApiKeyExchangeResult result) = ExchangeAndRead(
            CreateDesktopKey((ApiKeyScope)storedScopes, userId),
            CreateActiveOwner(userId));

        PermissionClaims(jwt).Should().BeEmpty();
        ScopeClaims(jwt).Should().NotContain("All");
        result.Scopes.Should().NotContain("All");
        jwt.Claims.Should().NotContain(c => c.Type == System.Security.Claims.ClaimTypes.Role);
    }

    /// <summary>
    /// A key persisted with a bit outside the known mask cannot be interpreted safely and must be
    /// rejected rather than silently masked down.
    /// </summary>
    [Theory]
    [InlineData(1 << 30)]
    [InlineData(-1)]
    public async Task ExchangeApiKeyAsync_WithUndefinedStoredBits_IsRejected(int storedScopes)
    {
        Guid userId = Guid.NewGuid();
        _mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>()))
            .ReturnsAsync(CreateDesktopKey((ApiKeyScope)storedScopes, userId));
        _mockUsersRepository.Setup(r => r.GetUserEntityAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateActiveOwner(userId));

        ApiKeyExchangeResult result = await _service.ExchangeApiKeyAsync("raw-desktop-key", null, null);

        AssertGenericFailure(result);
    }

    [Fact]
    public async Task ExchangeApiKeyAsync_AuditsRequestedEffectiveAndDroppedScopes()
    {
        Guid userId = Guid.NewGuid();
        ApiKey key = CreateDesktopKey(ApiKeyScope.ModelRead | ApiKeyScope.CalibrationRead | ApiKeyScope.CalibrationDelete, userId);
        SetOwnerAuthorization(userId, permissions: [PrintFarmerPermissions.Calibration.Read]);
        _mockApiKeyRepository.Setup(r => r.GetByKeyHashAsync(It.IsAny<string>())).ReturnsAsync(key);
        _mockUsersRepository.Setup(r => r.GetUserEntityAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateActiveOwner(userId));

        await _service.ExchangeApiKeyAsync("raw-desktop-key", "127.0.0.1", "test-agent");

        _mockAuditService.Verify(
            a => a.LogApiKeyExchangeAsync(
                userId,
                key.Id,
                "127.0.0.1",
                "test-agent",
                It.Is<ApiKeyExchangeScopeAudit>(audit =>
                    audit.RequestedScopes.Contains("CalibrationDelete") &&
                    audit.EffectiveScopes.Contains("CalibrationRead") &&
                    !audit.EffectiveScopes.Contains("CalibrationDelete") &&
                    audit.DroppedScopes.Contains("CalibrationDelete") &&
                    audit.GrantedPermissions.Contains("calibration:read") &&
                    !audit.GrantedPermissions.Contains("calibration:delete")),
                null,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// An exchange token is a bearer credential on an end-user machine, so a misconfigured (or
    /// hostile) lifetime must never be able to turn it into a long-lived credential.
    /// </summary>
    [Theory]
    [InlineData("60")]
    [InlineData("1440")]
    [InlineData("2147483647")]
    public void ExchangeApiKeyAsync_ConfiguredLifetimeAboveCeiling_IsClamped(string configured)
    {
        _mockConfiguration.Setup(c => c["Jwt:DesktopExchangeLifetimeMinutes"]).Returns(configured);
        Guid userId = Guid.NewGuid();

        (_, ApiKeyExchangeResult result) = ExchangeAndRead(
            CreateDesktopKey(ApiKeyScope.ModelRead, userId),
            CreateActiveOwner(userId));

        result.ExpiresAt.Should().NotBeNull();
        result.ExpiresAt!.Value.Should().BeCloseTo(
            DateTime.UtcNow.AddMinutes(ApiKeyExchangeService.MaxLifetimeMinutes),
            TimeSpan.FromMinutes(1));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("not-a-number")]
    public void ExchangeApiKeyAsync_InvalidConfiguredLifetime_FallsBackToDefault(string configured)
    {
        _mockConfiguration.Setup(c => c["Jwt:DesktopExchangeLifetimeMinutes"]).Returns(configured);
        Guid userId = Guid.NewGuid();

        (_, ApiKeyExchangeResult result) = ExchangeAndRead(
            CreateDesktopKey(ApiKeyScope.ModelRead, userId),
            CreateActiveOwner(userId));

        result.ExpiresAt!.Value.Should().BeCloseTo(
            DateTime.UtcNow.AddMinutes(ApiKeyExchangeService.DefaultLifetimeMinutes),
            TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Scope claims and permission claims are two views of one effective mask, so they can never
    /// disagree regardless of which scopes were revoked.
    /// </summary>
    [Fact]
    public void ExchangeApiKeyAsync_ScopeAndPermissionClaims_AlwaysAgree()
    {
        Guid userId = Guid.NewGuid();
        SetOwnerAuthorization(userId, permissions:
        [
            PrintFarmerPermissions.Calibration.Read,
            PrintFarmerPermissions.Queue.Read,
        ]);

        (JsonWebToken jwt, _) = ExchangeAndRead(
            CreateDesktopKey(
                ApiKeyScope.CalibrationRead | ApiKeyScope.CalibrationDelete | ApiKeyScope.QueueRead | ApiKeyScope.QueueStart,
                userId),
            CreateActiveOwner(userId));

        List<string> scopes = [.. ScopeClaims(jwt)];
        List<string> permissions = [.. PermissionClaims(jwt)];

        scopes.Should().BeEquivalentTo(new[] { "CalibrationRead", "QueueRead" });
        permissions.Should().BeEquivalentTo(new[] { "calibration:read", "queue:read" });

        // Every emitted permission must trace back to an emitted scope, and vice versa.
        foreach (string scope in scopes)
        {
            DesktopScopePermissionMap.TryParseScopeName(scope, out ApiKeyScope flag).Should().BeTrue();
            IReadOnlyList<string> mapped = DesktopScopePermissionMap.GetPermissions(flag);
            mapped.Should().OnlyContain(p => permissions.Contains(p));
        }
    }

    #endregion
}

