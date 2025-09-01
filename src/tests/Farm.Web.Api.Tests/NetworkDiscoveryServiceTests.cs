using System.Reflection;
using Farm.Web.Api.Services;
using Farm.Web.Shared;

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
        var printerInfo = Activator.CreateInstance(printerInfoType!)
            ?? throw new InvalidOperationException("Failed to create PrinterInfo instance");

        printerInfoType!.GetProperty("Name")!.SetValue(printerInfo, name);
        printerInfoType.GetProperty("Manufacturer")!.SetValue(printerInfo, "Test Manufacturer");
        printerInfoType.GetProperty("Model")!.SetValue(printerInfo, "Test Model");
        printerInfoType.GetProperty("Firmware")!.SetValue(printerInfo, "Test Firmware");
        printerInfoType.GetProperty("Version")!.SetValue(printerInfo, "1.0.0");

        return printerInfo;
    }

    [Fact]
    public void CreateDiscoveredPrinter_UnknownManufacturer_SetsManufacturerToNull()
    {
        // Arrange
        var ipAddress = "192.168.1.100";
        var port = 7125;
        var backend = PrinterBackend.Moonraker;
        var printerInfo = CreatePrinterInfoWithUnknownManufacturer("Test Printer");

        // Act
        var result = InvokeCreateDiscoveredPrinter(ipAddress, port, backend, printerInfo);

        // Assert
        result.Manufacturer.Should().BeNull("because Unknown manufacturer should not be set");
        result.Model.Should().BeNull("because manufacturer is null, so model should also be null");
    }

    [Fact]
    public void CreateDiscoveredPrinter_UnknownModel_SetsModelToNull()
    {
        // Arrange
        var ipAddress = "192.168.1.100";
        var port = 80;
        var backend = PrinterBackend.PrusaLink;
        var printerInfo = CreatePrinterInfoWithUnknownModel("Test Printer");

        // Act
        var result = InvokeCreateDiscoveredPrinter(ipAddress, port, backend, printerInfo);

        // Assert
        result.Manufacturer.Should().Be("Test Manufacturer", "because manufacturer is not Unknown");
        result.Model.Should().BeNull("because Unknown model should not be set");
    }

    [Fact]
    public void CreateDiscoveredPrinter_BothUnknown_SetsBothToNull()
    {
        // Arrange
        var ipAddress = "192.168.1.100";
        var port = 7125;
        var backend = PrinterBackend.Moonraker;
        var printerInfo = CreatePrinterInfoWithUnknownValues("Test Printer");

        // Act
        var result = InvokeCreateDiscoveredPrinter(ipAddress, port, backend, printerInfo);

        // Assert
        result.Manufacturer.Should().BeNull("because Unknown manufacturer should not be set");
        result.Model.Should().BeNull("because Unknown model should not be set");
    }

    private static object CreatePrinterInfoWithUnknownManufacturer(string name)
    {
        // Use reflection to create an instance of the private PrinterInfo class
        var networkDiscoveryServiceType = typeof(NetworkDiscoveryService);
        var printerInfoType = networkDiscoveryServiceType.GetNestedType("PrinterInfo", BindingFlags.NonPublic);
        var printerInfo = Activator.CreateInstance(printerInfoType!)
            ?? throw new InvalidOperationException("Failed to create PrinterInfo instance");

        printerInfoType!.GetProperty("Name")!.SetValue(printerInfo, name);
        printerInfoType.GetProperty("Manufacturer")!.SetValue(printerInfo, "Unknown");
        printerInfoType.GetProperty("Model")!.SetValue(printerInfo, "Test Model");
        printerInfoType.GetProperty("Firmware")!.SetValue(printerInfo, "Test Firmware");
        printerInfoType.GetProperty("Version")!.SetValue(printerInfo, "1.0.0");

        return printerInfo;
    }

    private static object CreatePrinterInfoWithUnknownModel(string name)
    {
        // Use reflection to create an instance of the private PrinterInfo class
        var networkDiscoveryServiceType = typeof(NetworkDiscoveryService);
        var printerInfoType = networkDiscoveryServiceType.GetNestedType("PrinterInfo", BindingFlags.NonPublic);
        var printerInfo = Activator.CreateInstance(printerInfoType!)
            ?? throw new InvalidOperationException("Failed to create PrinterInfo instance");

        printerInfoType!.GetProperty("Name")!.SetValue(printerInfo, name);
        printerInfoType.GetProperty("Manufacturer")!.SetValue(printerInfo, "Test Manufacturer");
        printerInfoType.GetProperty("Model")!.SetValue(printerInfo, "Unknown");
        printerInfoType.GetProperty("Firmware")!.SetValue(printerInfo, "Test Firmware");
        printerInfoType.GetProperty("Version")!.SetValue(printerInfo, "1.0.0");

        return printerInfo;
    }

    private static object CreatePrinterInfoWithUnknownValues(string name)
    {
        // Use reflection to create an instance of the private PrinterInfo class
        var networkDiscoveryServiceType = typeof(NetworkDiscoveryService);
        var printerInfoType = networkDiscoveryServiceType.GetNestedType("PrinterInfo", BindingFlags.NonPublic);
        var printerInfo = Activator.CreateInstance(printerInfoType!)
            ?? throw new InvalidOperationException("Failed to create PrinterInfo instance");

        printerInfoType!.GetProperty("Name")!.SetValue(printerInfo, name);
        printerInfoType.GetProperty("Manufacturer")!.SetValue(printerInfo, "Unknown");
        printerInfoType.GetProperty("Model")!.SetValue(printerInfo, "Unknown");
        printerInfoType.GetProperty("Firmware")!.SetValue(printerInfo, "Test Firmware");
        printerInfoType.GetProperty("Version")!.SetValue(printerInfo, "1.0.0");

        return printerInfo;
    }

    [Fact]
    public void CreateDiscoveredPrinter_PartialUnknown_KeepsValues()
    {
        // Arrange
        var ipAddress = "192.168.1.100";
        var port = 80;
        var backend = PrinterBackend.PrusaLink;
        var printerInfo = CreatePrinterInfoWithPartialUnknown("Test Printer");

        // Act
        var result = InvokeCreateDiscoveredPrinter(ipAddress, port, backend, printerInfo);

        // Assert
        result.Manufacturer.Should().Be("MyUnknown Manufacturer", "because it doesn't start with Unknown");
        result.Model.Should().Be("Model Unknown Type", "because it doesn't start with Unknown");
    }

    private static object CreatePrinterInfoWithPartialUnknown(string name)
    {
        // Use reflection to create an instance of the private PrinterInfo class
        var networkDiscoveryServiceType = typeof(NetworkDiscoveryService);
        var printerInfoType = networkDiscoveryServiceType.GetNestedType("PrinterInfo", BindingFlags.NonPublic);
        var printerInfo = Activator.CreateInstance(printerInfoType!)
            ?? throw new InvalidOperationException("Failed to create PrinterInfo instance");

        printerInfoType!.GetProperty("Name")!.SetValue(printerInfo, name);
        printerInfoType.GetProperty("Manufacturer")!.SetValue(printerInfo, "MyUnknown Manufacturer");
        printerInfoType.GetProperty("Model")!.SetValue(printerInfo, "Model Unknown Type");
        printerInfoType.GetProperty("Firmware")!.SetValue(printerInfo, "Test Firmware");
        printerInfoType.GetProperty("Version")!.SetValue(printerInfo, "1.0.0");

        return printerInfo;
    }

    [Fact]
    public void CreateDiscoveredPrinter_UnknownPrusa_SetsModelToNull()
    {
        // Arrange - This tests the specific "Unknown Prusa" pattern from PrusaLink discovery
        var ipAddress = "192.168.1.100";
        var port = 80;
        var backend = PrinterBackend.PrusaLink;
        var printerInfo = CreatePrinterInfoWithUnknownPrusa("Test Printer");

        // Act
        var result = InvokeCreateDiscoveredPrinter(ipAddress, port, backend, printerInfo);

        // Assert
        result.Manufacturer.Should().Be("Prusa Research", "because manufacturer is known");
        result.Model.Should().BeNull("because Unknown Prusa should not be set");
    }

    [Fact]
    public void CreateDiscoveredPrinter_NullManufacturerValidModel_SetsBothToNull()
    {
        // Arrange - When manufacturer is null, model should also be set to null
        var ipAddress = "192.168.1.100";
        var port = 7125;
        var backend = PrinterBackend.Moonraker;
        var printerInfo = CreatePrinterInfoWithNullManufacturer("Test Printer");

        // Act
        var result = InvokeCreateDiscoveredPrinter(ipAddress, port, backend, printerInfo);

        // Assert
        result.Manufacturer.Should().BeNull("because manufacturer is null");
        result.Model.Should().BeNull("because manufacturer is null, so model should also be null");
    }

    [Fact]
    public void CreateDiscoveredPrinter_UnknownManufacturerValidModel_SetsBothToNull()
    {
        // Arrange - When manufacturer is "Unknown" (filtered to null) and model is valid, both should be null
        var ipAddress = "192.168.1.100";
        var port = 7125;
        var backend = PrinterBackend.Moonraker;
        var printerInfo = CreatePrinterInfoWithUnknownManufacturerValidModel("Test Printer");

        // Act
        var result = InvokeCreateDiscoveredPrinter(ipAddress, port, backend, printerInfo);

        // Assert
        result.Manufacturer.Should().BeNull("because Unknown manufacturer should not be set");
        result.Model.Should().BeNull("because manufacturer is null, so model should also be null");
    }

    private static object CreatePrinterInfoWithUnknownPrusa(string name)
    {
        // Use reflection to create an instance of the private PrinterInfo class
        var networkDiscoveryServiceType = typeof(NetworkDiscoveryService);
        var printerInfoType = networkDiscoveryServiceType.GetNestedType("PrinterInfo", BindingFlags.NonPublic);
        var printerInfo = Activator.CreateInstance(printerInfoType!)
            ?? throw new InvalidOperationException("Failed to create PrinterInfo instance");

        printerInfoType!.GetProperty("Name")!.SetValue(printerInfo, name);
        printerInfoType.GetProperty("Manufacturer")!.SetValue(printerInfo, "Prusa Research");
        printerInfoType.GetProperty("Model")!.SetValue(printerInfo, "Unknown Prusa");
        printerInfoType.GetProperty("Firmware")!.SetValue(printerInfo, "PrusaLink");
        printerInfoType.GetProperty("Version")!.SetValue(printerInfo, "1.0.0");

        return printerInfo;
    }

    private static object CreatePrinterInfoWithNullManufacturer(string name)
    {
        // Use reflection to create an instance of the private PrinterInfo class
        var networkDiscoveryServiceType = typeof(NetworkDiscoveryService);
        var printerInfoType = networkDiscoveryServiceType.GetNestedType("PrinterInfo", BindingFlags.NonPublic);
        var printerInfo = Activator.CreateInstance(printerInfoType!)
            ?? throw new InvalidOperationException("Failed to create PrinterInfo instance");

        printerInfoType!.GetProperty("Name")!.SetValue(printerInfo, name);
        printerInfoType.GetProperty("Manufacturer")!.SetValue(printerInfo, null);
        printerInfoType.GetProperty("Model")!.SetValue(printerInfo, "Valid Model");
        printerInfoType.GetProperty("Firmware")!.SetValue(printerInfo, "Test Firmware");
        printerInfoType.GetProperty("Version")!.SetValue(printerInfo, "1.0.0");

        return printerInfo;
    }

    private static object CreatePrinterInfoWithUnknownManufacturerValidModel(string name)
    {
        // Use reflection to create an instance of the private PrinterInfo class
        var networkDiscoveryServiceType = typeof(NetworkDiscoveryService);
        var printerInfoType = networkDiscoveryServiceType.GetNestedType("PrinterInfo", BindingFlags.NonPublic);
        var printerInfo = Activator.CreateInstance(printerInfoType!)
            ?? throw new InvalidOperationException("Failed to create PrinterInfo instance");

        printerInfoType!.GetProperty("Name")!.SetValue(printerInfo, name);
        printerInfoType.GetProperty("Manufacturer")!.SetValue(printerInfo, "Unknown");
        printerInfoType.GetProperty("Model")!.SetValue(printerInfo, "Valid Model Name");
        printerInfoType.GetProperty("Firmware")!.SetValue(printerInfo, "Test Firmware");
        printerInfoType.GetProperty("Version")!.SetValue(printerInfo, "1.0.0");

        return printerInfo;
    }

    private static DiscoveredPrinterDto InvokeCreateDiscoveredPrinter(string ipAddress, int port, PrinterBackend backend, object printerInfo)
    {
        // Use reflection to invoke the private static method
        var networkDiscoveryServiceType = typeof(NetworkDiscoveryService);
        var method = networkDiscoveryServiceType.GetMethod("CreateDiscoveredPrinter", BindingFlags.NonPublic | BindingFlags.Static);

        var result = method!.Invoke(null, [ipAddress, port, backend, printerInfo]);
        return (DiscoveredPrinterDto)result!;
    }
}
