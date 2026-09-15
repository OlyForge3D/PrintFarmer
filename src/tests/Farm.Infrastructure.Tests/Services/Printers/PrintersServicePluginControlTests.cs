using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Infrastructure.Tests.Services.Printers;

public sealed class PrintersServicePluginControlTests
{
    [Theory]
    [InlineData(PrinterBackend.Moonraker)]
    [InlineData(PrinterBackend.OctoPrint)]
    [InlineData(PrinterBackend.PrusaLink)]
    public async Task ControlMethods_AnyCapablePlugin_ForwardSemanticValuesAndCredentials(PrinterBackend backend)
    {
        await using var db = CreateDbContext();
        Printer printer = CreatePrinter(backend);
        using var cancellation = new CancellationTokenSource();
        CancellationToken ct = cancellation.Token;
        var client = new Mock<IBackendClient>(MockBehavior.Strict);
        Mock<ISupportsMovement> movement = client.As<ISupportsMovement>();
        Mock<ISupportsTemperatureControl> temperature = client.As<ISupportsTemperatureControl>();
        Mock<ISupportsEmergencyStop> emergency = client.As<ISupportsEmergencyStop>();
        Mock<ISupportsMotorControl> motors = client.As<ISupportsMotorControl>();
        Mock<ISupportsExtrusionControl> extrusion = client.As<ISupportsExtrusionControl>();
        Mock<ISupportsMmuControl> mmu = client.As<ISupportsMmuControl>();
        Mock<ISupportsZOffsetCalibration> calibration = client.As<ISupportsZOffsetCalibration>();
        Mock<ISupportsGcodeExecution> rawCommands = client.As<ISupportsGcodeExecution>();
        var mmuRequest = new MmuControlRequest(MmuControlAction.ChangeTool, Tool: 2);
        movement.Setup(c => c.HomeAsync(printer.BackendUrl, It.Is<PrinterCredential?>(v => v != null && v.ApiKey == "test-key"), ct)).ReturnsAsync(true);
        movement.Setup(c => c.HomeXYAsync(printer.BackendUrl, It.Is<PrinterCredential?>(v => v != null && v.Password == "test-password"), ct)).ReturnsAsync(true);
        movement.Setup(c => c.MoveAsync(printer.BackendUrl, 1, null, null, 600, It.Is<PrinterCredential?>(v => v != null && v.Username == "test-user"), ct)).ReturnsAsync(true);
        temperature.Setup(c => c.SetTemperaturesAsync(printer.BackendUrl, 200, 60, It.Is<PrinterCredential?>(v => v != null && v.ApiKey == "test-key"), ct)).ReturnsAsync(true);
        emergency.Setup(c => c.EmergencyStopAsync(printer.BackendUrl, It.Is<PrinterCredential?>(v => v != null && v.ApiKey == "test-key"), ct)).ReturnsAsync(true);
        motors.Setup(c => c.DisableMotorsAsync(printer.BackendUrl, It.Is<PrinterCredential?>(v => v != null && v.ApiKey == "test-key"), ct)).ReturnsAsync(true);
        extrusion.Setup(c => c.ExtrudeAsync(printer.BackendUrl, -5, 300, It.Is<PrinterCredential?>(v => v != null && v.ApiKey == "test-key"), ct)).ReturnsAsync(true);
        mmu.Setup(c => c.ExecuteMmuAsync(printer.BackendUrl, mmuRequest, It.Is<PrinterCredential?>(v => v != null && v.ApiKey == "test-key"), ct)).ReturnsAsync(true);
        calibration.Setup(c => c.SaveZOffsetAsync(printer.BackendUrl, -0.25m, It.Is<PrinterCredential?>(v => v != null && v.ApiKey == "test-key"), ct)).ReturnsAsync(true);
        PrintersService service = CreateService(db, printer, client.Object);

        (await service.SendHomeAsync(printer.Id, ct)).Should().BeTrue();
        (await service.HomeXYAsync(printer.Id, ct)).Should().BeTrue();
        (await service.MoveAsync(printer.Id, 1, null, null, 600, ct)).Should().Be(PrinterControlOutcome.Ok);
        (await service.SetTempsAsync(printer.Id, 200, 60, ct)).Should().Be(PrinterControlOutcome.Ok);
        (await service.EmergencyStopAsync(printer.Id, ct)).Should().BeTrue();
        (await service.DisableMotorsAsync(printer.Id, ct)).Should().BeTrue();
        (await service.ExtrudeFilamentAsync(printer.Id, -5, 300, ct)).Should().BeTrue();
        (await service.ExecuteMmuAsync(printer.Id, mmuRequest, ct)).Should().BeTrue();
        (await service.SaveZOffsetToFirmwareAsync(printer.Id, -0.25m, ct)).Should().BeTrue();

        movement.VerifyAll();
        temperature.VerifyAll();
        emergency.VerifyAll();
        motors.VerifyAll();
        extrusion.VerifyAll();
        mmu.VerifyAll();
        calibration.VerifyAll();
        rawCommands.VerifyNoOtherCalls();
        printer.FrontendUrl.Should().Be("http://printer.local");
    }

