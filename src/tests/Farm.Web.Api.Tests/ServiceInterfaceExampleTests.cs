using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services;
using Farm.Web.Shared;
using Moq;

namespace Farm.Web.Api.Tests.Examples;

/// <summary>
/// Example test class demonstrating how to use the service interfaces with mock implementations.
/// This shows how the interfaces enable better testability without requiring actual printer hardware.
/// </summary>
public class ServiceInterfaceExampleTests
{
    /// <summary>
    /// Example showing how to mock IMoonrakerClient for unit testing
    /// </summary>
    [Fact]
    public async Task MockedMoonrakerClient_CanReturnPredefinedStatus()
    {
        // Arrange
        var mockMoonraker = new Mock<IMoonrakerClient>();
        var expectedStatus = new PrinterStatus(IsOnline: true, State: "ready");
        
        mockMoonraker
            .Setup(m => m.GetStatusAsync("http://test-printer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedStatus);

        // Act
        var result = await mockMoonraker.Object.GetStatusAsync("http://test-printer");

        // Assert
        Assert.True(result.IsOnline);
        Assert.Equal("ready", result.State);
        
        // Verify the method was called
        mockMoonraker.Verify(m => m.GetStatusAsync("http://test-printer", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Example showing how to mock ISpoolmanService for testing filament spool functionality
    /// </summary>
    [Fact]
    public async Task MockedSpoolmanService_CanReturnSpoolList()
    {
        // Arrange
        var mockSpoolman = new Mock<ISpoolmanService>();
        var expectedSpools = new List<SpoolmanSpoolDto>
        {
            new SpoolmanSpoolDto(
                Id: 1,
                Name: "Test Spool 1",
                Material: "PLA",
                RemainingWeightG: 500.0,
                ColorHex: "#FF0000",
                InUse: false,
                FilamentName: "Premium PLA",
                Vendor: "Test Vendor"
            ),
            new SpoolmanSpoolDto(
                Id: 2,
                Name: "Test Spool 2",
                Material: "PETG",
                RemainingWeightG: 750.0,
                ColorHex: "#00FF00",
                InUse: true,
                FilamentName: "Premium PETG",
                Vendor: "Test Vendor"
            )
        };

        mockSpoolman
            .Setup(s => s.ListSpoolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedSpools);

        // Act
        var result = await mockSpoolman.Object.ListSpoolsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("Test Spool 1", result[0].Name);
        Assert.Equal("PLA", result[0].Material);
        Assert.Equal("#FF0000", result[0].ColorHex);
    }

    /// <summary>
    /// Example showing how to mock IPresetService for testing filament preset functionality
    /// </summary>
    [Fact]
    public void MockedPresetService_CanReturnPresets()
    {
        // Arrange
        var mockPresets = new Mock<IPresetService>();
        var expectedPresets = new FilamentPresetsDto
        (
            Abs: new TempTargets(230, 100),
            Asa: new TempTargets(245, 100),
            Pla: new TempTargets(205, 60),
            Pc: new TempTargets(260, 110),
            Pctg: new TempTargets(235, 80),
            Petg: new TempTargets(240, 85)
        );

        mockPresets
            .Setup(p => p.GetPresets())
            .Returns(expectedPresets);

        // Act
        var result = mockPresets.Object.GetPresets();

        // Assert
        Assert.Equal(205, result.Pla.Hotend);
        Assert.Equal(60, result.Pla.Bed);
        Assert.Equal(240, result.Petg.Hotend);
        Assert.Equal(85, result.Petg.Bed);
    }

    /// <summary>
    /// Example showing how to mock IPrusaLinkClient for testing Prusa printer functionality
    /// </summary>
    [Fact]
    public async Task MockedPrusaLinkClient_CanReturnJobInfo()
    {
        // Arrange
        var mockPrusa = new Mock<IPrusaLinkClient>();
        var expectedJob = new PrusaJob
        (
            PrintState: "printing",
            Progress: 45.5,
            JobName: "test_print.gcode",
            ThumbnailUrl: "http://prusa/thumb.png",
            CameraStreamUrl: "http://prusa/stream",
            CameraSnapshotUrl: "http://prusa/snapshot"
        );

        mockPrusa
            .Setup(p => p.GetJobAsync("http://prusa-printer", "test-api-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedJob);

        // Act
        var result = await mockPrusa.Object.GetJobAsync("http://prusa-printer", "test-api-key");

        // Assert
        Assert.Equal("test_print.gcode", result?.JobName);
        Assert.Equal(45.5, result?.Progress);
        Assert.Equal("printing", result?.PrintState);
    }

    /// <summary>
    /// Example showing how to mock ISdcpClient for testing SDCP printer functionality
    /// </summary>
    [Fact]
    public async Task MockedSdcpClient_CanReturnCompositeStatus()
    {
        // Arrange
        var mockSdcp = new Mock<ISdcpClient>();
        var expectedStatus = new PrinterCompositeStatus
        (
            IsOnline: true,
            State: "printing",
            Progress: 75.0,
            JobName: "elegoo_print.gcode",
            ThumbnailUrl: null,
            CameraStreamUrl: "http://elegoo-camera/stream",
            CameraSnapshotUrl: null,
            X: 100.5,
            Y: 50.2,
            Z: 15.8,
            HotendTemp: 210.0,
            BedTemp: 60.0,
            HotendTarget: 215.0,
            BedTarget: 65.0
        );

        mockSdcp
            .Setup(s => s.GetCompositeStatusAsync("ws://elegoo-printer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedStatus);

        // Act
        var result = await mockSdcp.Object.GetCompositeStatusAsync("ws://elegoo-printer");

        // Assert
        Assert.True(result.IsOnline);
        Assert.Equal("printing", result.State);
        Assert.Equal(75.0, result.Progress);
        Assert.Equal("elegoo_print.gcode", result.JobName);
        Assert.Equal(210.0, result.HotendTemp);
        Assert.Equal(60.0, result.BedTemp);
    }

    /// <summary>
    /// Example showing how to mock IDatabaseSeeder for testing database initialization
    /// </summary>
    [Fact]
    public async Task MockedDatabaseSeeder_CanSeedData()
    {
        // Arrange
        var mockSeeder = new Mock<IDatabaseSeeder>();
        
        mockSeeder
            .Setup(s => s.SeedCatalogDataAsync())
            .Returns(Task.CompletedTask);

        mockSeeder
            .Setup(s => s.SeedSpoolmanConfigAsync())
            .Returns(Task.CompletedTask);

        // Act
        await mockSeeder.Object.SeedCatalogDataAsync();
        await mockSeeder.Object.SeedSpoolmanConfigAsync();

        // Assert
        mockSeeder.Verify(s => s.SeedCatalogDataAsync(), Times.Once);
        mockSeeder.Verify(s => s.SeedSpoolmanConfigAsync(), Times.Once);
    }
}
