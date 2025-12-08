using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.PrinterCapabilities;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class PrinterCapabilitiesControllerTests
{
    private readonly Mock<IPrinterCapabilitiesService> _svcMock;
    private readonly Mock<IUnifiedLoggingService> _loggerMock;
    private readonly PrinterCapabilitiesController _controller;

    public PrinterCapabilitiesControllerTests()
    {
        _svcMock = new Mock<IPrinterCapabilitiesService>();
        _loggerMock = new Mock<IUnifiedLoggingService>();
        _controller = new PrinterCapabilitiesController(_svcMock.Object, _loggerMock.Object);
    }

    private PrinterCapabilitiesDto CreateTestCapabilities(Guid printerId, string name = "Test Printer")
    {
        return new PrinterCapabilitiesDto(
            Id: Guid.NewGuid(),
            PrinterId: printerId,
            PrinterName: name,
            NozzleDiameter: 0.4,
            SupportedMaterials: new[] { "PLA", "PETG" },
            MaxBuildVolumeX: 250,
            MaxBuildVolumeY: 210,
            MaxBuildVolumeZ: 220,
            HasHeatedBed: true,
            HasEnclosure: false,
            MultiMaterial: false,
            SupportsAutoLeveling: true,
            NumberOfExtruders: 1,
            MinHotendTemp: 170,
            MaxHotendTemp: 300,
            MinBedTemp: 20,
            MaxBedTemp: 120,
            CurrentMaterial: "PLA",
            CurrentSpoolId: null,
            IsAvailable: true,
            LastUpdated: DateTime.UtcNow
        );
    }

    [Fact]
    public async Task GetAllCapabilitiesAsync_ReturnsOkWithCapabilities()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var capabilities = new List<PrinterCapabilitiesDto> { CreateTestCapabilities(printerId) };
        _svcMock.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(capabilities.AsReadOnly());

        // Act
        var result = await _controller.GetAllCapabilitiesAsync();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(capabilities, okResult.Value);
    }

    [Fact]
    public async Task GetCapabilitiesAsync_WithValidId_ReturnsOkWithCapabilities()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var capabilities = CreateTestCapabilities(printerId);
        _svcMock.Setup(s => s.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(capabilities);

        // Act
        var result = await _controller.GetCapabilitiesAsync(printerId);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(capabilities, okResult.Value);
    }

    [Fact]
    public async Task GetCapabilitiesAsync_WithInvalidId_ReturnsNotFound()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        _svcMock.Setup(s => s.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterCapabilitiesDto?)null);

        // Act
        var result = await _controller.GetCapabilitiesAsync(printerId);

        // Assert
        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Contains(printerId.ToString(), notFoundResult.Value?.ToString() ?? "");
    }

    [Fact]
    public async Task CreateCapabilitiesAsync_WithValidRequest_ReturnsCreated()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var request = new CreatePrinterCapabilitiesDto(PrinterId: printerId);
        var created = CreateTestCapabilities(printerId);
        _svcMock.Setup(s => s.CreateAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);

        // Act
        var result = await _controller.CreateCapabilitiesAsync(request);

        // Assert
        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(nameof(PrinterCapabilitiesController.GetCapabilitiesAsync), createdResult.ActionName);
        Assert.Equal(created, createdResult.Value);
    }

    [Fact]
    public async Task CreateCapabilitiesAsync_WithNullRequest_ReturnsBadRequest()
    {
        // Act
        var result = await _controller.CreateCapabilitiesAsync(null!);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Request body is required", badRequestResult.Value);
    }

    [Fact]
    public async Task DeleteCapabilitiesAsync_WithValidId_ReturnsNoContent()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        _svcMock.Setup(s => s.DeleteAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.DeleteCapabilitiesAsync(printerId);

        // Assert
        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteCapabilitiesAsync_WhenNotFound_ReturnsNotFound()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        _svcMock.Setup(s => s.DeleteAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _controller.DeleteCapabilitiesAsync(printerId);

        // Assert
        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
        Assert.NotNull(notFoundResult.Value);
    }

    [Fact]
    public async Task DiscoverCapabilitiesAsync_WithNewCapabilities_ReturnsCreated()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var discovered = CreateTestCapabilities(printerId);
        _svcMock.Setup(s => s.DiscoverAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((discovered, true));

        // Act
        var result = await _controller.DiscoverCapabilitiesAsync(printerId);

        // Assert
        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(discovered, createdResult.Value);
    }

    [Fact]
    public async Task DiscoverCapabilitiesAsync_WithExistingCapabilities_ReturnsOk()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var discovered = CreateTestCapabilities(printerId);
        _svcMock.Setup(s => s.DiscoverAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((discovered, false));

        // Act
        var result = await _controller.DiscoverCapabilitiesAsync(printerId);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(discovered, okResult.Value);
    }

    [Fact]
    public async Task ValidateCapabilitiesAsync_WithValidId_ReturnsValidationResult()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var validationResult = new CapabilityValidationResult { IsValid = true };
        _svcMock.Setup(s => s.ValidateAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(validationResult);

        // Act
        var result = await _controller.ValidateCapabilitiesAsync(printerId);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(validationResult, okResult.Value);
    }

    [Fact]
    public async Task GetModelDefaultsAsync_WithValidId_ReturnsDefaults()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var defaults = CreateTestCapabilities(printerId, "Model Defaults");
        _svcMock.Setup(s => s.GetModelDefaultsAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(defaults);

        // Act
        var result = await _controller.GetModelDefaultsAsync(printerId);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(defaults, okResult.Value);
    }

    [Fact]
    public async Task GetModelDefaultsAsync_WithInvalidId_ReturnsNotFound()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        _svcMock.Setup(s => s.GetModelDefaultsAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterCapabilitiesDto?)null);

        // Act
        var result = await _controller.GetModelDefaultsAsync(printerId);

        // Assert
        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Contains(printerId.ToString(), notFoundResult.Value?.ToString() ?? "");
    }

    [Fact]
    public async Task CreateOrUpdateCapabilitiesAsync_WithValidRequest_ReturnsOk()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var request = new UpdatePrinterCapabilitiesDto(NozzleDiameter: 0.4);
        var updated = CreateTestCapabilities(printerId);
        _svcMock.Setup(s => s.CreateOrUpdateAsync(printerId, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(updated);

        // Act
        var result = await _controller.CreateOrUpdateCapabilitiesAsync(printerId, request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(updated, okResult.Value);
    }

    [Fact]
    public async Task CreateOrUpdateCapabilitiesAsync_WithNullRequest_ReturnsBadRequest()
    {
        // Act
        var result = await _controller.CreateOrUpdateCapabilitiesAsync(Guid.NewGuid(), null!);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Request body is required", badRequestResult.Value);
    }

    [Fact]
    public async Task CreateOrUpdateCapabilitiesAsync_WithInvalidPrinterId_ReturnsNotFound()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var request = new UpdatePrinterCapabilitiesDto(NozzleDiameter: 0.4);
        _svcMock.Setup(s => s.CreateOrUpdateAsync(printerId, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterCapabilitiesDto?)null!);

        // Act
        var result = await _controller.CreateOrUpdateCapabilitiesAsync(printerId, request);

        // Assert
        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Contains(printerId.ToString(), notFoundResult.Value?.ToString() ?? "");
    }

    [Fact]
    public async Task GetCompatiblePrintersAsync_WithValidGcodeFileId_ReturnsOkWithPrinters()
    {
        // Arrange
        var gcodeFileId = Guid.NewGuid();
        var printers = new List<PrinterDto>
        {
            new PrinterDto(Id: Guid.NewGuid(), Name: "Printer 1", ServerUrl: "http://localhost:7125", Notes: null, IsOnline: true, State: "Idle"),
            new PrinterDto(Id: Guid.NewGuid(), Name: "Printer 2", ServerUrl: "http://localhost:7126", Notes: null, IsOnline: false, State: "Offline")
        }.AsReadOnly();
        _svcMock.Setup(s => s.GetCompatiblePrintersAsync(gcodeFileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printers);

        // Act
        var result = await _controller.GetCompatiblePrintersAsync(gcodeFileId);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(printers, okResult.Value);
    }

    [Fact]
    public async Task GetCompatiblePrintersAsync_WithInvalidGcodeFileId_ReturnsNotFound()
    {
        // Arrange
        var gcodeFileId = Guid.NewGuid();
        _svcMock.Setup(s => s.GetCompatiblePrintersAsync(gcodeFileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<PrinterDto>?)null!);

        // Act
        var result = await _controller.GetCompatiblePrintersAsync(gcodeFileId);

        // Assert
        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Contains(gcodeFileId.ToString(), notFoundResult.Value?.ToString() ?? "");
    }

    [Fact]
    public async Task GetAllCapabilitiesAsync_WhenServiceThrows_Returns500Error()
    {
        // Arrange
        _svcMock.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("Database error"));

        // Act
        var result = await _controller.GetAllCapabilitiesAsync();

        // Assert
        var problemResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, problemResult.StatusCode);
    }

    [Fact]
    public async Task CreateCapabilitiesAsync_WhenServiceThrows_Returns500Error()
    {
        // Arrange
        var request = new CreatePrinterCapabilitiesDto(PrinterId: Guid.NewGuid());
        _svcMock.Setup(s => s.CreateAsync(request, It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("Database error"));

        // Act
        var result = await _controller.CreateCapabilitiesAsync(request);

        // Assert
        var problemResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, problemResult.StatusCode);
    }
}
