using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
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

        _controller = new UserApiKeysController(_repoMock.Object, _settingsServiceMock.Object)
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
