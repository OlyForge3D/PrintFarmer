using Farm.Infrastructure;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class SpoolmanBarcodeEndpointTests
{
    private readonly Mock<ISpoolmanService> spoolmanServiceMock = new();
    private readonly SpoolmanController controller;

    public SpoolmanBarcodeEndpointTests()
    {
        Mock<ISettingsService> settingsServiceMock = new();
        Mock<ILogger<SpoolmanController>> loggerMock = new();
        controller = new SpoolmanController(spoolmanServiceMock.Object, settingsServiceMock.Object, loggerMock.Object);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_KnownArticleNumber_ReturnsOkWithFilament()
    {
        SpoolmanFilamentDto filament = CreateFilament(42, "012345678905");
        spoolmanServiceMock
            .Setup(s => s.GetFilamentByBarcodeAsync("012345678905", It.IsAny<CancellationToken>()))
            .ReturnsAsync(filament);

        ActionResult<SpoolmanFilamentDto> result = await controller.GetFilamentByBarcodeAsync("012345678905", CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        SpoolmanFilamentDto value = Assert.IsType<SpoolmanFilamentDto>(ok.Value);
        Assert.Equal(42, value.Id);
        Assert.Equal("012345678905", value.ArticleNumber);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_UnknownArticleNumber_ReturnsNotFound()
    {
        spoolmanServiceMock
            .Setup(s => s.GetFilamentByBarcodeAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpoolmanFilamentDto?)null);

        ActionResult<SpoolmanFilamentDto> result = await controller.GetFilamentByBarcodeAsync("missing", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_EmptyCode_ReturnsBadRequest()
    {
        ActionResult<SpoolmanFilamentDto> result = await controller.GetFilamentByBarcodeAsync("   ", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SaveBarcodeMappingAsync_ValidRequest_ReturnsUpdatedFilament()
    {
        SpoolmanFilamentDto filament = CreateFilament(7, "ABC123");
        SpoolmanBarcodeMappingRequest request = new() { Barcode = "ABC123", FilamentId = 7 };
        spoolmanServiceMock
            .Setup(s => s.SaveBarcodeMappingAsync(7, "ABC123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(filament);

        ActionResult<SpoolmanFilamentDto> result = await controller.SaveBarcodeMappingAsync(request, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        SpoolmanFilamentDto value = Assert.IsType<SpoolmanFilamentDto>(ok.Value);
        Assert.Equal(7, value.Id);
        Assert.Equal("ABC123", value.ArticleNumber);
    }

    [Fact]
    public async Task SaveBarcodeMappingAsync_MissingFilament_ReturnsNotFound()
    {
        SpoolmanBarcodeMappingRequest request = new() { Barcode = "ABC123", FilamentId = 404 };
        spoolmanServiceMock
            .Setup(s => s.SaveBarcodeMappingAsync(404, "ABC123", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpoolmanFilamentDto?)null);

        ActionResult<SpoolmanFilamentDto> result = await controller.SaveBarcodeMappingAsync(request, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task SaveBarcodeMappingAsync_EmptyBarcode_ReturnsBadRequest()
    {
        SpoolmanBarcodeMappingRequest request = new() { Barcode = " ", FilamentId = 7 };

        ActionResult<SpoolmanFilamentDto> result = await controller.SaveBarcodeMappingAsync(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateSpoolByBarcodeAsync_KnownBarcode_ReturnsCreatedSpool()
    {
        SpoolmanImportSpoolByBarcodeRequest request = new()
        {
            Barcode = "ABC123",
            RemainingWeight = 950,
            Location = "Shelf A",
        };
        SpoolmanSpoolDto spool = new(99, "PLA", "PLA", 950, null, false, FilamentId: 7, Location: "Shelf A");
        spoolmanServiceMock
            .Setup(s => s.CreateSpoolByBarcodeAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(spool);

        ActionResult<SpoolmanSpoolDto> result = await controller.CreateSpoolByBarcodeAsync(request, CancellationToken.None);

        ObjectResult created = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(201, created.StatusCode);
        SpoolmanSpoolDto value = Assert.IsType<SpoolmanSpoolDto>(created.Value);
        Assert.Equal(7, value.FilamentId);
        Assert.Equal(950, value.RemainingWeightG);
        Assert.Equal("Shelf A", value.Location);
    }

    [Fact]
    public async Task CreateSpoolByBarcodeAsync_UnknownBarcode_ReturnsNotFound()
    {
        SpoolmanImportSpoolByBarcodeRequest request = new() { Barcode = "missing" };
        spoolmanServiceMock
            .Setup(s => s.CreateSpoolByBarcodeAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpoolmanSpoolDto?)null);

        ActionResult<SpoolmanSpoolDto> result = await controller.CreateSpoolByBarcodeAsync(request, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    private static SpoolmanFilamentDto CreateFilament(int id, string? articleNumber)
        => new(
            Id: id,
            Name: "PolyTerra PLA",
            Material: "PLA",
            ColorHex: "111111",
            Vendor: "Polymaker",
            Density: 1.24,
            Diameter: 1.75,
            Weight: 1000,
            SpoolWeight: 200,
            Price: 24.99,
            SettingsExtruderTemp: 210,
            SettingsBedTemp: 60,
            ArticleNumber: articleNumber,
            Comment: null,
            MultiColorHexes: null,
            ExternalId: null);
}
