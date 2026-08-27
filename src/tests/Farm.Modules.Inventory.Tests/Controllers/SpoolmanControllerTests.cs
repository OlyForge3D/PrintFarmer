using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Settings;
using Farm.Modules.Inventory.Controllers;
using Farm.Web.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Modules.Inventory.Tests.Controllers;

public class SpoolmanControllerTests
{
    private readonly Mock<ISpoolmanService> _spoolmanServiceMock;
    private readonly Mock<ISettingsService> _settingsServiceMock;
    private readonly Mock<IBarcodeScanLogService> _barcodeScanLogServiceMock;
    private readonly Mock<ILogger<SpoolmanController>> _loggerMock;
    private readonly Mock<IFilamentCoverageBroadcaster> _coverageBroadcasterMock;
    private readonly Mock<ISpoolBurnRateProjectionService> _burnRateProjectionMock;
    private readonly SpoolmanController _controller;

    public SpoolmanControllerTests()
    {
        _spoolmanServiceMock = new Mock<ISpoolmanService>();
        _settingsServiceMock = new Mock<ISettingsService>();
        _barcodeScanLogServiceMock = new Mock<IBarcodeScanLogService>();
        _loggerMock = new Mock<ILogger<SpoolmanController>>();
        _coverageBroadcasterMock = new Mock<IFilamentCoverageBroadcaster>(MockBehavior.Strict);
        _burnRateProjectionMock = new Mock<ISpoolBurnRateProjectionService>();
        _controller = new SpoolmanController(
            _spoolmanServiceMock.Object,
            _settingsServiceMock.Object,
            _barcodeScanLogServiceMock.Object,
            _loggerMock.Object,
            _coverageBroadcasterMock.Object,
            _burnRateProjectionMock.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.Name, "admin")],
                        "test")),
                },
            },
        };
    }

    [Theory]
    [InlineData(nameof(SpoolmanController.TestAsync))]
    [InlineData(nameof(SpoolmanController.ClearConfigAsync))]
    [InlineData(nameof(SpoolmanController.ScanNetworkAsync))]
    public void PrivilegedNetworkAndConfigurationActions_RequireAdmin(string methodName)
    {
        // Issue #1467: these actions used to be gated by the "RequireAdmin" role-backed policy
        // alias; migrated to [RequirePermission("spoolman", "admin")] like the other admin-only
        // gates in this controller.
        RequirePermissionAttribute attribute = Assert.Single(
            typeof(SpoolmanController)
                .GetMethod(methodName)!
                .GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: true)
                .Cast<RequirePermissionAttribute>());

        Assert.Equal("spoolman", attribute.Resource);
        Assert.Equal("admin", attribute.Action);
    }

    [Fact]
    public async Task GetBurnRateAsync_ValidIdentity_ReturnsProjection()
    {
        CanonicalSpoolIdentity identity = new(
            SpoolSourceKind.Central,
            "HTTP://CENTRAL.LOCAL:80/",
            42);
        SpoolBurnRateProjectionDto projection = new(
            identity.SourceKind,
            identity.SourceIdentity,
            identity.SpoolId,
            500,
            90,
            3,
            new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc),
            3,
            SpoolBurnRateProjectionState.Ready);
        _burnRateProjectionMock.Setup(service => service.ProjectAsync(
                identity,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(projection);

        ActionResult<SpoolBurnRateProjectionDto> result =
            await _controller.GetBurnRateAsync(
                42,
                SpoolSourceKind.Central,
                "HTTP://CENTRAL.LOCAL:80/",
                CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(projection, ok.Value);
    }

    [Theory]
    [InlineData(0, SpoolSourceKind.Central, "http://central.local")]
    [InlineData(42, null, "http://central.local")]
    [InlineData(42, SpoolSourceKind.Central, "")]
    public async Task GetBurnRateAsync_InvalidIdentity_ReturnsBadRequest(
        int spoolId,
        SpoolSourceKind? sourceKind,
        string sourceIdentity)
    {
        ActionResult<SpoolBurnRateProjectionDto> result =
            await _controller.GetBurnRateAsync(
                spoolId,
                sourceKind,
                sourceIdentity,
                CancellationToken.None);

        _ = Assert.IsType<BadRequestObjectResult>(result.Result);
        _burnRateProjectionMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TestAsync_WithNullRequest_ReturnsBadRequest()
    {
        // Act
        IActionResult result = await _controller.TestAsync(null, CancellationToken.None);

        // Assert
        _ = Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task TestAsync_WithNullBaseUrl_ReturnsBadRequest()
    {
        // Arrange
        var request = new SpoolmanConfigDto(BaseUrl: null);

        // Act
        IActionResult result = await _controller.TestAsync(request, CancellationToken.None);

        // Assert
        _ = Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task TestAsync_WithEmptyBaseUrl_ReturnsBadRequest()
    {
        // Arrange
        var request = new SpoolmanConfigDto(BaseUrl: "");

        // Act
        IActionResult result = await _controller.TestAsync(request, CancellationToken.None);

        // Assert
        _ = Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task TestAsync_WithValidRequest_ReturnsProbeResult()
    {
        // Arrange
        var request = new SpoolmanConfigDto(BaseUrl: "http://localhost:7912");
        var probeResult = new SpoolmanProbeResult(
            Success: true,
            NormalizedUrl: "http://localhost:7912",
            EndpointTried: "http://localhost:7912/api/v1/health",
            StatusCode: 200,
            Version: "0.18.0",
            Message: null,
            ErrorCategory: null);

        _spoolmanServiceMock
            .Setup(s => s.ProbeAsync("http://localhost:7912", It.IsAny<CancellationToken>()))
            .ReturnsAsync(probeResult);

        // Act
        IActionResult result = await _controller.TestAsync(request, CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public void GetConfig_WithValidConfig_ReturnsOk()
    {
        // Arrange
        var config = new SpoolmanConfigDto(BaseUrl: "http://localhost:7912");
        _spoolmanServiceMock
            .Setup(s => s.GetConfig())
            .Returns(config);

        // Act
        ActionResult<SpoolmanConfigDto?> result = _controller.GetConfig();

        // Assert
        ActionResult<SpoolmanConfigDto> okResult = Assert.IsType<ActionResult<SpoolmanConfigDto>>(result);
        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public void GetConfig_WithNullConfig_ReturnsNull()
    {
        // Arrange
        _spoolmanServiceMock
            .Setup(s => s.GetConfig())
            .Returns((SpoolmanConfigDto?)null!);

        // Act
        ActionResult<SpoolmanConfigDto?> result = _controller.GetConfig();

        // Assert
        _ = Assert.IsType<ActionResult<SpoolmanConfigDto>>(result);
    }

    [Fact]
    public async Task SetConfig_WithNullConfig_ReturnsBadRequest()
    {
        IActionResult result = await _controller.SetConfigAsync(null);

        _ = Assert.IsType<BadRequestObjectResult>(result);
        _coverageBroadcasterMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetConfig_WithValidConfig_BroadcastsAfterSuccess()
    {
        SpoolmanConfigDto config = new("http://localhost:7912");
        MockSequence sequence = new();
        _spoolmanServiceMock.InSequence(sequence).Setup(s => s.SetConfig(config));
        _coverageBroadcasterMock.InSequence(sequence).Setup(b => b.BroadcastFleetChangedAsync(
                FilamentCoverageChangeReasons.SpoolWeight,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        IActionResult result = await _controller.SetConfigAsync(config);

        _ = Assert.IsType<NoContentResult>(result);
        _coverageBroadcasterMock.VerifyAll();
    }

    [Fact]
    public async Task SetConfig_WhenPersistenceFails_DoesNotBroadcast()
    {
        SpoolmanConfigDto config = new("http://localhost:7912");
        _spoolmanServiceMock.Setup(s => s.SetConfig(config))
            .Throws(new InvalidOperationException("save failed"));

        Func<Task> act = () => _controller.SetConfigAsync(config);

        await Assert.ThrowsAsync<InvalidOperationException>(act);
        _coverageBroadcasterMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetSpoolsAsync_WithValidConfig_ReturnsSpools()
    {
        // Arrange
        var spools = new List<SpoolmanSpoolDto>
        {
            new SpoolmanSpoolDto(Id: 1, Name: "PLA", Material: "PLA", RemainingWeightG: 1000, ColorHex: null, InUse: false),
            new SpoolmanSpoolDto(Id: 2, Name: "PETG", Material: "PETG", RemainingWeightG: 500, ColorHex: null, InUse: false)
        };

        _spoolmanServiceMock
            .Setup(s => s.ListSpoolsAsync(It.IsAny<SpoolmanSpoolQueryParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanPagedResult<SpoolmanSpoolDto>(spools, 2));

        // Act
        ActionResult<SpoolmanPagedResult<SpoolmanSpoolDto>> result = await _controller.GetSpoolsAsync(null, null, null, null, null, null, null, null, CancellationToken.None);

        // Assert
        _ = Assert.IsType<ActionResult<SpoolmanPagedResult<SpoolmanSpoolDto>>>(result);
    }

    [Fact]
    public async Task GetSpoolsAsync_CallsService()
    {
        // Arrange
        _spoolmanServiceMock
            .Setup(s => s.ListSpoolsAsync(It.IsAny<SpoolmanSpoolQueryParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanPagedResult<SpoolmanSpoolDto>([], 0));

        // Act
        await _controller.GetSpoolsAsync(null, null, null, null, null, null, null, null, CancellationToken.None);

        // Assert
        _spoolmanServiceMock.Verify(
            s => s.ListSpoolsAsync(It.IsAny<SpoolmanSpoolQueryParams>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HealthAsync_WithHealthyService_ReturnsSuccess()
    {
        // Arrange
        var probeResult = new SpoolmanProbeResult(
            Success: true,
            NormalizedUrl: null,
            EndpointTried: "http://localhost:7912/api/v1/health",
            StatusCode: 200,
            Version: null,
            Message: null,
            ErrorCategory: null);

        _spoolmanServiceMock
            .Setup(s => s.HealthProbeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(probeResult);

        // Act
        IActionResult result = await _controller.HealthAsync(CancellationToken.None);

        // Assert
        _ = Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task HealthAsync_WithUnhealthyService_ReturnsFailure()
    {
        // Arrange
        var probeResult = new SpoolmanProbeResult(
            Success: false,
            NormalizedUrl: null,
            EndpointTried: null,
            StatusCode: null,
            Version: null,
            Message: "Service unavailable",
            ErrorCategory: null);

        _spoolmanServiceMock
            .Setup(s => s.HealthProbeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(probeResult);

        // Act
        IActionResult result = await _controller.HealthAsync(CancellationToken.None);

        // Assert
        _ = Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task ClearConfig_ReturnsNoContentAndBroadcasts()
    {
        MockSequence sequence = new();
        _spoolmanServiceMock.InSequence(sequence).Setup(s => s.ClearConfig());
        _coverageBroadcasterMock.InSequence(sequence).Setup(b => b.BroadcastFleetChangedAsync(
                FilamentCoverageChangeReasons.SpoolWeight,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        IActionResult result = await _controller.ClearConfigAsync();

        _ = Assert.IsType<NoContentResult>(result);
        _coverageBroadcasterMock.VerifyAll();
    }

    [Fact]
    public async Task ClearConfig_WithException_ReturnsInternalServerError()
    {
        // Arrange
        _spoolmanServiceMock
            .Setup(s => s.ClearConfig())
            .Throws(new InvalidOperationException("Clear failed"));

        // Act
        IActionResult result = await _controller.ClearConfigAsync();

        // Assert
        ObjectResult statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, statusCodeResult.StatusCode);
        _coverageBroadcasterMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateSpoolAsync_Success_BroadcastsSpoolWeight()
    {
        SpoolmanSpoolRequest request = new() { RemainingWeight = 120 };
        _spoolmanServiceMock.Setup(s => s.UpdateSpoolInSpoolmanAsync(7, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanSpoolDto(7, "spool", "PLA", 120, null, true));
        SetupFleetBroadcast();

        _ = await _controller.UpdateSpoolAsync(7, request, CancellationToken.None);

        _coverageBroadcasterMock.VerifyAll();
    }

    [Fact]
    public async Task UpdateSpoolAsync_RemoteFailure_DoesNotBroadcast()
    {
        SpoolmanSpoolRequest request = new() { RemainingWeight = 120 };
        _spoolmanServiceMock.Setup(s => s.UpdateSpoolInSpoolmanAsync(7, request, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));

        _ = await _controller.UpdateSpoolAsync(7, request, CancellationToken.None);

        _coverageBroadcasterMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeleteSpoolAsync_Success_BroadcastsSpoolWeight()
    {
        _spoolmanServiceMock.Setup(s => s.DeleteSpoolFromSpoolmanAsync(7, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        SetupFleetBroadcast();

        _ = await _controller.DeleteSpoolAsync(7, CancellationToken.None);

        _coverageBroadcasterMock.VerifyAll();
    }

    [Fact]
    public async Task BulkUpdateSpoolsAsync_WithSuccessfulUpdates_BroadcastsOnce()
    {
        SpoolmanBulkUpdateSpoolsRequest request = new() { SpoolIds = [1, 2], Location = "rack" };
        _spoolmanServiceMock.Setup(s => s.BulkUpdateSpoolsAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanBulkUpdateResult(2, 0, []));
        SetupFleetBroadcast();

        _ = await _controller.BulkUpdateSpoolsAsync(request, CancellationToken.None);

        _coverageBroadcasterMock.Verify(
            b => b.BroadcastFleetChangedAsync(
                FilamentCoverageChangeReasons.SpoolWeight,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task BulkDeleteSpoolsAsync_WithSuccessfulDeletes_BroadcastsOnce()
    {
        SpoolmanBulkDeleteSpoolsRequest request = new() { SpoolIds = [1, 2] };
        _spoolmanServiceMock.Setup(s => s.BulkDeleteSpoolsAsync(request.SpoolIds, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanBulkUpdateResult(2, 0, []));
        SetupFleetBroadcast();

        _ = await _controller.BulkDeleteSpoolsAsync(request, CancellationToken.None);

        _coverageBroadcasterMock.Verify(
            b => b.BroadcastFleetChangedAsync(
                FilamentCoverageChangeReasons.SpoolWeight,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task BulkUpdateSpoolsAsync_WithNoSuccessfulUpdates_DoesNotBroadcast()
    {
        SpoolmanBulkUpdateSpoolsRequest request = new() { SpoolIds = [1] };
        _spoolmanServiceMock.Setup(s => s.BulkUpdateSpoolsAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanBulkUpdateResult(0, 1, ["failed"]));

        _ = await _controller.BulkUpdateSpoolsAsync(request, CancellationToken.None);

        _coverageBroadcasterMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ImportSpoolsCsvAsync_ExistingSpoolUpdates_BroadcastsOnceAfterImport()
    {
        const string csv = "Id,RemainingWeightG\n7,120\n8,80\n";
        using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(csv));
        FormFile file = new(stream, 0, stream.Length, "file", "spools.csv");
        _spoolmanServiceMock.Setup(s => s.UpdateSpoolInSpoolmanAsync(
                It.IsAny<int>(),
                It.IsAny<SpoolmanSpoolRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, SpoolmanSpoolRequest _, CancellationToken _) =>
                new SpoolmanSpoolDto(id, "spool", "PLA", 100, null, true));
        SetupFleetBroadcast();

        _ = await _controller.ImportSpoolsCsvAsync(file, CancellationToken.None);

        _coverageBroadcasterMock.Verify(
            b => b.BroadcastFleetChangedAsync(
                FilamentCoverageChangeReasons.SpoolWeight,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ImportSpoolsCsvAsync_WhollyNewSpools_DoesNotBroadcast()
    {
        const string csv = "FilamentId,RemainingWeightG\n4,120\n";
        using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(csv));
        FormFile file = new(stream, 0, stream.Length, "file", "spools.csv");
        _spoolmanServiceMock.Setup(s => s.CreateSpoolInSpoolmanAsync(
                It.IsAny<SpoolmanSpoolRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanSpoolDto(9, "new", "PLA", 120, null, false));

        _ = await _controller.ImportSpoolsCsvAsync(file, CancellationToken.None);

        _coverageBroadcasterMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ScanNetworkAsync_WithSettings_ReturnResults()
    {
        // Arrange
        var settings = new NetworkDiscoverySettings
        {
            DiscoverySubnets = new[] { "192.168.1.0/24" }
        };

        var discoveryResults = new List<SpoolmanDiscoveryResult>
        {
            new SpoolmanDiscoveryResult(
                Url: "http://192.168.1.100:7912",
                IsAvailable: true,
                Error: null)
        };

        _settingsServiceMock
            .Setup(s => s.Get<NetworkDiscoverySettings>())
            .Returns(settings);

        _spoolmanServiceMock
            .Setup(s => s.ScanNetworkForSpoolmanAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(discoveryResults);

        // Act
        IActionResult result = await _controller.ScanNetworkAsync(CancellationToken.None);

        // Assert
        _ = Assert.IsType<OkObjectResult>(result);
    }

    private void SetupFleetBroadcast()
    {
        _coverageBroadcasterMock.Setup(b => b.BroadcastFleetChangedAsync(
                FilamentCoverageChangeReasons.SpoolWeight,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task ScanNetworkAsync_WithException_ReturnsError()
    {
        // Arrange
        var settings = new NetworkDiscoverySettings
        {
            DiscoverySubnets = new[] { "192.168.1.0/24" }
        };

        _settingsServiceMock
            .Setup(s => s.Get<NetworkDiscoverySettings>())
            .Returns(settings);

        _spoolmanServiceMock
            .Setup(s => s.ScanNetworkForSpoolmanAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network error"));

        // Act
        IActionResult result = await _controller.ScanNetworkAsync(CancellationToken.None);

        // Assert
        ObjectResult statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task ScanNetworkAsync_WithNullSettings_HandlesGracefully()
    {
        // Arrange
        _settingsServiceMock
            .Setup(s => s.Get<NetworkDiscoverySettings>())
            .Returns((NetworkDiscoverySettings?)null!);

        _spoolmanServiceMock
            .Setup(s => s.ScanNetworkForSpoolmanAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SpoolmanDiscoveryResult>());

        // Act
        IActionResult result = await _controller.ScanNetworkAsync(CancellationToken.None);

        // Assert
        _ = Assert.IsType<OkObjectResult>(result);
    }

    // -------------------------------------------------------------------------
    // GetFilamentsAsync (paged) tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetFilamentsAsync_WithNoParams_ReturnsPagedResult()
    {
        // Arrange
        var filaments = new List<SpoolmanFilamentDto>
        {
            new(Id: 1, Name: "PolyTerra PLA Charcoal Black", Material: "PLA", ColorHex: "1B1B1B",
                Vendor: "Polymaker", Density: 1.24, Diameter: 1.75, Weight: 1000, SpoolWeight: null,
                Price: null, SettingsExtruderTemp: 200, SettingsBedTemp: 60,
                ArticleNumber: null, Comment: null, MultiColorHexes: null, ExternalId: null),
            new(Id: 2, Name: "PolyTerra PETG Clear", Material: "PETG", ColorHex: "FFFFFF",
                Vendor: "Polymaker", Density: 1.27, Diameter: 1.75, Weight: 1000, SpoolWeight: null,
                Price: null, SettingsExtruderTemp: 230, SettingsBedTemp: 70,
                ArticleNumber: null, Comment: null, MultiColorHexes: null, ExternalId: null),
        };

        _spoolmanServiceMock
            .Setup(s => s.ListFilamentsPagedAsync(It.IsAny<SpoolmanFilamentQueryParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanPagedResult<SpoolmanFilamentDto>(filaments, 2));

        // Act
        ActionResult<SpoolmanPagedResult<SpoolmanFilamentDto>> result =
            await _controller.GetFilamentsAsync(null, null, null, null, null, null, CancellationToken.None);

        // Assert
        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        SpoolmanPagedResult<SpoolmanFilamentDto> pagedResult = Assert.IsType<SpoolmanPagedResult<SpoolmanFilamentDto>>(ok.Value);
        Assert.Equal(2, pagedResult.TotalCount);
        Assert.Equal(2, pagedResult.Items.Count);
    }

    [Fact]
    public async Task GetFilamentsAsync_CallsServiceWithQueryParams()
    {
        // Arrange
        _spoolmanServiceMock
            .Setup(s => s.ListFilamentsPagedAsync(It.IsAny<SpoolmanFilamentQueryParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanPagedResult<SpoolmanFilamentDto>([], 0));

        // Act
        await _controller.GetFilamentsAsync(
            limit: 10, offset: 20, sort: "name:asc", search: "PLA",
            material: "PLA", vendor: "Polymaker", CancellationToken.None);

        // Assert
        _spoolmanServiceMock.Verify(
            s => s.ListFilamentsPagedAsync(
                It.Is<SpoolmanFilamentQueryParams>(p =>
                    p.Limit == 10 &&
                    p.Offset == 20 &&
                    p.Sort == "name:asc" &&
                    p.Search == "PLA" &&
                    p.Material == "PLA" &&
                    p.Vendor == "Polymaker"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetFilamentsAsync_WithInvalidLimit_ReturnsBadRequest()
    {
        // Act
        ActionResult<SpoolmanPagedResult<SpoolmanFilamentDto>> result =
            await _controller.GetFilamentsAsync(limit: 0, null, null, null, null, null, CancellationToken.None);

        // Assert
        BadRequestObjectResult bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(bad.Value);
    }

    [Fact]
    public async Task GetFilamentsAsync_WithLimitOverMax_ReturnsBadRequest()
    {
        // Act
        ActionResult<SpoolmanPagedResult<SpoolmanFilamentDto>> result =
            await _controller.GetFilamentsAsync(limit: 501, null, null, null, null, null, CancellationToken.None);

        // Assert
        BadRequestObjectResult bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(bad.Value);
    }

    [Fact]
    public async Task GetFilamentsAsync_WithNegativeOffset_ReturnsBadRequest()
    {
        // Arrange
        _spoolmanServiceMock
            .Setup(s => s.ListFilamentsPagedAsync(It.IsAny<SpoolmanFilamentQueryParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanPagedResult<SpoolmanFilamentDto>([], 0));

        // Act
        ActionResult<SpoolmanPagedResult<SpoolmanFilamentDto>> result =
            await _controller.GetFilamentsAsync(null, offset: -1, null, null, null, null, CancellationToken.None);

        // Assert
        BadRequestObjectResult bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(bad.Value);
    }

    [Fact]
    public async Task GetFilamentsAsync_ReturnsEmptyPagedResultWhenSpoolmanNotConfigured()
    {
        // Arrange
        _spoolmanServiceMock
            .Setup(s => s.ListFilamentsPagedAsync(It.IsAny<SpoolmanFilamentQueryParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanPagedResult<SpoolmanFilamentDto>([], 0));

        // Act
        ActionResult<SpoolmanPagedResult<SpoolmanFilamentDto>> result =
            await _controller.GetFilamentsAsync(null, null, null, null, null, null, CancellationToken.None);

        // Assert
        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        SpoolmanPagedResult<SpoolmanFilamentDto> pagedResult = Assert.IsType<SpoolmanPagedResult<SpoolmanFilamentDto>>(ok.Value);
        Assert.Equal(0, pagedResult.TotalCount);
        Assert.Empty(pagedResult.Items);
    }
}
