using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class UserApiKeysControllerTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly List<ApiKey> _store = [];
    private readonly Mock<Farm.Infrastructure.Repositories.Api.IApiKeyRepository> _repoMock;
    private readonly Mock<ISettingsService> _settingsServiceMock;
    private readonly Mock<IUsersRepository> _usersRepositoryMock;
    private readonly UserApiKeysController _controller;

    public UserApiKeysControllerTests()
    {
        _repoMock = new Mock<Farm.Infrastructure.Repositories.Api.IApiKeyRepository>();
        _repoMock.Setup(r => r.AddAsync(It.IsAny<ApiKey>()))
            .Returns((ApiKey key) =>
            {
                _store.Add(key);
                return Task.CompletedTask;
            });
        _repoMock.Setup(r => r.GetByUserIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid userId) => _store.Where(k => k.UserId == userId).ToArray());
        _repoMock.Setup(r => r.GetByIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => _store.FirstOrDefault(k => k.Id == id));
        _repoMock.Setup(r => r.UpdateAsync(It.IsAny<ApiKey>()))
            .Returns(Task.CompletedTask);

        _settingsServiceMock = new Mock<ISettingsService>();
        _settingsServiceMock.Setup(s => s.Get<OctoPrintSettings>())
            .Returns(new OctoPrintSettings { HashStoredApiKeys = true });

        // Default owner authorization: no roles, no granted permissions. Tests that exercise
        // privileged scopes opt in explicitly via GrantOwnerPermissions/MakeOwnerFarmAdmin.
        _usersRepositoryMock = new Mock<IUsersRepository>();
        _usersRepositoryMock
            .Setup(r => r.GetActiveRoleNamesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _usersRepositoryMock
            .Setup(r => r.GetGrantedPermissionsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _controller = new UserApiKeysController(_repoMock.Object, _settingsServiceMock.Object, _usersRepositoryMock.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, _userId.ToString())],
                        "TestAuth"))
                }
            }
        };
    }

    [Fact]
    public async Task CreateApiKeyAsync_WithNoPurposeSpecified_DefaultsToOctoPrintWithNoScopes()
    {
        var req = new CreateApiKeyRequest("legacy client");

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        ApiKey created = _store.Should().ContainSingle().Subject;
        created.Purpose.Should().Be(ApiKeyPurpose.OctoPrint);
        created.Scopes.Should().Be(ApiKeyScope.None);
        created.ExpiresAt.Should().BeNull();
        ok.Value.Should().NotBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateApiKeyAsync_DesktopPurposeWithAnyHashingSetting_ReturnsSecretButPersistsOnlyHash(
        bool hashStoredApiKeys)
    {
        SetHashStoredApiKeys(hashStoredApiKeys);
        var req = new CreateApiKeyRequest("desktop", ApiKeyPurpose.Desktop, ApiKeyScope.ModelRead);

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        object response = ok.Value;
        string rawKey = Assert.IsType<string>(response.GetType().GetProperty("key")?.GetValue(response));
        ApiKey created = _store.Should().ContainSingle().Subject;
        created.KeyHash.Should().NotBe(rawKey).And.HaveLength(64);
    }

    [Fact]
    public async Task RevealApiKeyAsync_DesktopPurposeWithHashingDisabled_IsRejected()
    {
        SetHashStoredApiKeys(false);
        ApiKey desktopKey = new()
        {
            UserId = _userId,
            Name = "desktop",
            KeyHash = "stored-hash",
            Purpose = ApiKeyPurpose.Desktop,
            Scopes = ApiKeyScope.ModelRead,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
        };
        _store.Add(desktopKey);

        IActionResult result = await _controller.RevealApiKeyAsync(_userId, desktopKey.Id);

        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result);
        badRequest.Value.Should().NotBeNull();
        badRequest.Value!.ToString().Should().Contain("one-time");
    }

    [Fact]
    public async Task RevealApiKeyAsync_OctoPrintPurposeWithHashingDisabled_ReturnsLegacyStoredSecret()
    {
        SetHashStoredApiKeys(false);
        const string rawKey = "legacy-octoprint-secret";
        ApiKey octoPrintKey = new()
        {
            UserId = _userId,
            Name = "slicer",
            KeyHash = rawKey,
            Purpose = ApiKeyPurpose.OctoPrint,
        };
        _store.Add(octoPrintKey);

        IActionResult result = await _controller.RevealApiKeyAsync(_userId, octoPrintKey.Id);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        ok.Value.Should().NotBeNull();
        object response = ok.Value;
        string revealedKey = Assert.IsType<string>(response.GetType().GetProperty("key")?.GetValue(response));
        revealedKey.Should().Be(rawKey);
    }

    [Fact]
    public async Task RotateApiKeyAsync_DesktopPurposeWithHashingDisabled_ReturnsSecretButPersistsOnlyHash()
    {
        SetHashStoredApiKeys(false);
        DateTime expiresAt = DateTime.UtcNow.AddDays(30);
        ApiKey desktopKey = new()
        {
            UserId = _userId,
            Name = "desktop",
            KeyHash = "old-hash",
            Purpose = ApiKeyPurpose.Desktop,
            Scopes = ApiKeyScope.LibrarySync,
            ExpiresAt = expiresAt,
        };
        _store.Add(desktopKey);

        IActionResult result = await _controller.RotateApiKeyAsync(_userId, desktopKey.Id);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        object response = ok.Value;
        string rawKey = Assert.IsType<string>(response.GetType().GetProperty("key")?.GetValue(response));
        desktopKey.KeyHash.Should().NotBe(rawKey).And.HaveLength(64);
        desktopKey.Purpose.Should().Be(ApiKeyPurpose.Desktop);
        desktopKey.Scopes.Should().Be(ApiKeyScope.LibrarySync);
        desktopKey.ExpiresAt.Should().Be(expiresAt);
    }

    [Theory]
    [InlineData(ApiKeyPurpose.OctoPrint)]
    [InlineData(ApiKeyPurpose.Desktop)]
    public async Task RotateApiKeyAsync_ExpiredKey_IsRejectedWithoutReplacingSecret(ApiKeyPurpose purpose)
    {
        const string oldHash = "old-hash";
        DateTime expiresAt = DateTime.UtcNow.AddMinutes(-1);
        ApiKeyScope scopes = purpose == ApiKeyPurpose.Desktop ? ApiKeyScope.ModelRead : ApiKeyScope.None;
        ApiKey expiredKey = new()
        {
            UserId = _userId,
            Name = "expired",
            KeyHash = oldHash,
            Purpose = purpose,
            Scopes = scopes,
            ExpiresAt = expiresAt,
        };
        _store.Add(expiredKey);

        IActionResult result = await _controller.RotateApiKeyAsync(_userId, expiredKey.Id);

        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
        object response = badRequest.Value;
        string error = Assert.IsType<string>(response.GetType().GetProperty("error")?.GetValue(response));
        error.Should().Be("Expired API keys cannot be rotated. Create a new API key instead.");
        expiredKey.KeyHash.Should().Be(oldHash);
        expiredKey.Purpose.Should().Be(purpose);
        expiredKey.Scopes.Should().Be(scopes);
        expiredKey.ExpiresAt.Should().Be(expiresAt);
        _repoMock.Verify(r => r.UpdateAsync(It.IsAny<ApiKey>()), Times.Never);
    }

    [Fact]
    public async Task CreateApiKeyAsync_OctoPrintPurposeWithExplicitScopes_IsRejected()
    {
        var req = new CreateApiKeyRequest("slicer", ApiKeyPurpose.OctoPrint, ApiKeyScope.ModelRead);

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        Assert.IsType<BadRequestObjectResult>(result);
        _store.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateApiKeyAsync_OctoPrintPurposeWithFutureExpiry_IsAccepted()
    {
        DateTime expiry = DateTime.UtcNow.AddDays(30);
        var req = new CreateApiKeyRequest("slicer", ApiKeyPurpose.OctoPrint, ApiKeyScope.None, expiry);

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        Assert.IsType<OkObjectResult>(result);
        _store.Should().ContainSingle().Which.ExpiresAt.Should().BeCloseTo(expiry, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CreateApiKeyAsync_OctoPrintPurposeWithPastExpiry_IsRejected()
    {
        var req = new CreateApiKeyRequest(
            "slicer",
            ApiKeyPurpose.OctoPrint,
            ApiKeyScope.None,
            DateTime.UtcNow.AddMinutes(-1));

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        Assert.IsType<BadRequestObjectResult>(result);
        _store.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateApiKeyAsync_WithNameLongerThanLimit_IsRejected()
    {
        var req = new CreateApiKeyRequest(new string('a', UserApiKeysController.MaxNameLength + 1));

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        Assert.IsType<BadRequestObjectResult>(result);
        _store.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateApiKeyAsync_DesktopPurposeWithNoScopes_IsRejected()
    {
        var req = new CreateApiKeyRequest("desktop", ApiKeyPurpose.Desktop, ApiKeyScope.None);

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        Assert.IsType<BadRequestObjectResult>(result);
        _store.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateApiKeyAsync_DesktopPurposeWithScopesAndNoExpiry_AppliesSafeDefaultExpiry()
    {
        var req = new CreateApiKeyRequest("desktop", ApiKeyPurpose.Desktop, ApiKeyScope.ModelRead | ApiKeyScope.LibrarySync);

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        Assert.IsType<OkObjectResult>(result);
        ApiKey created = _store.Should().ContainSingle().Subject;
        created.Purpose.Should().Be(ApiKeyPurpose.Desktop);
        created.Scopes.Should().Be(ApiKeyScope.ModelRead | ApiKeyScope.LibrarySync);
        created.ExpiresAt.Should().NotBeNull();
        created.ExpiresAt!.Value.Should().BeCloseTo(
            DateTime.UtcNow.Add(UserApiKeysController.DefaultDesktopKeyLifetime),
            TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task CreateApiKeyAsync_DesktopPurposeWithPastExpiry_IsRejected()
    {
        var req = new CreateApiKeyRequest("desktop", ApiKeyPurpose.Desktop, ApiKeyScope.ModelWrite, DateTime.UtcNow.AddDays(-1));

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        Assert.IsType<BadRequestObjectResult>(result);
        _store.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateApiKeyAsync_DesktopPurposeWithExpiryBeyondMax_IsRejected()
    {
        var req = new CreateApiKeyRequest(
            "desktop",
            ApiKeyPurpose.Desktop,
            ApiKeyScope.ModelWrite,
            DateTime.UtcNow.Add(UserApiKeysController.MaxKeyLifetime).AddDays(1));

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        Assert.IsType<BadRequestObjectResult>(result);
        _store.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateApiKeyAsync_DesktopPurposeWithValidExplicitExpiry_IsAccepted()
    {
        DateTime expiry = DateTime.UtcNow.AddDays(30);
        var req = new CreateApiKeyRequest("desktop", ApiKeyPurpose.Desktop, ApiKeyScope.LibrarySync, expiry);

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        Assert.IsType<OkObjectResult>(result);
        ApiKey created = _store.Should().ContainSingle().Subject;
        created.ExpiresAt.Should().BeCloseTo(expiry, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CreateApiKeyAsync_WithUndefinedScopeBits_IsRejected()
    {
        var req = new CreateApiKeyRequest("desktop", ApiKeyPurpose.Desktop, (ApiKeyScope)(1 << 30));

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        Assert.IsType<BadRequestObjectResult>(result);
        _store.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateApiKeyAsync_ForDifferentUser_ReturnsForbid()
    {
        var req = new CreateApiKeyRequest("other user's key");

        IActionResult result = await _controller.CreateApiKeyAsync(Guid.NewGuid(), req);

        Assert.IsType<ForbidResult>(result);
        _store.Should().BeEmpty();
    }

    [Fact]
    public async Task ListApiKeysAsync_ForCurrentUser_ReturnsPurposeScopesAndExpiryState()
    {
        ApiKey octoPrintKey = new()
        {
            UserId = _userId,
            Name = "slicer",
            KeyHash = "hash-1",
            Purpose = ApiKeyPurpose.OctoPrint,
            Scopes = ApiKeyScope.None,
        };
        ApiKey expiredDesktopKey = new()
        {
            UserId = _userId,
            Name = "old desktop",
            KeyHash = "hash-2",
            Purpose = ApiKeyPurpose.Desktop,
            Scopes = ApiKeyScope.ModelRead,
            ExpiresAt = DateTime.UtcNow.AddDays(-1),
        };
        _store.Add(octoPrintKey);
        _store.Add(expiredDesktopKey);

        IActionResult result = await _controller.ListApiKeysAsync(_userId);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        IEnumerable<ApiKeyDto> dtos = Assert.IsAssignableFrom<IEnumerable<ApiKeyDto>>(ok.Value);
        ApiKeyDto[] list = dtos.ToArray();
        list.Should().HaveCount(2);
        list.Single(d => d.Id == octoPrintKey.Id).IsExpired.Should().BeFalse();
        list.Single(d => d.Id == expiredDesktopKey.Id).IsExpired.Should().BeTrue();
        list.Single(d => d.Id == expiredDesktopKey.Id).Scopes.Should().Be(ApiKeyScope.ModelRead);
    }

    [Fact]
    public async Task ListApiKeysAsync_ForDifferentUserAsFarmAdmin_ReturnsOk()
    {
        Guid targetUserId = Guid.NewGuid();
        SetCaller(_userId, "farm_admin");

        IActionResult result = await _controller.ListApiKeysAsync(targetUserId);

        Assert.IsType<OkObjectResult>(result);
        _repoMock.Verify(r => r.GetByUserIdAsync(targetUserId), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Admin")]
    [InlineData("Administrator")]
    public async Task ListApiKeysAsync_ForDifferentUserWithoutCanonicalAdminRole_ReturnsForbid(string? role)
    {
        Guid targetUserId = Guid.NewGuid();
        SetCaller(_userId, role);

        IActionResult result = await _controller.ListApiKeysAsync(targetUserId);

        Assert.IsType<ForbidResult>(result);
        _repoMock.Verify(r => r.GetByUserIdAsync(It.IsAny<Guid>()), Times.Never);
    }

    private void SetHashStoredApiKeys(bool enabled)
    {
        _settingsServiceMock.Setup(s => s.Get<OctoPrintSettings>())
            .Returns(new OctoPrintSettings { HashStoredApiKeys = enabled });
    }

    #region Privileged scopes: owner-authorization gate

    /// <summary>
    /// Stubs the <b>target owner's</b> live database authorization. Deliberately keyed by user id
    /// so a test can prove the caller's own claims are irrelevant.
    /// </summary>
    private void SetOwnerAuthorization(Guid ownerId, IEnumerable<string>? roles = null, IEnumerable<string>? permissions = null)
    {
        _usersRepositoryMock
            .Setup(r => r.GetActiveRoleNamesAsync(ownerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. roles ?? []]);
        _usersRepositoryMock
            .Setup(r => r.GetGrantedPermissionsAsync(ownerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. (permissions ?? []).Select(p =>
            {
                (string resource, string action) = PrintFarmerPermissions.Split(p);
                return (resource, action);
            })]);
    }

    [Fact]
    public async Task CreateApiKeyAsync_PrivilegedScopeForAuthorizedOwner_IsAccepted()
    {
        SetOwnerAuthorization(_userId, permissions: [PrintFarmerPermissions.Calibration.Read]);
        var req = new CreateApiKeyRequest("desktop", ApiKeyPurpose.Desktop, ScopeNames: ["CalibrationRead"]);

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        Assert.IsType<OkObjectResult>(result);
        _store.Should().ContainSingle().Which.Scopes.Should().Be(ApiKeyScope.CalibrationRead);
    }

    [Fact]
    public async Task CreateApiKeyAsync_PrivilegedScopeForUnauthorizedOwner_IsRejected()
    {
        SetOwnerAuthorization(_userId);
        var req = new CreateApiKeyRequest("desktop", ApiKeyPurpose.Desktop, ScopeNames: ["CalibrationRead"]);

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result);
        badRequest.Value!.ToString().Should().Contain("calibration:read");
        _store.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateApiKeyAsync_PrivilegedScopeForFarmAdminOwner_IsAccepted()
    {
        SetOwnerAuthorization(_userId, roles: [PrintFarmerPermissions.FarmAdminRole]);
        var req = new CreateApiKeyRequest("desktop", ApiKeyPurpose.Desktop, ScopeNames: ["CalibrationPublish", "CalibrationRead"]);

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        Assert.IsType<OkObjectResult>(result);
        _store.Should().ContainSingle().Which.Scopes
            .Should().Be(ApiKeyScope.CalibrationRead | ApiKeyScope.CalibrationPublish);
    }

    /// <summary>
    /// Authorization must be resolved from the <b>target owner's</b> live database state, never
    /// from the caller's JWT: a farm_admin caller must not be able to mint a privileged key for an
    /// unprivileged user.
    /// </summary>
    [Fact]
    public async Task CreateApiKeyAsync_AdminCallerForUnprivilegedTarget_IsRejected()
    {
        Guid targetUserId = Guid.NewGuid();
        SetCaller(_userId, PrintFarmerPermissions.FarmAdminRole);
        SetOwnerAuthorization(targetUserId);
        var req = new CreateApiKeyRequest("desktop", ApiKeyPurpose.Desktop, ScopeNames: ["CalibrationRead"]);

        IActionResult result = await _controller.CreateApiKeyAsync(targetUserId, req);

        Assert.IsType<BadRequestObjectResult>(result);
        _store.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateApiKeyAsync_OctoPrintPurposeWithPrivilegedScope_IsRejected()
    {
        SetOwnerAuthorization(_userId, roles: [PrintFarmerPermissions.FarmAdminRole]);
        var req = new CreateApiKeyRequest("octoprint", ApiKeyPurpose.OctoPrint, ScopeNames: ["CalibrationRead"]);

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        Assert.IsType<BadRequestObjectResult>(result);
        _store.Should().BeEmpty();
    }

    [Theory]
    [InlineData(1 << 3)]
    [InlineData(1 << 30)]
    [InlineData(-1)]
    public async Task CreateApiKeyAsync_WithUndefinedOrNegativeScopeBits_IsRejected(int raw)
    {
        var req = new CreateApiKeyRequest("desktop", ApiKeyPurpose.Desktop, (ApiKeyScope)raw);

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        Assert.IsType<BadRequestObjectResult>(result);
        _store.Should().BeEmpty();
    }

    /// <summary>
    /// The legacy numeric/string aggregate must keep meaning exactly the three model scopes.
    /// </summary>
    [Theory]
    [InlineData(7)]
    [InlineData(1)]
    public async Task CreateApiKeyAsync_LegacyAggregateScopes_ExcludeEveryPrivilegedScope(int raw)
    {
        var req = new CreateApiKeyRequest("desktop", ApiKeyPurpose.Desktop, (ApiKeyScope)raw);

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        Assert.IsType<OkObjectResult>(result);
        ApiKey created = _store.Should().ContainSingle().Subject;
        (created.Scopes & DesktopScopePermissionMap.PermissionBackedScopes).Should().Be(ApiKeyScope.None);
        DesktopScopePermissionMap.GetPermissions(created.Scopes).Should().BeEmpty();
        _usersRepositoryMock.Verify(
            r => r.GetGrantedPermissionsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateApiKeyAsync_WithBothScopeNamesAndLegacyScopes_IsRejected()
    {
        var req = new CreateApiKeyRequest("desktop", ApiKeyPurpose.Desktop, ApiKeyScope.ModelRead, ScopeNames: ["ModelWrite"]);

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        Assert.IsType<BadRequestObjectResult>(result);
        _store.Should().BeEmpty();
    }

    [Theory]
    [InlineData("All")]
    [InlineData("None")]
    [InlineData("NotARealScope")]
    public async Task CreateApiKeyAsync_WithCompositeOrUnknownScopeName_IsRejected(string scopeName)
    {
        var req = new CreateApiKeyRequest("desktop", ApiKeyPurpose.Desktop, ScopeNames: [scopeName]);

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        Assert.IsType<BadRequestObjectResult>(result);
        _store.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateApiKeyAsync_GenerationWithoutSlicingScopes_IsRejectedWithActionableError()
    {
        SetOwnerAuthorization(_userId, roles: [PrintFarmerPermissions.FarmAdminRole]);
        var req = new CreateApiKeyRequest("desktop", ApiKeyPurpose.Desktop,
            ScopeNames: ["CalibrationRead", "CalibrationGenerate"]);

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result);
        badRequest.Value!.ToString().Should().Contain("SlicingSubmit");
        _store.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateApiKeyAsync_CompleteGenerationSelection_IsAccepted()
    {
        SetOwnerAuthorization(_userId, roles: [PrintFarmerPermissions.FarmAdminRole]);
        var req = new CreateApiKeyRequest("desktop", ApiKeyPurpose.Desktop,
            ScopeNames: ["CalibrationRead", "CalibrationGenerate", "SlicingSubmit", "SlicingReadArtifact"]);

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task CreateApiKeyAsync_QueueMutationWithoutQueueRead_IsRejected()
    {
        SetOwnerAuthorization(_userId, roles: [PrintFarmerPermissions.FarmAdminRole]);
        var req = new CreateApiKeyRequest("desktop", ApiKeyPurpose.Desktop, ScopeNames: ["QueueStart"]);

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result);
        badRequest.Value!.ToString().Should().Contain("QueueRead");
    }

    /// <summary>
    /// Scopes are immutable: rotation replaces the secret only.
    /// </summary>
    [Fact]
    public async Task RotateApiKeyAsync_PreservesScopesPurposeAndExpiry()
    {
        ApiKey key = new()
        {
            UserId = _userId,
            Name = "desktop",
            KeyHash = "old-hash",
            Purpose = ApiKeyPurpose.Desktop,
            Scopes = ApiKeyScope.ModelRead | ApiKeyScope.CalibrationRead,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
        };
        _store.Add(key);

        IActionResult result = await _controller.RotateApiKeyAsync(_userId, key.Id);

        Assert.IsType<OkObjectResult>(result);
        key.Scopes.Should().Be(ApiKeyScope.ModelRead | ApiKeyScope.CalibrationRead);
        key.Purpose.Should().Be(ApiKeyPurpose.Desktop);
        key.KeyHash.Should().NotBe("old-hash");
    }

    [Fact]
    public async Task ListApiKeysAsync_ReturnsIndividualScopeNamesNeverTheCompositeAlias()
    {
        _store.Add(new ApiKey
        {
            UserId = _userId,
            Name = "legacy-all",
            KeyHash = "hash",
            Purpose = ApiKeyPurpose.Desktop,
            Scopes = (ApiKeyScope)7,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
        });

        IActionResult result = await _controller.ListApiKeysAsync(_userId);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        ApiKeyDto dto = Assert.IsAssignableFrom<IEnumerable<ApiKeyDto>>(ok.Value).Single();
        dto.ScopeNames.Should().BeEquivalentTo(new[] { "ModelRead", "ModelWrite", "LibrarySync" });
        dto.ScopeNames.Should().NotContain("All");
    }

    #endregion

    private void SetCaller(Guid userId, string? role = null)
    {
        List<Claim> claims = [new Claim(ClaimTypes.NameIdentifier, userId.ToString())];
        if (role is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        _controller.ControllerContext.HttpContext.User =
            new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }
}
