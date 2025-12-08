using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Catalog;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.Printers;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class PrintersControllerTests
{
    private readonly Mock<IUnifiedLoggingService> _loggerMock;
    private readonly Mock<IPrintersService> _printersServiceMock;
    private readonly Mock<ICatalogService> _catalogServiceMock;
    private readonly Mock<IDefaultCatalogService> _defaultCatalogMock;
    private readonly Mock<IValidator<CreatePrinterDto>> _validatorMock;
    private readonly Mock<IDiscoveryProxyService> _discoveryProxyMock;
    private readonly PrintersController _controller;

    public PrintersControllerTests()
    {
        _loggerMock = new Mock<IUnifiedLoggingService>();
        _printersServiceMock = new Mock<IPrintersService>();
        _catalogServiceMock = new Mock<ICatalogService>();
        _defaultCatalogMock = new Mock<IDefaultCatalogService>();
        _validatorMock = new Mock<IValidator<CreatePrinterDto>>();
        _discoveryProxyMock = new Mock<IDiscoveryProxyService>();

        _controller = new PrintersController(
            _loggerMock.Object,
            _printersServiceMock.Object,
            _catalogServiceMock.Object,
            _defaultCatalogMock.Object,
            _validatorMock.Object,
            _discoveryProxyMock.Object);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
    }

    #region GetAsync - List All Printers

    [Fact]
    public async Task GetAsync_WithoutFilters_ReturnsOkWithPrinterList()
    {
        // Arrange
        var printers = new List<PrinterFastDto>
        {
            new PrinterFastDto(
                Id: Guid.NewGuid(),
                Name: "Printer1",
                ServerUrl: "http://192.168.1.100:7125",
                Notes: null,
                IsOnline: true,
                State: "Idle",
                ManufacturerName: "Prusa",
                ModelName: "MINI+",
                Backend: PrinterBackend.Moonraker),
            new PrinterFastDto(
                Id: Guid.NewGuid(),
                Name: "Printer2",
                ServerUrl: "http://192.168.1.101:8080",
                Notes: null,
                IsOnline: false,
                State: null,
                ManufacturerName: "Creality",
                ModelName: "Ender 3",
                Backend: PrinterBackend.PrusaLink)
        };

        _printersServiceMock
            .Setup(s => s.GetAllFastDtosAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(printers.ToArray());

        // Act
        var result = await _controller.GetAsync(CancellationToken.None, false);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedPrinters = Assert.IsAssignableFrom<IEnumerable<PrinterFastDto>>(okResult.Value);
        Assert.Equal(2, returnedPrinters.Count());

        _printersServiceMock.Verify(s => s.GetAllFastDtosAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAsync_WhenEmpty_ReturnsOkWithEmptyList()
    {
        // Arrange
        _printersServiceMock
            .Setup(s => s.GetAllFastDtosAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PrinterFastDto>());

        // Act
        var result = await _controller.GetAsync(CancellationToken.None, false);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedPrinters = Assert.IsAssignableFrom<IEnumerable<PrinterFastDto>>(okResult.Value);
        Assert.Empty(returnedPrinters);
    }

    [Fact]
    public async Task GetAsync_WithTransientDbException_ReturnsOkWithEmptyList()
    {
        // Arrange
        _printersServiceMock
            .Setup(s => s.GetAllFastDtosAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no such table"));

        // Act
        var result = await _controller.GetAsync(CancellationToken.None, false);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedPrinters = Assert.IsAssignableFrom<IEnumerable<PrinterFastDto>>(okResult.Value);
        Assert.Empty(returnedPrinters);
    }

    #endregion

    #region GetAsync - Get Single Printer by ID

    [Fact]
    public async Task GetAsync_WithValidId_ReturnsOkWithPrinterDetails()
    {
        // Arrange
        var printerId = Guid.NewGuid();

        var printerDto = new PrinterDto(
            Id: printerId,
            Name: "Test Printer",
            ServerUrl: "http://192.168.1.100:7125",
            Notes: "Test notes",
            IsOnline: false,
            State: "Idle",
            ManufacturerName: "Prusa",
            ModelName: "MINI+",
            Progress: null,
            JobName: null,
            ThumbnailUrl: null,
            CameraStreamUrl: null,
            CameraSnapshotUrl: null,
            X: null,
            Y: null,
            Z: null,
            HotendTemp: null,
            BedTemp: null,
            HotendTarget: null,
            BedTarget: null,
            Backend: PrinterBackend.Moonraker,
            ApiKey: "test-key-123",
            OriginalServerUrl: "http://192.168.1.100",
            IpAddress: "192.168.1.100"
        );

        _printersServiceMock
            .Setup(s => s.GetPrinterDtoAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printerDto);

        // Act
        var result = await _controller.GetAsync(printerId, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedPrinter = Assert.IsType<PrinterDto>(okResult.Value);
        Assert.Equal(printerId, returnedPrinter.Id);
        Assert.Equal("Test Printer", returnedPrinter.Name);
        Assert.Equal("Prusa", returnedPrinter.ManufacturerName);
    }

    [Fact]
    public async Task GetAsync_WithInvalidId_ReturnsNotFound()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        _printersServiceMock
            .Setup(s => s.GetPrinterDtoAsync(printerId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        // Act
        var result = await _controller.GetAsync(printerId, CancellationToken.None);

        // Assert
        var notFoundResult = Assert.IsType<NotFoundResult>(result.Result);
        Assert.Equal(404, notFoundResult.StatusCode);
    }

    #endregion

    #region GetStatusAsync

    [Fact]
    public async Task GetStatusAsync_WithValidId_ReturnsStatusDto()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var status = new PrinterStatusDto(
            Id: printerId,
            IsOnline: true,
            State: "Printing",
            Progress: 50,
            JobName: "benchy.gcode",
            ThumbnailUrl: null,
            CameraStreamUrl: "http://192.168.1.100:8080/stream",
            CameraSnapshotUrl: "http://192.168.1.100:8080/snapshot",
            SpoolInfo: null);

        _printersServiceMock
            .Setup(s => s.GetStatusDtoAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

        // Act
        var result = await _controller.GetStatusAsync(printerId, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedStatus = Assert.IsType<PrinterStatusDto>(okResult.Value);
        Assert.Equal("Printing", returnedStatus.State);
        Assert.Equal(50, returnedStatus.Progress);
        Assert.True(returnedStatus.IsOnline);
    }

    [Fact]
    public async Task GetStatusAsync_WhenOffline_ReturnsOfflineStatus()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var status = new PrinterStatusDto(
            Id: printerId,
            IsOnline: false,
            State: null,
            Progress: null,
            JobName: null,
            ThumbnailUrl: null,
            CameraStreamUrl: null,
            CameraSnapshotUrl: null,
            SpoolInfo: null);

        _printersServiceMock
            .Setup(s => s.GetStatusDtoAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

        // Act
        var result = await _controller.GetStatusAsync(printerId, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedStatus = Assert.IsType<PrinterStatusDto>(okResult.Value);
        Assert.False(returnedStatus.IsOnline);
        Assert.Null(returnedStatus.State);
    }

    [Fact]
    public async Task GetStatusAsync_WhenServiceThrowsKeyNotFound_ReturnsNotFound()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        _printersServiceMock
            .Setup(s => s.GetStatusDtoAsync(printerId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        // Act
        var result = await _controller.GetStatusAsync(printerId, CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetStatusAsync_WhenServiceThrowsException_ReturnsFallbackOfflineStatus()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        _printersServiceMock
            .Setup(s => s.GetStatusDtoAsync(printerId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Network error"));

        // Act
        var result = await _controller.GetStatusAsync(printerId, CancellationToken.None);

        // Assert
        // The controller returns PrinterStatusDto directly in the catch block (no wrapping in Ok())
        var status = result.Value;
        Assert.NotNull(status);
        Assert.False(status.IsOnline);
        Assert.Equal(printerId, status.Id);
    }

    #endregion

    #region CreateAsync

    [Fact]
    public async Task CreateAsync_WithValidRequest_ReturnsCreatedAtRoute()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var manufacturerId = Guid.NewGuid();
        var modelId = Guid.NewGuid();

        var request = new CreatePrinterDto
        {
            Name = "New Printer",
            ServerUrl = "http://192.168.1.101:7125",
            IpAddress = "192.168.1.101",
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            ApiKey = "test-key-123",
            Backend = PrinterBackend.Moonraker
        };

        var createdPrinter = new PrinterDto(
            Id: printerId,
            Name: request.Name,
            ServerUrl: "http://192.168.1.101:7125",
            Notes: null,
            IsOnline: false,
            State: null,
            ManufacturerName: "Prusa",
            ModelName: "MINI+",
            Progress: null,
            JobName: null,
            ThumbnailUrl: null,
            CameraStreamUrl: null,
            CameraSnapshotUrl: null,
            X: null, Y: null, Z: null,
            HotendTemp: null, BedTemp: null,
            HotendTarget: null, BedTarget: null,
            Backend: PrinterBackend.Moonraker,
            ApiKey: request.ApiKey,
            OriginalServerUrl: "http://192.168.1.101",
            IpAddress: "192.168.1.101",
            SpoolInfo: null);

        _validatorMock
            .Setup(v => v.ValidateAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        _printersServiceMock
            .Setup(s => s.CreatePrinterFromDtoAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(createdPrinter);

        // Act
        var result = await _controller.CreateAsync(request, CancellationToken.None);

        // Assert
        var createdResult = Assert.IsType<CreatedAtRouteResult>(result.Result);
        Assert.Equal("GetPrinterById", createdResult.RouteName);
        Assert.NotNull(createdResult.RouteValues);
        Assert.True(createdResult.RouteValues.ContainsKey("id"));
        var returnedPrinter = Assert.IsType<PrinterDto>(createdResult.Value);
        Assert.Equal(printerId, returnedPrinter.Id);
        Assert.Equal("New Printer", returnedPrinter.Name);
    }

    [Fact]
    public async Task CreateAsync_WithValidationFailure_ReturnsBadRequest()
    {
        // Arrange
        var request = new CreatePrinterDto
        {
            Name = "",
            ServerUrl = "invalid-url",
            IpAddress = "",
            ManufacturerId = Guid.Empty,
            ModelId = Guid.Empty,
            ApiKey = "",
            Backend = PrinterBackend.Moonraker
        };

        var validationResult = new ValidationResult(
            new[] { new ValidationFailure("Name", "Name is required") });

        _validatorMock
            .Setup(v => v.ValidateAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(validationResult);

        // Act
        var result = await _controller.CreateAsync(request, CancellationToken.None);

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequest.Value);
        _printersServiceMock.Verify(
            s => s.CreatePrinterFromDtoAsync(It.IsAny<CreatePrinterDto>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateAsync_WithNullRequest_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => _controller.CreateAsync(null!, CancellationToken.None));
    }

    #endregion

    #region UpdateAsync

    [Fact]
    public async Task UpdateAsync_WithValidRequest_ReturnsUpdatedPrinter()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var manufacturerId = Guid.NewGuid();
        var modelId = Guid.NewGuid();

        var existingPrinter = new Printer
        {
            Id = printerId,
            Name = "Old Name",
            ServerUrl = "http://192.168.1.100:7125",
            OriginalServerUrl = "http://192.168.1.100",
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            Backend = (int)PrinterBackend.Moonraker,
            ApiKey = "old-key"
        };

        var updateRequest = new UpdatePrinterDto(
            Name: "Updated Name",
            ServerUrl: "http://192.168.1.102:7125",
            Notes: "Updated notes",
            ManufacturerId: null,
            ModelId: null,
            NewManufacturerName: null,
            NewModelName: null,
            DateAcquired: null);

        var updatedPrinter = new PrinterDto(
            Id: printerId,
            Name: updateRequest.Name,
            ServerUrl: "http://192.168.1.102:7125",
            Notes: updateRequest.Notes,
            IsOnline: false,
            State: null,
            ManufacturerName: "Prusa",
            ModelName: "MINI+",
            Progress: null,
            JobName: null,
            ThumbnailUrl: null,
            CameraStreamUrl: null,
            CameraSnapshotUrl: null,
            X: null, Y: null, Z: null,
            HotendTemp: null, BedTemp: null,
            HotendTarget: null, BedTarget: null,
            Backend: PrinterBackend.Moonraker,
            ApiKey: updateRequest.ApiKey,
            OriginalServerUrl: "http://192.168.1.102",
            IpAddress: "192.168.1.102",
            SpoolInfo: null);

        _printersServiceMock
            .Setup(s => s.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingPrinter);

        _printersServiceMock
            .Setup(s => s.ResolveHostnameAsync(
                updateRequest.ServerUrl,
                PrinterBackend.Moonraker,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolveHostnameResponse(
                NormalizedInputUrl: "http://192.168.1.102",
                ResolvedBaseUrl: "http://192.168.1.102:7125",
                ResolvedIp: "192.168.1.102"));

        _printersServiceMock
            .Setup(s => s.GetCapabilitiesByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterCapabilities?)null);

        _printersServiceMock
            .Setup(s => s.SaveCapabilitiesAsync(It.IsAny<PrinterCapabilities>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _catalogServiceMock
            .Setup(s => s.GetManufacturerByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ManufacturerDto?)null);

        _catalogServiceMock
            .Setup(s => s.GetModelByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterModelDto?)null);

        // Act
        var result = await _controller.UpdateAsync(printerId, updateRequest, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        
        _printersServiceMock.Verify(
            s => s.SaveCapabilitiesAsync(It.IsAny<PrinterCapabilities>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_WithNonExistentPrinter_ReturnsNotFound()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var updateRequest = new UpdatePrinterDto(
            Name: "Updated",
            ServerUrl: "http://192.168.1.102:7125",
            Notes: null,
            ManufacturerId: null,
            ModelId: null,
            NewManufacturerName: null,
            NewModelName: null,
            DateAcquired: null);

        _printersServiceMock
            .Setup(s => s.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Printer?)null);

        // Act
        var result = await _controller.UpdateAsync(printerId, updateRequest, CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task UpdateAsync_WithNullRequest_ThrowsArgumentNullException()
    {
        // Arrange
        var printerId = Guid.NewGuid();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _controller.UpdateAsync(printerId, null!, CancellationToken.None));
    }

    #endregion

    #region DeleteAsync

    [Fact]
    public async Task DeleteAsync_WithValidId_ReturnsNoContent()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "Printer to Delete",
            ServerUrl = "http://192.168.1.100:7125",
            Backend = (int)PrinterBackend.Moonraker
        };

        _printersServiceMock
            .Setup(s => s.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        _printersServiceMock
            .Setup(s => s.RemoveAsync(printer, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.DeleteAsync(printerId, CancellationToken.None);

        // Assert
        Assert.IsType<NoContentResult>(result);
        _printersServiceMock.Verify(
            s => s.RemoveAsync(printer, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_WithNonExistentId_ReturnsNotFound()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        _printersServiceMock
            .Setup(s => s.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Printer?)null);

        // Act
        var result = await _controller.DeleteAsync(printerId, CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result);
        _printersServiceMock.Verify(
            s => s.RemoveAsync(It.IsAny<Printer>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion

    #region GetCameraUrlsAsync

    [Fact]
    public async Task GetCameraUrlsAsync_ReturnsOkWithCameraUrls()
    {
        // Arrange
        var cameraUrls = new List<PrinterCameraUrlsDto>
        {
            new PrinterCameraUrlsDto(
                Id: Guid.NewGuid(),
                Name: "Printer1",
                CameraStreamUrl: "http://192.168.1.100:8080/stream",
                CameraSnapshotUrl: "http://192.168.1.100:8080/snapshot"),
            new PrinterCameraUrlsDto(
                Id: Guid.NewGuid(),
                Name: "Printer2",
                CameraStreamUrl: null,
                CameraSnapshotUrl: null)
        };

        _printersServiceMock
            .Setup(s => s.GetCameraUrlsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(cameraUrls.ToArray());

        // Act
        var result = await _controller.GetCameraUrlsAsync(CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedUrls = Assert.IsAssignableFrom<IEnumerable<PrinterCameraUrlsDto>>(okResult.Value);
        Assert.Equal(2, returnedUrls.Count());
    }

    [Fact]
    public async Task GetCameraUrlsAsync_WhenEmpty_ReturnsEmptyList()
    {
        // Arrange
        _printersServiceMock
            .Setup(s => s.GetCameraUrlsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PrinterCameraUrlsDto>());

        // Act
        var result = await _controller.GetCameraUrlsAsync(CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedUrls = Assert.IsAssignableFrom<IEnumerable<PrinterCameraUrlsDto>>(okResult.Value);
        Assert.Empty(returnedUrls);
    }

    [Fact]
    public async Task GetCameraUrlsAsync_WhenTransientDbException_ReturnsEmptyList()
    {
        // Arrange
        _printersServiceMock
            .Setup(s => s.GetCameraUrlsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        // Act
        var result = await _controller.GetCameraUrlsAsync(CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedUrls = Assert.IsAssignableFrom<IEnumerable<PrinterCameraUrlsDto>>(okResult.Value);
        Assert.Empty(returnedUrls);
    }

    #endregion

    #region GetSnapshotAsync

    [Fact]
    public async Task GetSnapshotAsync_WithValidId_ReturnsImageFile()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var snapshotBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }; // JPEG header

        _printersServiceMock
            .Setup(s => s.GetCameraSnapshotAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshotBytes);

        // Act
        var result = await _controller.GetSnapshotAsync(printerId, CancellationToken.None);

        // Assert
        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/jpeg", fileResult.ContentType);
        Assert.Equal(snapshotBytes, fileResult.FileContents);
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenNotAvailable_ReturnsNotFound()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        _printersServiceMock
            .Setup(s => s.GetCameraSnapshotAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        // Act
        var result = await _controller.GetSnapshotAsync(printerId, CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result);
    }

    #endregion

    #region GetPrintJobStatusAsync

    [Fact]
    public async Task GetPrintJobStatusAsync_WithActivePrintJob_ReturnsStatus()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "Test Printer",
            ServerUrl = "http://192.168.1.100:7125",
            Backend = (int)PrinterBackend.Moonraker
        };

        var jobStatus = new PrintJobStatusDto
        {
            JobName = "benchy.gcode",
            State = "Printing",
            Progress = 50
        };

        _printersServiceMock
            .Setup(s => s.FindByIdWithIncludesAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        _printersServiceMock
            .Setup(s => s.GetPrintJobStatusAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobStatus);

        // Act
        var result = await _controller.GetPrintJobStatusAsync(printerId, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var returnedStatus = Assert.IsType<PrintJobStatusDto>(okResult.Value);
        Assert.Equal("benchy.gcode", returnedStatus.JobName);
        Assert.Equal(50, returnedStatus.Progress);
    }

    [Fact]
    public async Task GetPrintJobStatusAsync_WithNoPrintJob_ReturnsOkWithNull()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "Test Printer",
            ServerUrl = "http://192.168.1.100:7125",
            Backend = (int)PrinterBackend.Moonraker
        };

        _printersServiceMock
            .Setup(s => s.FindByIdWithIncludesAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        _printersServiceMock
            .Setup(s => s.GetPrintJobStatusAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJobStatusDto?)null);

        // Act
        var result = await _controller.GetPrintJobStatusAsync(printerId, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Null(okResult.Value);
    }

    [Fact]
    public async Task GetPrintJobStatusAsync_WithNonExistentPrinter_ReturnsNotFound()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        _printersServiceMock
            .Setup(s => s.FindByIdWithIncludesAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Printer?)null);

        // Act
        var result = await _controller.GetPrintJobStatusAsync(printerId, CancellationToken.None);

        // Assert
        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
        Assert.NotNull(notFoundResult.Value);
    }

    [Fact]
    public async Task GetPrintJobStatusAsync_WhenTimeout_ReturnsOkWithNull()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "Test Printer",
            ServerUrl = "http://192.168.1.100:7125",
            Backend = (int)PrinterBackend.Moonraker
        };

        _printersServiceMock
            .Setup(s => s.FindByIdWithIncludesAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        _printersServiceMock
            .Setup(s => s.GetPrintJobStatusAsync(printerId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        // Act
        var result = await _controller.GetPrintJobStatusAsync(printerId, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Null(okResult.Value);
    }

    #endregion
}
