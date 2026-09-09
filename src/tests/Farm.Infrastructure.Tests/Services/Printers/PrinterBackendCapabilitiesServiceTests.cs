using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Backend.Plugin.FlashForge;
using Farm.Backend.Plugin.Moonraker;
using Farm.Backend.Plugin.OctoPrint;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Printers;

public class PrinterBackendCapabilitiesServiceTests
{
    [Theory]
    [InlineData(PrinterBackend.Moonraker, true, true, true, true)]
    [InlineData(PrinterBackend.OctoPrint, true, true, true, false)]
    [InlineData(PrinterBackend.PrusaLink, true, true, true, false)]
    [InlineData(PrinterBackend.FlashForge, false, false, true, false)]
    [InlineData(PrinterBackend.SDCP, false, false, false, false)]
    [InlineData(PrinterBackend.Unknown, false, false, false, false)]
    [InlineData((PrinterBackend)999, false, false, false, false)]
    public async Task GetByPrinterIdAsync_ConcreteBackend_ReportsOnlyProvenSharedRoutes(
        PrinterBackend backend,
        bool homing,
        bool homingZ,
        bool heaters,
        bool gcode)
    {
        using var http = new HttpClient();
        IBackendClient client = backend switch
        {
            PrinterBackend.Moonraker => new MoonrakerClient(
                http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings()),
            PrinterBackend.OctoPrint => new OctoPrintClient(http),
            PrinterBackend.PrusaLink => new PrusaLinkClient(http),
            PrinterBackend.FlashForge => new FlashForgeClient(
                NullLogger<FlashForgeClient>.Instance, new BackendTimeoutSettings()),
            _ => Mock.Of<IBackendClient>(),
        };
        var clients = new Mock<IBackendClientFactory>();
        clients.Setup(factory => factory.GetClient(It.IsAny<PrinterBackend>())).Returns(client);
        var capabilities = new BackendCapabilityFactory(
            clients.Object, NullLogger<BackendCapabilityFactory>.Instance);
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "capability-test",
            Backend = (int)backend,
            ServerUrl = "http://printer.local",
            BackendPort = 80,
        };
        var repo = new Mock<IPrintersRepository>();
        repo.Setup(repository => repository.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        var service = new PrinterBackendCapabilitiesService(repo.Object, capabilities);

        PrinterBackendCapabilitiesDto dto = Assert.IsType<PrinterBackendCapabilitiesDto>(
            await service.GetByPrinterIdAsync(printer.Id, CancellationToken.None));

        Assert.Equal(homing, dto.SupportsHoming);
        Assert.Equal(homing, dto.SupportsHomingXY);
        Assert.Equal(homingZ, dto.SupportsHomingZ);
        Assert.Equal(heaters, dto.SupportsHotendTemperature);
        Assert.Equal(heaters, dto.SupportsBedTemperature);
        Assert.Equal(gcode, dto.SupportsExtrusion);
        Assert.Equal(gcode, dto.SupportsDisableMotors);
        Assert.Equal(homing ? new[] { "x", "y", "z" } : [], dto.SupportedAxes);
        Assert.True(dto.SupportsZOffset);
        Assert.False(dto.SupportsRelativeMovement);
        Assert.False(dto.SupportsAbsoluteMovement);
        Assert.False(dto.SupportsZOffsetFirmwareSave);
        Assert.False(dto.SupportsFilamentLoad);
        Assert.False(dto.SupportsFilamentUnload);
        Assert.False(dto.SupportsFilamentChange);
        Assert.Equal(
            VerifiedSafetyDiscoveryState.Unavailable,
            dto.VerifiedSafety.Discovery.State);
        Assert.Equal(
            VerifiedSafetySupport.Unknown,
            dto.VerifiedSafety.Operations.AbsoluteMovement.Support);

        if (backend == PrinterBackend.Moonraker)
        {
            Assert.True(dto.SupportsMovement);
            Assert.True(dto.SupportsFilamentControl);
            Assert.True(dto.SupportsControlOperations);
        }
    }

