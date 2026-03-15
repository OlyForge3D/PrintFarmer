using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Cameras;
using Farm.Infrastructure.Services.Cameras;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Integration tests for Camera Management Phase 1.5 features.
/// Tests the new camera management endpoints, printer relationships, and enum fields.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class CameraManagementTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _client;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public CameraManagementTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    /// <summary>
    /// Helper to create a test camera with optional properties
    /// </summary>
    private async Task<CameraDto> CreateTestCameraAsync(
        string? name = null,
        Guid? printerId = null,
        CameraSource? source = null,
        CameraType? cameraType = null,
        string? streamUrl = null,
        string? snapshotUrl = null)
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ICameraService service = scope.ServiceProvider.GetRequiredService<ICameraService>();

        var request = new CreateCameraDto
        {
            Name = name ?? $"test-camera-{Guid.NewGuid().ToString().Substring(0, 8)}",
            StreamUrl = streamUrl ?? "http://example.com/stream",
            SnapshotUrl = snapshotUrl ?? "http://example.com/snapshot",
            IsEnabled = true,
            PrinterId = printerId,
            Source = source,
            CameraType = cameraType
        };

        if (printerId.HasValue)
        {
            return await service.CreateForPrinterAsync(printerId.Value, request, CancellationToken.None);
        }

        return await service.CreateAsync(request, CancellationToken.None);
    }

    /// <summary>
    /// Helper to create a test printer
    /// </summary>
    private async Task<Printer> CreateTestPrinterAsync(string? name = null)
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Get or create default manufacturer and model
        Manufacturer? manufacturer = await context.Manufacturers.FirstOrDefaultAsync();
        if (manufacturer == null)
        {
            manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Test Manufacturer" };
            context.Manufacturers.Add(manufacturer);
            await context.SaveChangesAsync();
        }

        PrinterModel? model = await context.PrinterModels.FirstOrDefaultAsync();
        if (model == null)
        {
            model = new PrinterModel { Id = Guid.NewGuid(), Name = "Test Model", ManufacturerId = manufacturer.Id };
            context.PrinterModels.Add(model);
            await context.SaveChangesAsync();
        }

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = name ?? $"test-printer-{Guid.NewGuid().ToString().Substring(0, 8)}",
            ServerUrl = $"http://test-printer-{Guid.NewGuid()}.local",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync();
        return printer;
    }

    #region Test 1: GetCameras_ReturnsAllCameras_IncludingNewFields

    [Fact]
    public async Task GetCameras_ReturnsAllCameras_IncludingNewFields()
    {
        // Arrange - Create cameras with new fields
        await CreateTestCameraAsync(
            name: "camera-with-source",
            source: CameraSource.Moonraker,
            cameraType: CameraType.Bed);

        await CreateTestCameraAsync(
            name: "camera-with-type",
            source: CameraSource.PrusaLink,
            cameraType: CameraType.Nozzle);

        // Act
        HttpResponseMessage response = await _client!.GetAsync("/api/cameras");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<CameraDto>? cameras = await response.Content.ReadFromJsonAsync<List<CameraDto>>(_jsonOptions);
        cameras.Should().NotBeNull();
        cameras!.Count.Should().BeGreaterThanOrEqualTo(2);

        CameraDto? camera1 = cameras.FirstOrDefault(c => c.Name == "camera-with-source");
        camera1.Should().NotBeNull();
        camera1!.Source.Should().Be(CameraSource.Moonraker);
        camera1.CameraType.Should().Be(CameraType.Bed);
        camera1.HealthStatus.Should().Be(CameraHealthStatus.Unknown);

        CameraDto? camera2 = cameras.FirstOrDefault(c => c.Name == "camera-with-type");
        camera2.Should().NotBeNull();
        camera2!.Source.Should().Be(CameraSource.PrusaLink);
        camera2.CameraType.Should().Be(CameraType.Nozzle);
    }

    #endregion

    #region Test 2: CreateCamera_WithPrinterId_LinksCameraToPrinter

    [Fact]
    public async Task CreateCamera_WithPrinterId_LinksCameraToPrinter()
    {
        // Arrange - Create a test printer first
        Printer printer = await CreateTestPrinterAsync("test-printer-for-camera");

        // Act - Create a camera linked to the printer
        CameraDto camera = await CreateTestCameraAsync(
            name: "printer-camera",
            printerId: printer.Id,
            source: CameraSource.Moonraker,
            cameraType: CameraType.General);

        // Assert
        camera.Should().NotBeNull();
        camera.PrinterId.Should().Be(printer.Id);
        camera.Source.Should().Be(CameraSource.Moonraker);
        camera.IsStandalone.Should().BeFalse();

        // Verify the link in the database
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Camera? dbCamera = await context.Cameras
            .Include(c => c.Printer)
            .FirstOrDefaultAsync(c => c.Id == camera.Id);

        dbCamera.Should().NotBeNull();
        dbCamera!.PrinterId.Should().Be(printer.Id);
        dbCamera.Printer.Should().NotBeNull();
        dbCamera.Printer!.Id.Should().Be(printer.Id);
    }

    #endregion

    #region Test 3: CreateCamera_WithInvalidPrinterId_Returns400or404

    [Fact]
    public async Task CreateCamera_WithInvalidPrinterId_Returns400or404()
    {
        // Arrange - Use a non-existent printer ID
        Guid invalidPrinterId = Guid.NewGuid();

        // Act & Assert
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ICameraService service = scope.ServiceProvider.GetRequiredService<ICameraService>();

        var request = new CreateCameraDto
        {
            Name = "camera-with-invalid-printer",
            StreamUrl = "http://example.com/stream"
        };

        // Should throw an exception or return null when trying to create with invalid printer ID
        Func<Task> act = async () => await service.CreateForPrinterAsync(invalidPrinterId, request, CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();
    }

    #endregion

    #region Test 4: CreateCamera_Standalone_DefaultsSourceToStandalone

    [Fact]
    public async Task CreateCamera_Standalone_DefaultsSourceToStandalone()
    {
        // Arrange & Act - Create camera without specifying source or printer
        CameraDto camera = await CreateTestCameraAsync(name: "standalone-camera");

        // Assert
        camera.Should().NotBeNull();
        camera.Source.Should().Be(CameraSource.Standalone);
        camera.CameraType.Should().Be(CameraType.General);
        camera.HealthStatus.Should().Be(CameraHealthStatus.Unknown);
        camera.PrinterId.Should().BeNull();
        camera.IsStandalone.Should().BeTrue();
    }

    #endregion

    #region Test 5: GetCamerasByPrinter_ReturnsOnlyLinkedCameras

    [Fact]
    public async Task GetCamerasByPrinter_ReturnsOnlyLinkedCameras()
    {
        // Arrange - Create printer and cameras
        Printer printer1 = await CreateTestPrinterAsync("printer-1");
        Printer printer2 = await CreateTestPrinterAsync("printer-2");

        // Create 2 cameras for printer1
        await CreateTestCameraAsync(name: "printer1-camera1", printerId: printer1.Id);
        await CreateTestCameraAsync(name: "printer1-camera2", printerId: printer1.Id);

        // Create 1 camera for printer2
        await CreateTestCameraAsync(name: "printer2-camera", printerId: printer2.Id);

        // Create 1 standalone camera
        await CreateTestCameraAsync(name: "standalone-camera");

        // Act - Get cameras for printer1
        HttpResponseMessage response = await _client!.GetAsync($"/api/cameras/by-printer/{printer1.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<CameraDto>? cameras = await response.Content.ReadFromJsonAsync<List<CameraDto>>(_jsonOptions);
        cameras.Should().NotBeNull();
        cameras!.Should().HaveCount(2);
        cameras.Should().OnlyContain(c => c.PrinterId == printer1.Id);
        cameras.Should().Contain(c => c.Name == "printer1-camera1");
        cameras.Should().Contain(c => c.Name == "printer1-camera2");
        cameras.Should().NotContain(c => c.Name == "printer2-camera");
        cameras.Should().NotContain(c => c.Name == "standalone-camera");
    }

    #endregion

    #region Test 6: GetCamerasByPrinter_EmptyPrinter_ReturnsEmptyList

    [Fact]
    public async Task GetCamerasByPrinter_EmptyPrinter_ReturnsEmptyList()
    {
        // Arrange - Create printer without cameras
        Printer printer = await CreateTestPrinterAsync("empty-printer");

        // Act
        HttpResponseMessage response = await _client!.GetAsync($"/api/cameras/by-printer/{printer.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<CameraDto>? cameras = await response.Content.ReadFromJsonAsync<List<CameraDto>>(_jsonOptions);
        cameras.Should().NotBeNull();
        cameras!.Should().BeEmpty();
    }

    #endregion

    #region Test 7: ToggleCamera_UpdatesIsEnabled

    [Fact]
    public async Task ToggleCamera_UpdatesIsEnabled()
    {
        // Arrange - Create an enabled camera
        CameraDto camera = await CreateTestCameraAsync(name: "camera-to-toggle");
        camera.IsEnabled.Should().BeTrue();

        // Act - Toggle to disabled
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ICameraService service = scope.ServiceProvider.GetRequiredService<ICameraService>();
        CameraDto? toggledCamera = await service.ToggleEnabledAsync(camera.Id, false, CancellationToken.None);

        // Assert
        toggledCamera.Should().NotBeNull();
        toggledCamera!.IsEnabled.Should().BeFalse();

        // Act - Toggle back to enabled
        CameraDto? reToggledCamera = await service.ToggleEnabledAsync(camera.Id, true, CancellationToken.None);

        // Assert
        reToggledCamera.Should().NotBeNull();
        reToggledCamera!.IsEnabled.Should().BeTrue();
    }

    #endregion

    #region Test 8: UpdateCamera_CanChangeCameraType

    [Fact]
    public async Task UpdateCamera_CanChangeCameraType()
    {
        // Arrange - Create camera with General type
        CameraDto camera = await CreateTestCameraAsync(
            name: "camera-to-update",
            cameraType: CameraType.General);
        camera.CameraType.Should().Be(CameraType.General);

        // Act - Update to Bed type
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ICameraService service = scope.ServiceProvider.GetRequiredService<ICameraService>();

        var updateDto = new UpdateCameraDto
        {
            CameraType = CameraType.Bed
        };

        CameraDto? updatedCamera = await service.UpdateAsync(camera.Id, updateDto, CancellationToken.None);

        // Assert
        updatedCamera.Should().NotBeNull();
        updatedCamera!.CameraType.Should().Be(CameraType.Bed);
        updatedCamera.Name.Should().Be(camera.Name); // Other fields should remain unchanged

        // Verify in database
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Camera? dbCamera = await context.Cameras.FindAsync(camera.Id);
        dbCamera.Should().NotBeNull();
        dbCamera!.CameraType.Should().Be(CameraType.Bed);
    }

    #endregion

    #region Test 9: DeleteCamera_CascadesFromPrinter

    [Fact]
    public async Task DeleteCamera_CascadesFromPrinter()
    {
        // Arrange - Create printer with camera
        Printer printer = await CreateTestPrinterAsync("printer-for-cascade-test");
        CameraDto camera = await CreateTestCameraAsync(
            name: "camera-for-cascade",
            printerId: printer.Id);

        // Verify camera exists
        using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Camera? existingCamera = await context.Cameras.FindAsync(camera.Id);
            existingCamera.Should().NotBeNull();
        }

        // Act - Delete the printer
        using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Printer? printerToDelete = await context.Printers.FindAsync(printer.Id);
            printerToDelete.Should().NotBeNull();
            context.Printers.Remove(printerToDelete!);
            await context.SaveChangesAsync();
        }

        // Assert - Camera should be deleted via cascade
        using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Camera? deletedCamera = await context.Cameras.FindAsync(camera.Id);
            deletedCamera.Should().BeNull();
        }
    }

    #endregion

    #region Test 10: CreateCamera_WithSource_PreservesSource

    [Fact]
    public async Task CreateCamera_WithSource_PreservesSource()
    {
        // Arrange & Act - Create cameras with different sources
        CameraDto moonrakerCamera = await CreateTestCameraAsync(
            name: "moonraker-camera",
            source: CameraSource.Moonraker);

        CameraDto prusalinkCamera = await CreateTestCameraAsync(
            name: "prusalink-camera",
            source: CameraSource.PrusaLink);

        CameraDto octoprintCamera = await CreateTestCameraAsync(
            name: "octoprint-camera",
            source: CameraSource.OctoPrint);

        CameraDto sdcpCamera = await CreateTestCameraAsync(
            name: "sdcp-camera",
            source: CameraSource.SDCP);

        CameraDto flashforgeCamera = await CreateTestCameraAsync(
            name: "flashforge-camera",
            source: CameraSource.FlashForge);

        // Assert
        moonrakerCamera.Source.Should().Be(CameraSource.Moonraker);
        prusalinkCamera.Source.Should().Be(CameraSource.PrusaLink);
        octoprintCamera.Source.Should().Be(CameraSource.OctoPrint);
        sdcpCamera.Source.Should().Be(CameraSource.SDCP);
        flashforgeCamera.Source.Should().Be(CameraSource.FlashForge);

        // Verify in database
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Camera? dbMoonraker = await context.Cameras.FindAsync(moonrakerCamera.Id);
        Camera? dbPrusaLink = await context.Cameras.FindAsync(prusalinkCamera.Id);
        Camera? dbOctoPrint = await context.Cameras.FindAsync(octoprintCamera.Id);
        Camera? dbSdcp = await context.Cameras.FindAsync(sdcpCamera.Id);
        Camera? dbFlashForge = await context.Cameras.FindAsync(flashforgeCamera.Id);

        dbMoonraker!.Source.Should().Be(CameraSource.Moonraker);
        dbPrusaLink!.Source.Should().Be(CameraSource.PrusaLink);
        dbOctoPrint!.Source.Should().Be(CameraSource.OctoPrint);
        dbSdcp!.Source.Should().Be(CameraSource.SDCP);
        dbFlashForge!.Source.Should().Be(CameraSource.FlashForge);
    }

    #endregion

    #region Test 11: CameraEntity_DefaultValues_AreCorrect

    [Fact]
    public void CameraEntity_DefaultValues_AreCorrect()
    {
        // Arrange & Act - Create a new Camera entity without setting properties
        var camera = new Camera
        {
            Id = Guid.NewGuid(),
            Name = "test-camera"
        };

        // Assert - Verify default values
        camera.Source.Should().Be(CameraSource.Standalone);
        camera.CameraType.Should().Be(CameraType.General);
        camera.HealthStatus.Should().Be(CameraHealthStatus.Unknown);
        camera.IsEnabled.Should().BeTrue();
        camera.SortOrder.Should().Be(0);
        camera.ConsecutiveFailures.Should().Be(0);
        camera.PrinterId.Should().BeNull();
        camera.Printer.Should().BeNull();
        camera.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    #endregion

    #region Test 12: CameraEntity_PrinterRelationship_IsOptional

    [Fact]
    public async Task CameraEntity_PrinterRelationship_IsOptional()
    {
        // Arrange - Create standalone camera (no printer)
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var standaloneCamera = new Camera
        {
            Id = Guid.NewGuid(),
            Name = "standalone-camera-entity",
            StreamUrl = "http://example.com/stream",
            PrinterId = null, // Explicitly null
            Printer = null    // Explicitly null
        };

        context.Cameras.Add(standaloneCamera);
        await context.SaveChangesAsync();

        // Act - Retrieve the camera
        Camera? retrievedCamera = await context.Cameras
            .Include(c => c.Printer)
            .FirstOrDefaultAsync(c => c.Id == standaloneCamera.Id);

        // Assert - Printer relationship should be optional and null
        retrievedCamera.Should().NotBeNull();
        retrievedCamera!.PrinterId.Should().BeNull();
        retrievedCamera.Printer.Should().BeNull();
        retrievedCamera.Name.Should().Be("standalone-camera-entity");

        // Now test with a printer
        Printer testPrinter = await CreateTestPrinterAsync("printer-for-relationship-test");

        var printerCamera = new Camera
        {
            Id = Guid.NewGuid(),
            Name = "printer-camera-entity",
            StreamUrl = "http://example.com/stream",
            PrinterId = testPrinter.Id
        };

        context.Cameras.Add(printerCamera);
        await context.SaveChangesAsync();

        // Act - Retrieve the printer camera
        Camera? retrievedPrinterCamera = await context.Cameras
            .Include(c => c.Printer)
            .FirstOrDefaultAsync(c => c.Id == printerCamera.Id);

        // Assert - Printer relationship should be populated
        retrievedPrinterCamera.Should().NotBeNull();
        retrievedPrinterCamera!.PrinterId.Should().Be(testPrinter.Id);
        retrievedPrinterCamera.Printer.Should().NotBeNull();
        retrievedPrinterCamera.Printer!.Id.Should().Be(testPrinter.Id);
    }

    #endregion
}
