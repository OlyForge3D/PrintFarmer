using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Exceptions;
using Farm.Infrastructure.Services.Tags;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.Services.Catalog;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class CatalogControllerTests
{
    private readonly Mock<ILogger<CatalogController>> _loggerMock;
    private readonly Mock<ICatalogService> _catalogServiceMock;
    private readonly CatalogController _controller;

    public CatalogControllerTests()
    {
        _loggerMock = new Mock<ILogger<CatalogController>>();
        _catalogServiceMock = new Mock<ICatalogService>();
        _controller = new CatalogController(_loggerMock.Object, _catalogServiceMock.Object);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
    }

    [Fact]
    public async Task GetManufacturersAsync_ReturnsOkWithManufacturers()
    {
        // Arrange
        var manufacturers = new List<ManufacturerDto>
        {
            new ManufacturerDto(Id: Guid.NewGuid(), Name: "Prusa"),
            new ManufacturerDto(Id: Guid.NewGuid(), Name: "Creality")
        };
        _catalogServiceMock
            .Setup(s => s.GetManufacturersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((manufacturers, "etag123"));

        // Act
        ActionResult<IEnumerable<ManufacturerDto>> result = await _controller.GetManufacturersAsync(CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(manufacturers, okResult.Value);
    }

    [Fact]
    public async Task GetManufacturersAsync_WithEmptyList_ReturnsOkWithEmptyList()
    {
        // Arrange
        var manufacturers = new List<ManufacturerDto>();
        _catalogServiceMock
            .Setup(s => s.GetManufacturersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((manufacturers, "etag123"));

        // Act
        ActionResult<IEnumerable<ManufacturerDto>> result = await _controller.GetManufacturersAsync(CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        IEnumerable<ManufacturerDto> list = Assert.IsAssignableFrom<IEnumerable<ManufacturerDto>>(okResult.Value);
        Assert.Empty(list);
    }

    [Fact]
    public async Task GetManufacturerByIdAsync_WithValidId_ReturnsOk()
    {
        // Arrange
        var manufacturerId = Guid.NewGuid();
        var manufacturer = new ManufacturerDto(Id: manufacturerId, Name: "Prusa");
        _catalogServiceMock
            .Setup(s => s.GetManufacturerByIdAsync(manufacturerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(manufacturer);

        // Act
        ActionResult<ManufacturerDto> result = await _controller.GetManufacturerByIdAsync(manufacturerId, CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(manufacturer, okResult.Value);
    }

    [Fact]
    public async Task GetManufacturerByIdAsync_WithInvalidId_ReturnsNotFound()
    {
        // Arrange
        _catalogServiceMock
            .Setup(s => s.GetManufacturerByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ManufacturerDto?)null);

        // Act
        ActionResult<ManufacturerDto> result = await _controller.GetManufacturerByIdAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task CreateManufacturerAsync_WithValidRequest_ReturnsCreated()
    {
        // Arrange
        var request = new CreateManufacturerRequest(Name: "Prusa");
        var created = new ManufacturerDto(Id: Guid.NewGuid(), Name: "Prusa");
        _catalogServiceMock
            .Setup(s => s.CreateManufacturerAsync("Prusa", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);

        // Act
        ActionResult<ManufacturerDto> result = await _controller.CreateManufacturerAsync(request, CancellationToken.None);

        // Assert
        CreatedAtRouteResult createdResult = Assert.IsType<CreatedAtRouteResult>(result.Result);
        Assert.Equal("GetManufacturerById", createdResult.RouteName);
        Assert.Equal(created, createdResult.Value);
    }

    [Fact]
    public async Task CreateManufacturerAsync_WithEmptyName_ReturnsBadRequest()
    {
        // Arrange
        var request = new CreateManufacturerRequest(Name: "");

        // Act
        ActionResult<ManufacturerDto> result = await _controller.CreateManufacturerAsync(request, CancellationToken.None);

        // Assert
        BadRequestObjectResult badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Name is required", badRequestResult.Value);
    }

    [Fact]
    public async Task GetPrinterModelsAsync_ReturnsOkWithModels()
    {
        // Arrange
        var models = new List<PrinterModelDto>
        {
            new PrinterModelDto(Id: Guid.NewGuid(), Name: "MK4", ManufacturerId: Guid.NewGuid())
        };
        _catalogServiceMock
            .Setup(s => s.GetModelsAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((models, "etag456"));

        // Act
        ActionResult<IEnumerable<PrinterModelDto>> result = await _controller.GetPrinterModelsAsync(null, CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(models, okResult.Value);
    }

    [Fact]
    public async Task GetPrinterModelByIdAsync_WithValidId_ReturnsOk()
    {
        // Arrange
        var modelId = Guid.NewGuid();
        var model = new PrinterModelDto(Id: modelId, Name: "MK4", ManufacturerId: Guid.NewGuid());
        _catalogServiceMock
            .Setup(s => s.GetModelByIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);

        // Act
        ActionResult<PrinterModelDto> result = await _controller.GetPrinterModelByIdAsync(modelId, CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(model, okResult.Value);
    }

    [Fact]
    public async Task CreatePrinterModelAsync_WithValidRequest_ReturnsCreated()
    {
        // Arrange
        var manufacturerId = Guid.NewGuid();
        var request = new CreateModelRequest(
            ManufacturerId: manufacturerId,
            Name: "MK4",
            MotionType: MotionType.Cartesian,
            MaxX: 250,
            MaxY: 210,
            MaxZ: 220,
            DefaultBackend: PrinterBackend.Moonraker,
            SupportedFilamentTypeIds: null
        );
        var created = new PrinterModelDto(Id: Guid.NewGuid(), Name: "MK4", ManufacturerId: manufacturerId);
        _catalogServiceMock
            .Setup(s => s.CreateModelAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);

        // Act
        ActionResult<PrinterModelDto> result = await _controller.CreatePrinterModelAsync(request, CancellationToken.None);

        // Assert
        CreatedAtRouteResult createdResult = Assert.IsType<CreatedAtRouteResult>(result.Result);
        Assert.Equal("GetPrinterModelById", createdResult.RouteName);
        Assert.Equal(created, createdResult.Value);
    }

    [Fact]
    public async Task CreatePrinterModelAsync_WithInvalidManufacturerId_ReturnsNotFound()
    {
        // Arrange
        var request = new CreateModelRequest(
            ManufacturerId: Guid.NewGuid(),
            Name: "MK4",
            MotionType: null,
            MaxX: null,
            MaxY: null,
            MaxZ: null,
            DefaultBackend: null,
            SupportedFilamentTypeIds: null
        );
        _catalogServiceMock
            .Setup(s => s.CreateModelAsync(request, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("Manufacturer not found"));

        // Act
        ActionResult<PrinterModelDto> result = await _controller.CreatePrinterModelAsync(request, CancellationToken.None);

        // Assert
        NotFoundObjectResult notFoundResult = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal("Manufacturer not found", notFoundResult.Value);
    }

    [Fact]
    public async Task CreatePrinterModelAsync_WithDuplicateName_ReturnsConflict()
    {
        // Arrange
        var request = new CreateModelRequest(
            ManufacturerId: Guid.NewGuid(),
            Name: "MK4",
            MotionType: null,
            MaxX: null,
            MaxY: null,
            MaxZ: null,
            DefaultBackend: null,
            SupportedFilamentTypeIds: null
        );
        _catalogServiceMock
            .Setup(s => s.CreateModelAsync(request, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DuplicateEntityException("Model already exists"));

        // Act
        ActionResult<PrinterModelDto> result = await _controller.CreatePrinterModelAsync(request, CancellationToken.None);

        // Assert
        ConflictObjectResult conflictResult = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.NotNull(conflictResult.Value);
    }

    [Fact]
    public async Task DeleteModelAsync_WithValidId_ReturnsNoContent()
    {
        // Arrange
        var modelId = Guid.NewGuid();
        _catalogServiceMock
            .Setup(s => s.DeleteModelAsync(modelId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        IActionResult result = await _controller.DeleteModelAsync(modelId, CancellationToken.None);

        // Assert
        Assert.IsType<NoContentResult>(result);
    }
}
