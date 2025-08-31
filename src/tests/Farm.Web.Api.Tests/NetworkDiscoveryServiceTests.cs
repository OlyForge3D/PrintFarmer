using Farm.Web.Api.Services;
using Farm.Web.Shared;
using FluentAssertions;
using System.Reflection;
using Xunit;

namespace Farm.Web.Api.Tests;

public class NetworkDiscoveryServiceTests
{
    [Fact]
    public void CreateDiscoveredPrinter_MoonrakerPort80_OmitsPortFromUrl()
    {
        // Arrange
        var ipAddress = "192.168.1.100";
        var port = 80;
        var backend = PrinterBackend.Moonraker;
        var printerInfo = CreatePrinterInfo("Test Printer");

        // Act
        var result = InvokeCreateDiscoveredPrinter(ipAddress, port, backend, printerInfo);

        // Assert
        result.ServerUrl.Should().Be("http://192.168.1.100");
        result.Port.Should().Be(80);
        result.Backend.Should().Be(PrinterBackend.Moonraker);
    }

    [Fact]
    public void CreateDiscoveredPrinter_MoonrakerPort7125_IncludesPortInUrl()
    {
        // Arrange
        var ipAddress = "192.168.1.100";
        var port = 7125;
        var backend = PrinterBackend.Moonraker;
        var printerInfo = CreatePrinterInfo("Test Printer");

        // Act
        var result = InvokeCreateDiscoveredPrinter(ipAddress, port, backend, printerInfo);

        // Assert
        result.ServerUrl.Should().Be("http://192.168.1.100:7125");
        result.Port.Should().Be(7125);
        result.Backend.Should().Be(PrinterBackend.Moonraker);
    }

    [Fact]
    public void CreateDiscoveredPrinter_PrusaLinkPort80_IncludesPortInUrl()
    {
        // Arrange
        var ipAddress = "192.168.1.100";
        var port = 80;
        var backend = PrinterBackend.PrusaLink;
        var printerInfo = CreatePrinterInfo("Test Printer");

        // Act
        var result = InvokeCreateDiscoveredPrinter(ipAddress, port, backend, printerInfo);

        // Assert
        result.ServerUrl.Should().Be("http://192.168.1.100:80");
        result.Port.Should().Be(80);
        result.Backend.Should().Be(PrinterBackend.PrusaLink);
    }

    private static object CreatePrinterInfo(string name)
    {
        // Use reflection to create an instance of the private PrinterInfo class
        var networkDiscoveryServiceType = typeof(NetworkDiscoveryService);
        var printerInfoType = networkDiscoveryServiceType.GetNestedType("PrinterInfo", BindingFlags.NonPublic);
        var printerInfo = Activator.CreateInstance(printerInfoType!);
        
        printerInfoType!.GetProperty("Name")!.SetValue(printerInfo, name);
        printerInfoType.GetProperty("Manufacturer")!.SetValue(printerInfo, "Test Manufacturer");
        printerInfoType.GetProperty("Model")!.SetValue(printerInfo, "Test Model");
        printerInfoType.GetProperty("Firmware")!.SetValue(printerInfo, "Test Firmware");
        printerInfoType.GetProperty("Version")!.SetValue(printerInfo, "1.0.0");
        
        return printerInfo;
    }

    private static DiscoveredPrinterDto InvokeCreateDiscoveredPrinter(string ipAddress, int port, PrinterBackend backend, object printerInfo)
    {
        // Use reflection to invoke the private static method
        var networkDiscoveryServiceType = typeof(NetworkDiscoveryService);
        var method = networkDiscoveryServiceType.GetMethod("CreateDiscoveredPrinter", BindingFlags.NonPublic | BindingFlags.Static);
        
        var result = method!.Invoke(null, new object[] { ipAddress, port, backend, printerInfo });
        return (DiscoveredPrinterDto)result!;
    }
}
