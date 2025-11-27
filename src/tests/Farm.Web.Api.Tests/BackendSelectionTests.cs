using Farm.Web.Shared;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests;

/// <summary>
/// Tests for backend selection feature in network discovery
/// </summary>
public class BackendSelectionTests
{
    [Fact]
    public void NetworkDiscoverySettingsDto_ShouldAcceptBackendsParameter()
    {
        // Arrange
        List<PrinterBackend> backends = new List<PrinterBackend> { PrinterBackend.Moonraker, PrinterBackend.PrusaLink };

        // Act
        NetworkDiscoverySettingsDto settings = new NetworkDiscoverySettingsDto(
            NetworkRanges: new List<string> { "192.168.1.0/24" },
            TimeoutMs: 3000,
            MaxConcurrentScans: 20,
            Backends: backends
        );

        // Assert
        settings.Backends.Should().NotBeNull();
        settings.Backends.Should().HaveCount(2);
        settings.Backends.Should().Contain(PrinterBackend.Moonraker);
        settings.Backends.Should().Contain(PrinterBackend.PrusaLink);
    }

    [Fact]
    public void NetworkDiscoverySettingsDto_ShouldAllowNullBackends()
    {
        // Arrange & Act
        NetworkDiscoverySettingsDto settings = new NetworkDiscoverySettingsDto(
            NetworkRanges: new List<string> { "192.168.1.0/24" },
            TimeoutMs: 3000,
            MaxConcurrentScans: 20,
            Backends: null
        );

        // Assert
        settings.Backends.Should().BeNull();
    }

    [Fact]
    public void NetworkDiscoverySettingsDto_DefaultConstructor_ShouldHaveNullBackends()
    {
        // Arrange & Act
        NetworkDiscoverySettingsDto settings = new NetworkDiscoverySettingsDto();

        // Assert
        settings.Backends.Should().BeNull();
        settings.NetworkRanges.Should().BeEmpty();
        settings.TimeoutMs.Should().Be(3000);
        settings.MaxConcurrentScans.Should().Be(20);
        // NOTE: Ports parameter removed - each discovery probe handles its own backend-specific ports
    }

    [Fact]
    public void StartDiscoveryRequest_ShouldAcceptBackendsList()
    {
        // Arrange
        List<PrinterBackend> backends = new List<PrinterBackend> { PrinterBackend.SDCP };

        // Act
        StartDiscoveryRequest request = new StartDiscoveryRequest(Backends: backends);

        // Assert
        request.Backends.Should().NotBeNull();
        request.Backends.Should().HaveCount(1);
        request.Backends.Should().Contain(PrinterBackend.SDCP);
    }

    [Fact]
    public void StartDiscoveryRequest_ShouldAllowNullBackends()
    {
        // Arrange & Act
        StartDiscoveryRequest request = new StartDiscoveryRequest(Backends: null);

        // Assert
        request.Backends.Should().BeNull();
    }

    [Fact]
    public void StartDiscoveryRequest_DefaultConstructor_ShouldHaveNullBackends()
    {
        // Arrange & Act
        StartDiscoveryRequest request = new StartDiscoveryRequest();

        // Assert
        request.Backends.Should().BeNull();
    }

    [Theory]
    [InlineData(PrinterBackend.Moonraker)]
    [InlineData(PrinterBackend.PrusaLink)]
    [InlineData(PrinterBackend.SDCP)]
    [InlineData(PrinterBackend.OctoPrint)]
    public void NetworkDiscoverySettingsDto_ShouldAcceptSingleBackend(PrinterBackend backend)
    {
        // Arrange
        List<PrinterBackend> backends = new List<PrinterBackend> { backend };

        // Act
        NetworkDiscoverySettingsDto settings = new NetworkDiscoverySettingsDto(
            NetworkRanges: new List<string> { "10.0.0.0/24" },
            Backends: backends
        );

        // Assert
        settings.Backends.Should().NotBeNull();
        settings.Backends.Should().HaveCount(1);
        settings.Backends.Should().Contain(backend);
    }

    [Fact]
    public void NetworkDiscoverySettingsDto_ShouldAcceptAllBackends()
    {
        // Arrange
        List<PrinterBackend> backends = new List<PrinterBackend>
        {
            PrinterBackend.Moonraker,
            PrinterBackend.PrusaLink,
            PrinterBackend.SDCP,
            PrinterBackend.OctoPrint
        };

        // Act
        NetworkDiscoverySettingsDto settings = new NetworkDiscoverySettingsDto(
            NetworkRanges: new List<string> { "172.16.0.0/16" },
            Backends: backends
        );

        // Assert
        settings.Backends.Should().NotBeNull();
        settings.Backends.Should().HaveCount(4);
        settings.Backends.Should().Contain(PrinterBackend.Moonraker);
        settings.Backends.Should().Contain(PrinterBackend.PrusaLink);
        settings.Backends.Should().Contain(PrinterBackend.SDCP);
        settings.Backends.Should().Contain(PrinterBackend.OctoPrint);
    }

    [Fact]
    public void NetworkDiscoverySettingsDto_ShouldAcceptEmptyBackendsList()
    {
        // Arrange
        List<PrinterBackend> backends = new List<PrinterBackend>();

        // Act
        NetworkDiscoverySettingsDto settings = new NetworkDiscoverySettingsDto(
            NetworkRanges: new List<string> { "192.168.0.0/16" },
            Backends: backends
        );

        // Assert
        settings.Backends.Should().NotBeNull();
        settings.Backends.Should().BeEmpty();
    }
}
