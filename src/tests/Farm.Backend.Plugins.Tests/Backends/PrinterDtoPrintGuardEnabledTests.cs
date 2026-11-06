using System.Net;
using System.Text.Json;
using Farm.Backend.Plugin.Moonraker;
using Farm.Backend.Plugin.OctoPrint;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Backend.Plugin.Sdcp;
using Farm.Backend.Plugin.TestEmulator;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.OctoPrint;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Cameras;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Backend.Plugins.Tests.Backends;

public sealed class PrinterDtoPrintGuardEnabledTests
{
    public static TheoryData<string, bool, bool> MappingCases
    {
        get
        {
            TheoryData<string, bool, bool> cases = [];
            foreach (string mapping in new[] { "Moonraker", "OctoPrintRaw", "OctoPrintTyped", "PrusaLink", "Sdcp", "TestEmulator" })
            {
                foreach (bool enabled in new[] { true, false })
                {
                    foreach (bool online in new[] { true, false })
                    {
                        cases.Add(mapping, enabled, online);
                    }
                }
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(MappingCases))]
    public async Task BackendMapping_PreservesStoredPrintGuardEnabled(string mapping, bool enabled, bool online)
    {
        Printer printer = CreatePrinter(enabled);
        using var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        PrinterCompositeStatus status = new(online, online ? "idle" : "offline", 0, null, null, null, null);
        PrinterDto dto;
        switch (mapping)
        {
            case "Moonraker":
                dto = await new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings())
                    .CreatePrinterDtoAsync(printer, status, null);
                break;
            case "OctoPrintRaw":
                dto = await new OctoPrintClient(http).CreatePrinterDtoAsync(
                    printer, online ? """{"state":"Operational"}""" : """{"state":"Offline"}""", "{}", printer.Credential);
                break;
            case "OctoPrintTyped":
                dto = await CreateOctoPrintStatusClient(printer, online).GetPrinterDtoAsync(printer, CancellationToken.None);
                break;
            case "PrusaLink":
                dto = await new PrusaLinkClient(http).CreatePrinterDtoAsync(
                    printer, new PrusaCompositeStatus(online, status.State, 0, null, null, null, null));
                break;
            case "Sdcp":
                dto = await new SdcpClient(http, NullLogger<SdcpClient>.Instance, new BackendTimeoutSettings())
                    .CreatePrinterDtoAsync(printer, status);
                break;
            case "TestEmulator":
                printer.Backend = 100;
                printer.ServerUrl = $"http://testemulator-{printer.Id}";
                var states = new TestEmulatorStateManager();
                states.Register(printer.Id, new EmulatedPrinterState
                {
                    State = online ? EmulatorPrinterState.Idle : EmulatorPrinterState.Offline,
                });
                dto = await new TestEmulatorClient(states).GetPrinterDtoAsync(printer, CancellationToken.None);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mapping), mapping, "Unknown mapping.");
        }

        Assert.Equal(online, dto.IsOnline);
        AssertFlag(dto, enabled);
        Assert.Equal(enabled, printer.PrintGuardEnabled);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task GetPrinterDtoAsync_PreservesFlagThroughLiveAndOfflineFallback(bool enabled, bool backendFails)
    {
        Printer printer = CreatePrinter(enabled);
        var printers = new Mock<IPrintersRepository>();
        printers.Setup(repository => repository.FindByIdWithIncludesAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        var cameras = new Mock<ICameraRepository>();
        cameras.Setup(repository => repository.GetByPrinterIdAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.Printers).Returns(printers.Object);
        unitOfWork.Setup(work => work.Cameras).Returns(cameras.Object);
        var factory = new Mock<IPrinterStatusClientFactory>();
        if (backendFails)
        {
            factory.Setup(value => value.GetStatusClient(printer.Backend))
                .Throws(new HttpRequestException("Simulated unavailable backend."));
        }
        else
        {
            factory.Setup(value => value.GetStatusClient(printer.Backend))
                .Returns(CreateOctoPrintStatusClient(printer, true));
        }

        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().Options);
        var service = new PrintersService(
            unitOfWork.Object,
            db,
            Mock.Of<IBackendClientFactory>(),
            Mock.Of<IBackendCapabilityFactory>(),
            Mock.Of<Farm.Infrastructure.Services.Catalog.ICatalogService>(),
            Mock.Of<IHttpClientFactory>(),
            NullLogger<PrintersService>.Instance,
            Mock.Of<IPrinterStatusBroadcaster>(),
            Mock.Of<IMultiPrinterStatusCoordinator>(),
            factory.Object,
            Mock.Of<IPrinterStatusCacheReader>(),
            Mock.Of<Farm.Infrastructure.Services.Locations.ILocationService>(),
            Mock.Of<Farm.Infrastructure.Services.Security.ISensitiveDataProtector>(),
            Mock.Of<Farm.Infrastructure.Services.Interfaces.ISpoolmanService>(),
            Mock.Of<Farm.Infrastructure.Services.Cameras.IGo2RtcService>(),
            Mock.Of<Farm.Infrastructure.Services.StorageManagement.IStoragePathService>(),
            Mock.Of<Farm.Infrastructure.Services.Spoolman.IFilamentCoverageSpoolResolver>());

        PrinterDto dto = await service.GetPrinterDtoAsync(printer.Id, CancellationToken.None);

        Assert.Equal(!backendFails, dto.IsOnline);
        AssertFlag(dto, enabled);
        Assert.Equal(enabled, printer.PrintGuardEnabled);
    }

    private static Printer CreatePrinter(bool enabled) => new()
    {
        Id = Guid.NewGuid(),
        Name = "PrintGuard projection fixture",
        ServerUrl = "http://printer.invalid",
        Backend = (int)PrinterBackend.OctoPrint,
        BackendPort = 80,
        PrintGuardEnabled = enabled,
    };

    private static OctoPrintStatusClient CreateOctoPrintStatusClient(Printer printer, bool online)
    {
        var client = new Mock<IOctoPrintClient>();
        client.Setup(value => value.GetPrinterStateAsync(printer.BackendUrl, printer.Credential))
            .ReturnsAsync(new OctoPrintPrinterState { Operational = online, State = online ? "Operational" : "Offline" });
        client.Setup(value => value.GetJobStatusAsync(printer.BackendUrl, printer.Credential))
            .ReturnsAsync(new OctoPrintJobStatus());
        return new OctoPrintStatusClient(
            client.Object,
            new CircuitBreakerService(NullLogger<CircuitBreakerService>.Instance),
            new ManagedSpoolProviderHelper(Mock.Of<ISpoolmanStatusCache>(), NullLogger<ManagedSpoolProviderHelper>.Instance),
            NullLogger<OctoPrintStatusClient>.Instance);
    }

    private static void AssertFlag(PrinterDto dto, bool enabled)
    {
        Assert.Equal(enabled, dto.PrintGuardEnabled);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(enabled, json.RootElement.GetProperty("printGuardEnabled").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("PrintGuardEnabled", out _));
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"plugins":[]}""") });
    }
}
