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
    [Fact]
    public async Task EmergencyStopAsync_PluginCancelled_PropagatesCancellationWithoutRetry()
    {
        await using var db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.Moonraker);
        using var cancellation = new CancellationTokenSource();
        var client = new Mock<IBackendClient>(MockBehavior.Strict);
        Mock<ISupportsEmergencyStop> emergency = client.As<ISupportsEmergencyStop>();
        emergency.Setup(value => value.EmergencyStopAsync(
                printer.BackendUrl, It.IsAny<PrinterCredential?>(), cancellation.Token))
            .Returns(async () =>
            {
                cancellation.Cancel();
                return await Task.FromCanceled<bool>(cancellation.Token);
            });
        PrintersService service = CreateService(db, printer, client.Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.EmergencyStopAsync(printer.Id, cancellation.Token));

        emergency.Verify(value => value.EmergencyStopAsync(
            printer.BackendUrl, It.Is<PrinterCredential?>(credential => credential != null && credential.ApiKey == "test-key"),
            cancellation.Token), Times.Once);
        emergency.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetMovementStatusAsync_ManualControl_ReadsOnlyOneMovementSnapshot()
    {
        await using var db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.Moonraker);
        using var cancellation = new CancellationTokenSource();
        var status = new Mock<IPrinterStatusClient>(MockBehavior.Strict);
        var observed = new PrinterStatusDto(printer.Id, true, "Idle", X: 10, Y: 20, Z: 0);
        status.Setup(value => value.GetMovementStatusAsync(printer, cancellation.Token)).ReturnsAsync(observed);
        var factory = new Mock<IPrinterStatusClientFactory>(MockBehavior.Strict);
        factory.Setup(value => value.GetStatusClient(printer.Backend)).Returns(status.Object);
        var client = new Mock<IBackendClient>(MockBehavior.Strict);
        PrintersService service = CreateService(db, printer, client.Object, factory.Object);

        PrinterStatusDto result = await service.GetMovementStatusAsync(printer, cancellation.Token);

        result.IsOnline.Should().BeTrue();
        result.X.Should().Be(10);
        result.Y.Should().Be(20);
        result.Z.Should().Be(0);
        factory.Verify(value => value.GetStatusClient(printer.Backend), Times.Once);
        status.Verify(value => value.GetMovementStatusAsync(printer, cancellation.Token), Times.Once);
        status.VerifyNoOtherCalls();
        factory.VerifyNoOtherCalls();
        client.VerifyNoOtherCalls();
    }

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
        (await service.SetSpeedFactorAsync(printer.Id, 125, CancellationToken.None)).Should().BeFalse();
        (await service.SetFlowFactorAsync(printer.Id, 90, CancellationToken.None)).Should().BeFalse();

        rawCommands.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RuntimeFactorAsync_SemanticCapability_ForwardsIndependentPercentAndCredentials()
    {
        await using var db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.Moonraker);
        var client = new Mock<IBackendClient>(MockBehavior.Strict);
        Mock<ISupportsRuntimeFactorControl> factors = client.As<ISupportsRuntimeFactorControl>();
        factors.Setup(c => c.SetSpeedFactorAsync(printer.BackendUrl, 123.45,
            It.Is<PrinterCredential?>(value => value != null && value.ApiKey == "test-key"), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        factors.Setup(c => c.SetFlowFactorAsync(printer.BackendUrl, 87.65,
            It.Is<PrinterCredential?>(value => value != null && value.ApiKey == "test-key"), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        PrintersService service = CreateService(db, printer, client.Object);

        Assert.True(await service.SetSpeedFactorAsync(printer.Id, 123.45, default));
        Assert.True(await service.SetFlowFactorAsync(printer.Id, 87.65, default));
        Assert.False(await service.SetSpeedFactorAsync(Guid.NewGuid(), 125, default));
        factors.VerifyAll();
        factors.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(201)]
    [InlineData(1.001)]
    public async Task RuntimeFactorAsync_InvalidPercent_RejectsBeforeBackendLookup(double percent)
    {
        await using var db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.Moonraker);
        var client = new Mock<IBackendClient>(MockBehavior.Strict);
        Mock<ISupportsRuntimeFactorControl> factors = client.As<ISupportsRuntimeFactorControl>();
        PrintersService service = CreateService(db, printer, client.Object);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetSpeedFactorAsync(printer.Id, percent, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetFlowFactorAsync(printer.Id, percent, default));
        factors.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RuntimeFactorAsync_BackendFailure_DoesNotReportSuccessOrRetry()
    {
        await using var db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.Moonraker);
        var client = new Mock<IBackendClient>(MockBehavior.Strict);
        Mock<ISupportsRuntimeFactorControl> factors = client.As<ISupportsRuntimeFactorControl>();
        factors.Setup(c => c.SetSpeedFactorAsync(It.IsAny<string>(), 125,
            It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("fixture"));
        PrintersService service = CreateService(db, printer, client.Object);

        Assert.False(await service.SetSpeedFactorAsync(printer.Id, 125, default));
        factors.Verify(c => c.SetSpeedFactorAsync(It.IsAny<string>(), 125,
            It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()), Times.Once);
        factors.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(260, 245d, 220d, 260d)]
    [InlineData(null, 245d, 220d, 245d)]
    [InlineData(200, 245d, 220d, 220d)]
    [InlineData(null, null, 220d, 220d)]
    [InlineData(null, 200d, 240d, 240d)]
    public async Task GetMmuUnloadTemperatureAsync_AssignedGate_UsesFilamentThenMaterialThenSafetyMinimum(
        int? spoolTemperature, double? materialTemperature, double minimum, double expected)
    {
        await using var db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.Moonraker);
        printer.Toolheads.Add(new Toolhead { Index = 0, IsPrimary = true, CurrentSpoolId = 99, CurrentMaterial = "PLA" });
        printer.Toolheads.Add(new Toolhead { Index = 2, ToolheadType = ToolheadType.MmuGate, CurrentSpoolId = 8, CurrentMaterial = "PETG" });
        db.FilamentTypes.Add(new FilamentType { Id = Guid.NewGuid(), Name = "PETG", DefaultHotendTemp = materialTemperature });
        await db.SaveChangesAsync();
        var spoolman = new Mock<Farm.Infrastructure.Services.Interfaces.ISpoolmanService>(MockBehavior.Strict);
        spoolman.Setup(service => service.GetSpoolByIdAsync(8, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanSpoolDto(8, "gate spool", "PETG", 500, null, true, FilamentId: 17));
        spoolman.Setup(service => service.GetFilamentByIdAsync(17, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanFilamentDto(17, "PETG", "PETG", null, null, null, null, null, null, null, spoolTemperature, null, null, null, null, null));
        var backend = new Mock<IBackendClient>(MockBehavior.Strict);
        PrintersService service = CreateService(db, printer, backend.Object, spoolmanService: spoolman.Object);

        Assert.Equal(expected, await service.GetMmuUnloadTemperatureAsync(printer.Id,
            new MmuControlRequest(MmuControlAction.Unload, MmuControlProtocol.Qidibox, GateIndex: 1), minimum, default));

        spoolman.VerifyAll();
        spoolman.VerifyNoOtherCalls();
        backend.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetMmuUnloadTemperatureAsync_NoGateBindingOrSpoolServiceUnavailable_UsesMaterialOrMinimum(bool unavailable)
    {
        await using var db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.Moonraker);
        printer.Toolheads.Add(new Toolhead { Index = 0, IsPrimary = true, CurrentSpoolId = 99, CurrentMaterial = "PLA" });
        if (unavailable)
        {
            printer.Toolheads.Add(new Toolhead { Index = 2, ToolheadType = ToolheadType.MmuGate, CurrentSpoolId = 8, CurrentMaterial = "PETG" });
        }

        db.FilamentTypes.Add(new FilamentType { Id = Guid.NewGuid(), Name = "PETG", DefaultHotendTemp = 245 });
        await db.SaveChangesAsync();
        var spoolman = new Mock<Farm.Infrastructure.Services.Interfaces.ISpoolmanService>(MockBehavior.Strict);
        if (unavailable)
        {
            spoolman.Setup(service => service.GetSpoolByIdAsync(8, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException());
        }

        var backend = new Mock<IBackendClient>(MockBehavior.Strict);
        PrintersService service = CreateService(db, printer, backend.Object, spoolmanService: spoolman.Object);

        Assert.Equal(unavailable ? 245 : 220, await service.GetMmuUnloadTemperatureAsync(printer.Id,
            new MmuControlRequest(MmuControlAction.Unload, MmuControlProtocol.Qidibox, GateIndex: 1), 220, default));
        spoolman.Verify(service => service.GetSpoolByIdAsync(99, It.IsAny<CancellationToken>()), Times.Never);
        backend.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetMmuUnloadTemperatureAsync_TargetAboveInstalledRating_RejectsWithoutBackendIo(bool modelOnly)
    {
        await using var db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.Moonraker);
        Guid hotendModelId = Guid.NewGuid();
        printer.Toolheads.Add(new Toolhead { Index = 0, IsPrimary = true, HotendMaxTemperature = modelOnly ? null : 250, HotendModelId = modelOnly ? hotendModelId : null });
        db.HotendModelDefinitions.Add(new HotendModelDefinition { Id = hotendModelId, Name = "rated hotend", MaxTemp = 250 });
        printer.Toolheads.Add(new Toolhead { Index = 2, ToolheadType = ToolheadType.MmuGate, CurrentMaterial = "ABS" });
        db.FilamentTypes.Add(new FilamentType { Id = Guid.NewGuid(), Name = "ABS", DefaultHotendTemp = 260 });
        await db.SaveChangesAsync();
        var backend = new Mock<IBackendClient>(MockBehavior.Strict);
        PrintersService service = CreateService(db, printer, backend.Object);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetMmuUnloadTemperatureAsync(printer.Id,
            new MmuControlRequest(MmuControlAction.Unload, MmuControlProtocol.Qidibox, GateIndex: 1), 220, default));
        backend.VerifyNoOtherCalls();
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

    private static PrintersService CreateService(
        AppDbContext db, Printer printer, IBackendClient client, IPrinterStatusClientFactory? statusClientFactory = null,
        Farm.Infrastructure.Services.Interfaces.ISpoolmanService? spoolmanService = null)
    {
        var printers = new Mock<IPrintersRepository>();
        printers.Setup(repository => repository.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>())).ReturnsAsync(printer);
        printers.Setup(repository => repository.FindByIdWithToolheadsAsync(printer.Id, It.IsAny<CancellationToken>())).ReturnsAsync(printer);
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
            statusClientFactory ?? Mock.Of<IPrinterStatusClientFactory>(), Mock.Of<IPrinterStatusCacheReader>(),
            Mock.Of<Farm.Infrastructure.Services.Locations.ILocationService>(), protector.Object,
            spoolmanService ?? Mock.Of<Farm.Infrastructure.Services.Interfaces.ISpoolmanService>(),
            Mock.Of<Farm.Infrastructure.Services.Cameras.IGo2RtcService>(),
            Mock.Of<Farm.Infrastructure.Services.StorageManagement.IStoragePathService>(),
            Mock.Of<Farm.Infrastructure.Services.Spoolman.IFilamentCoverageSpoolResolver>());
    }
}