    [Theory]
    [InlineData(PrinterBackend.PrusaLink)]
    [InlineData(PrinterBackend.FlashForge)]
    public async Task GetByPrinterIdAsync_GenericRouteTargetsDifferentPort_DoesNotAdvertiseTemperature(
        PrinterBackend backend)
    {
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Backend = (int)backend,
            ServerUrl = "http://printer.local",
            BackendPort = 8899,
        };
        var repo = new Mock<IPrintersRepository>();
        repo.Setup(repository => repository.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        var factory = new Mock<IBackendCapabilityFactory>();
        ISupportsTemperatureControl? temperatureClient = Mock.Of<ISupportsTemperatureControl>();
        factory.Setup(capabilities => capabilities.TryGetTemperatureControlClientTyped(backend, out temperatureClient))
            .Returns(true);
        var service = new PrinterBackendCapabilitiesService(repo.Object, factory.Object);

        PrinterBackendCapabilitiesDto dto = Assert.IsType<PrinterBackendCapabilitiesDto>(
            await service.GetByPrinterIdAsync(printer.Id, CancellationToken.None));

        Assert.False(dto.SupportsHotendTemperature);
        Assert.False(dto.SupportsBedTemperature);
    }

    [Fact]
    public async Task GetByPrinterIdAsync_BroadFlagsWithoutTypedClients_DoesNotEnablePhysicalOperations()
    {
        var printer = new Printer { Id = Guid.NewGuid(), Backend = (int)PrinterBackend.Moonraker };
        var repo = new Mock<IPrintersRepository>();
        repo.Setup(repository => repository.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        var factory = new Mock<IBackendCapabilityFactory>();
        factory.Setup(capabilities => capabilities.GetSupportedCapabilities(PrinterBackend.Moonraker))
            .Returns(BackendCapabilities.All);
        var service = new PrinterBackendCapabilitiesService(repo.Object, factory.Object);

        PrinterBackendCapabilitiesDto dto = Assert.IsType<PrinterBackendCapabilitiesDto>(
            await service.GetByPrinterIdAsync(printer.Id, CancellationToken.None));

        Assert.True(dto.SupportsMovement);
        Assert.True(dto.SupportsTemperatureControl);
        Assert.True(dto.SupportsFilamentControl);
        Assert.False(dto.SupportsHoming);
        Assert.False(dto.SupportsHotendTemperature);
        Assert.False(dto.SupportsBedTemperature);
        Assert.False(dto.SupportsExtrusion);
        Assert.False(dto.SupportsDisableMotors);
        Assert.Empty(dto.SupportedAxes);
    }

    [Fact]
    public async Task GetByPrinterIdAsync_MissingPrinter_ReturnsNullWithoutResolvingBackend()
    {
        var factory = new Mock<IBackendCapabilityFactory>(MockBehavior.Strict);
        var service = new PrinterBackendCapabilitiesService(Mock.Of<IPrintersRepository>(), factory.Object);

        Assert.Null(await service.GetByPrinterIdAsync(Guid.NewGuid(), CancellationToken.None));
        factory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetByPrinterIdAsync_AuthoritativeDiscovery_ProjectsSupportedOperations()
    {
        Guid printerId = Guid.NewGuid();
        DateTime observedAtUtc = DateTime.UtcNow;
        var supported = new VerifiedSafetyOperationCapabilityDto(
            VerifiedSafetySupport.Supported,
            "test.discovery",
            observedAtUtc);
        PrinterVerifiedSafetyDto discovered = PrinterVerifiedSafetyDto.Unknown(
            discoveryState: VerifiedSafetyDiscoveryState.Partial) with
        {
            Operations = PrinterVerifiedSafetyDto.Unknown().Operations with
            {
                AbsoluteMovement = supported,
                FilamentLoad = supported,
            },
        };
        var printer = new Printer
        {
            Id = printerId,
            Name = "discovered",
            Backend = (int)PrinterBackend.Moonraker,
            ServerUrl = "http://printer.local",
            BackendPort = 7125,
            FrontendPort = 8080,
        };
        var repo = new Mock<IPrintersRepository>();
        repo.Setup(repository => repository.FindByIdAsync(
                printerId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        var client = new Mock<IBackendClient>();
        client.As<ISupportsVerifiedSafetyDiscovery>()
            .Setup(value => value.DiscoverVerifiedSafetyAsync(
                "http://printer.local:8080",
                It.IsAny<PrinterCredential?>(),
                "1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(discovered);
        var backendFactory = new Mock<IBackendClientFactory>();
        backendFactory.Setup(value => value.GetClient(PrinterBackend.Moonraker))
            .Returns(client.Object);
        var service = new PrinterBackendCapabilitiesService(
            repo.Object,
            Mock.Of<IBackendCapabilityFactory>(),
            backendFactory.Object);

        PrinterBackendCapabilitiesDto result =
            Assert.IsType<PrinterBackendCapabilitiesDto>(
                await service.GetByPrinterIdAsync(
                    printerId,
                    CancellationToken.None));

        Assert.True(result.SupportsAbsoluteMovement);
        Assert.True(result.SupportsFilamentLoad);
        Assert.False(result.SupportsFilamentUnload);
        Assert.Same(discovered, result.VerifiedSafety);
    }

    [Fact]
    public async Task InvalidateVerifiedSafety_AcrossServiceScopes_EvictsSharedDiscovery()
    {
        Guid printerId = Guid.NewGuid();
        var supported = new VerifiedSafetyOperationCapabilityDto(
            VerifiedSafetySupport.Supported,
            "test",
            DateTime.UtcNow);
        PrinterVerifiedSafetyDto first = PrinterVerifiedSafetyDto.Unknown() with
        {
            Operations = PrinterVerifiedSafetyDto.Unknown().Operations with
            {
                FilamentLoad = supported,
            },
        };
        PrinterVerifiedSafetyDto second = PrinterVerifiedSafetyDto.Unknown();
        var printer = new Printer
        {
            Id = printerId,
            Backend = (int)PrinterBackend.Moonraker,
            ServerUrl = "http://printer.local",
            FrontendPort = 8080,
        };
        var repo = new Mock<IPrintersRepository>();
        repo.Setup(repository => repository.FindByIdAsync(
                printerId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        var client = new Mock<IBackendClient>();
        client.As<ISupportsVerifiedSafetyDiscovery>()
            .SetupSequence(value => value.DiscoverVerifiedSafetyAsync(
                "http://printer.local:8080",
                It.IsAny<PrinterCredential?>(),
                "1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(first)
            .ReturnsAsync(second);
        var backendFactory = new Mock<IBackendClientFactory>();
        backendFactory.Setup(value => value.GetClient(PrinterBackend.Moonraker))
            .Returns(client.Object);
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var sharedCache = new PrinterVerifiedSafetyCache(memoryCache);
        var firstScope = new PrinterBackendCapabilitiesService(
            repo.Object,
            Mock.Of<IBackendCapabilityFactory>(),
            backendFactory.Object,
            sharedCache);
        var secondScope = new PrinterBackendCapabilitiesService(
            repo.Object,
            Mock.Of<IBackendCapabilityFactory>(),
            backendFactory.Object,
            sharedCache);

        PrinterBackendCapabilitiesDto firstResult =
            (await firstScope.GetByPrinterIdAsync(
                printerId,
                CancellationToken.None))!;
        PrinterBackendCapabilitiesDto cachedAcrossScope =
            (await secondScope.GetByPrinterIdAsync(
                printerId,
                CancellationToken.None))!;
        secondScope.InvalidateVerifiedSafety(printerId);
        PrinterBackendCapabilitiesDto refreshed =
            (await firstScope.GetByPrinterIdAsync(
                printerId,
                CancellationToken.None))!;

        Assert.True(firstResult.SupportsFilamentLoad);
        Assert.True(cachedAcrossScope.SupportsFilamentLoad);
        Assert.False(refreshed.SupportsFilamentLoad);
        client.As<ISupportsVerifiedSafetyDiscovery>().Verify(value =>
            value.DiscoverVerifiedSafetyAsync(
                "http://printer.local:8080",
                It.IsAny<PrinterCredential?>(),
                "1",
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public void Serialize_AdditiveCapabilities_PreservesLegacyNamesAndUsesCamelCase()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        var dto = new PrinterBackendCapabilitiesDto(
            Guid.NewGuid(), "wire-test", PrinterBackend.Moonraker,
            SupportsMovement: true, SupportsFilamentControl: true)
        {
            SupportsExtrusion = true,
            SupportsDisableMotors = true,
            SupportsZOffset = true,
            SupportsHoming = true,
            SupportsHomingXY = true,
            SupportsHomingZ = true,
            SupportsHotendTemperature = true,
            SupportsBedTemperature = true,
            SupportedAxes = ["x", "y", "z"],
        };

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(dto, options));
        Assert.Equal("Moonraker", json.RootElement.GetProperty("backend").GetString());
        Assert.True(json.RootElement.GetProperty("supportsMovement").GetBoolean());
        Assert.True(json.RootElement.GetProperty("supportsFilamentControl").GetBoolean());
        Assert.True(json.RootElement.GetProperty("supportsExtrusion").GetBoolean());
        Assert.True(json.RootElement.GetProperty("supportsDisableMotors").GetBoolean());
        Assert.True(json.RootElement.GetProperty("supportsZOffset").GetBoolean());
        Assert.False(json.RootElement.GetProperty("supportsZOffsetFirmwareSave").GetBoolean());
        Assert.True(json.RootElement.GetProperty("supportsHomingXY").GetBoolean());
        Assert.True(json.RootElement.GetProperty("supportsHomingZ").GetBoolean());
        Assert.False(json.RootElement.GetProperty("supportsRelativeMovement").GetBoolean());
        Assert.False(json.RootElement.GetProperty("supportsAbsoluteMovement").GetBoolean());
        Assert.False(json.RootElement.GetProperty("supportsFilamentLoad").GetBoolean());
        Assert.False(json.RootElement.GetProperty("supportsFilamentUnload").GetBoolean());
        Assert.False(json.RootElement.GetProperty("supportsFilamentChange").GetBoolean());
        Assert.Equal(3, json.RootElement.GetProperty("supportedAxes").GetArrayLength());
        JsonElement safety = json.RootElement.GetProperty("verifiedSafety");
        Assert.Equal(1, safety.GetProperty("contractVersion").GetInt32());
        Assert.Equal(
            "Unavailable",
            safety.GetProperty("discovery").GetProperty("state").GetString());
        Assert.Equal(
            "Unknown",
            safety.GetProperty("operations").GetProperty("absoluteMovement")
                .GetProperty("support").GetString());
    }

    [Fact]
    public void Deserialize_LegacyBroadFlags_DoNotInferSpecificOperations()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        const string payload = """
            {"printerId":"00000000-0000-0000-0000-000000000001","printerName":"old-server",
             "backend":"Moonraker","supportsMovement":true,"supportsTemperatureControl":true,
             "supportsFilamentControl":true,"supportsControlOperations":true}
            """;

        PrinterBackendCapabilitiesDto dto = Assert.IsType<PrinterBackendCapabilitiesDto>(
            JsonSerializer.Deserialize<PrinterBackendCapabilitiesDto>(payload, options));

        Assert.False(dto.SupportsRelativeMovement);
        Assert.False(dto.SupportsAbsoluteMovement);
        Assert.False(dto.SupportsExtrusion);
        Assert.False(dto.SupportsDisableMotors);
        Assert.False(dto.SupportsZOffset);
        Assert.False(dto.SupportsZOffsetFirmwareSave);
        Assert.False(dto.SupportsHoming);
        Assert.False(dto.SupportsHomingXY);
        Assert.False(dto.SupportsHomingZ);
        Assert.False(dto.SupportsHotendTemperature);
        Assert.False(dto.SupportsBedTemperature);
        Assert.False(dto.SupportsFilamentLoad);
        Assert.False(dto.SupportsFilamentUnload);
        Assert.False(dto.SupportsFilamentChange);
        Assert.Empty(dto.SupportedAxes);
    }
}
