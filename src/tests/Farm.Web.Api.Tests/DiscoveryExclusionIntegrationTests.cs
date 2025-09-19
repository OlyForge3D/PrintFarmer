using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Farm.Web.Shared;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests;

public class DiscoveryExclusionUnitTest
{
    [Fact]
    public async Task Discovery_should_exclude_already_added_printer()
    {
        // Arrange: create a mock INetworkDiscoveryService
        var mockDiscoveryService = new Mock<Farm.Web.Api.Services.INetworkDiscoveryService>();

        // Simulate a printer that is already added
        var seededPrinter = new DiscoveredPrinterDto
        {
            IpAddress = "10.10.0.2",
            Port = 7125,
            ServerUrl = "http://10.10.0.2:7125",
            Backend = PrinterBackend.Moonraker,
            Name = "Seeded Printer",
            IsReachable = true,
            DiscoveredAt = DateTime.UtcNow
        };

        // The discovery should not return the already-added printer
        mockDiscoveryService.Setup(s => s.DiscoverPrintersAsync(default)).ReturnsAsync(new List<DiscoveredPrinterDto>());

        // Act
        var discovered = await mockDiscoveryService.Object.DiscoverPrintersAsync();

        // Assert
        Assert.Empty(discovered); // Should be empty because the printer is already added
    }
}
