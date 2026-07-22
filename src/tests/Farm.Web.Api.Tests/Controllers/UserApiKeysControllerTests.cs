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
    public async Task CreateApiKeyAsync_WithNoPurposeSpecified_DefaultsToGeneralWithNoScopes()
    {
        var req = new CreateApiKeyRequest("legacy client");

        IActionResult result = await _controller.CreateApiKeyAsync(_userId, req);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        ApiKey created = _store.Should().ContainSingle().Subject;
        created.Purpose.Should().Be(ApiKeyPurpose.General);
        created.Scopes.Should().Be(ApiKeyScope.None);
        created.ExpiresAt.Should().BeNull();
        ok.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateApiKeyAsync_GeneralPurposeWithExplicitScopes_IsRejected()
    {
        var req = new CreateApiKeyRequest("slicer", ApiKeyPurpose.General, ApiKeyScope.ModelRead);

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
            DateTime.UtcNow.Add(UserApiKeysController.MaxDesktopKeyLifetime).AddDays(1));

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
    public async Task ListApiKeysAsync_ReturnsPurposeScopesAndExpiryState()
    {
        ApiKey generalKey = new()
        {
            UserId = _userId,
            Name = "slicer",
            KeyHash = "hash-1",
            Purpose = ApiKeyPurpose.General,
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
        _store.Add(generalKey);
        _store.Add(expiredDesktopKey);

        IActionResult result = await _controller.ListApiKeysAsync(_userId);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        IEnumerable<ApiKeyDto> dtos = Assert.IsAssignableFrom<IEnumerable<ApiKeyDto>>(ok.Value);
        ApiKeyDto[] list = dtos.ToArray();
        list.Should().HaveCount(2);
        list.Single(d => d.Id == generalKey.Id).IsExpired.Should().BeFalse();
        list.Single(d => d.Id == expiredDesktopKey.Id).IsExpired.Should().BeTrue();
        list.Single(d => d.Id == expiredDesktopKey.Id).Scopes.Should().Be(ApiKeyScope.ModelRead);
    }
}
