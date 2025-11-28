using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Farm.Infrastructure;
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
    public async Task MockedMoonrakerClient_CanReturnPredefinedStatusAsync()
    {
        // Arrange
        Mock<IMoonrakerClient> mockMoonraker = new Mock<IMoonrakerClient>();
        PrinterStatus expectedStatus = new PrinterStatus(IsOnline: true, State: "ready");

        _ = mockMoonraker
            .Setup(m => m.GetStatusAsync("http://test-printer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedStatus);

        // Act
        PrinterStatus result = await mockMoonraker.Object.GetStatusAsync("http://test-printer");

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
    public async Task MockedSpoolmanService_CanReturnSpoolListAsync()
    {
        // Arrange
        Mock<ISpoolmanService> mockSpoolman = new Mock<ISpoolmanService>();
        List<SpoolmanSpoolDto> expectedSpools = new List<SpoolmanSpoolDto>
        {
            new SpoolmanSpoolDto(
                Id: 1,
                Name: "Test Spool 1",
                Material: "PLA",
                RemainingWeightG: 500.0,
                ColorHex: "#FF0000",
                InUse: false,
                FilamentName: "Premium PLA",
                Vendor: "Test Vendor",
                InitialWeightG: 1000.0,
                UsedWeightG: 500.0,
                SpoolWeightG: 140.0,
                RemainingLengthMm: null,
                UsedLengthMm: null,
                Location: null,
                LotNumber: null,
                Archived: false
            ),
            new SpoolmanSpoolDto(
                Id: 2,
                Name: "Test Spool 2",
                Material: "PETG",
                RemainingWeightG: 750.0,
                ColorHex: "#00FF00",
                InUse: true,
                FilamentName: "Premium PETG",
                Vendor: "Test Vendor",
                InitialWeightG: 1000.0,
                UsedWeightG: 250.0,
                SpoolWeightG: 140.0,
                RemainingLengthMm: null,
                UsedLengthMm: null,
                Location: null,
                LotNumber: null,
                Archived: false
            )
        };

        _ = mockSpoolman
            .Setup(s => s.ListSpoolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedSpools);

        // Act
        IReadOnlyList<SpoolmanSpoolDto> result = await mockSpoolman.Object.ListSpoolsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("Test Spool 1", result[0].Name);
        Assert.Equal("PLA", result[0].Material);
        Assert.Equal("#FF0000", result[0].ColorHex);
    }
}