    [Fact]
    public async Task MaintenanceControls_RawGcodeOnlyPlugin_DoesNotInventCommands()
    {
        await using var db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.Moonraker);
        var client = new Mock<IBackendClient>();
        Mock<ISupportsGcodeExecution> rawCommands = client.As<ISupportsGcodeExecution>();
        PrintersService service = CreateService(db, printer, client.Object);

        (await service.DisableMotorsAsync(printer.Id, CancellationToken.None)).Should().BeFalse();
        (await service.ExtrudeFilamentAsync(printer.Id, 5, 300, CancellationToken.None)).Should().BeFalse();
        (await service.EmergencyStopAsync(printer.Id, CancellationToken.None)).Should().BeFalse();
        (await service.ExecuteMmuAsync(printer.Id, new MmuControlRequest(MmuControlAction.Load), CancellationToken.None)).Should().BeFalse();
        (await service.SaveZOffsetToFirmwareAsync(printer.Id, -0.25m, CancellationToken.None)).Should().BeFalse();

        rawCommands.VerifyNoOtherCalls();
    }

    private static AppDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Printer CreatePrinter(PrinterBackend backend) => new()
    {
        Id = Guid.NewGuid(),
        Name = "plugin-controls",
        ServerUrl = "http://printer.local",
        BackendPort = 4408,
        FrontendPort = 80,
        Backend = (int)backend,
        ApiKey = "test-key",
        Username = "test-user",
        Password = "test-password",
        Credential = PrinterCredential.FromAll("test-key", "test-user", "test-password"),
    };

    private static PrintersService CreateService(AppDbContext db, Printer printer, IBackendClient client)
    {
        var printers = new Mock<IPrintersRepository>();
        printers.Setup(repository => repository.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>())).ReturnsAsync(printer);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(unit => unit.Printers).Returns(printers.Object);
        var factory = new Mock<IBackendClientFactory>();
        factory.Setup(value => value.GetClient((PrinterBackend)printer.Backend)).Returns(client);
        var protector = new Mock<ISensitiveDataProtector>();
        protector.Setup(value => value.Unprotect(It.IsAny<string>())).Returns((string value) => value);

        return new PrintersService(
            unitOfWork.Object, db, factory.Object, Mock.Of<IBackendCapabilityFactory>(),
            Mock.Of<Farm.Infrastructure.Services.Catalog.ICatalogService>(),
            Mock.Of<IHttpClientFactory>(), NullLogger<PrintersService>.Instance,
            Mock.Of<IPrinterStatusBroadcaster>(), Mock.Of<IMultiPrinterStatusCoordinator>(),
            Mock.Of<IPrinterStatusClientFactory>(), Mock.Of<IPrinterStatusCacheReader>(),
            Mock.Of<Farm.Infrastructure.Services.Locations.ILocationService>(), protector.Object,
            Mock.Of<Farm.Infrastructure.Services.Interfaces.ISpoolmanService>(),
            Mock.Of<Farm.Infrastructure.Services.Cameras.IGo2RtcService>(),
            Mock.Of<Farm.Infrastructure.Services.StorageManagement.IStoragePathService>(),
            Mock.Of<Farm.Infrastructure.Services.Spoolman.IFilamentCoverageSpoolResolver>());
    }
}
