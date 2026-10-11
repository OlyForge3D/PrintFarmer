using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Modules.Printers.Controllers;
using Farm.Modules.Printers.Controllers.Requests;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Modules.Printers.Tests.Controllers;

public sealed class PrintersControllerMmuHeatingTests
{
    private static PrinterSafetyValidationResult Cold(double minimum = 220) => new(
        false, 409, "printer_temperature_below_minimum", "Heat required.", minimum, 32.5);

    private static MmuGateActionRequest Unload() => new() { Protocol = "Qidibox", Action = "Unload", GateIndex = 1 };

    [Fact]
    public async Task MmuGateActionAsync_ColdNozzle_HeatsOnceWaitsForFilamentTemperatureThenUnloads()
    {
        var fixture = Create();
        int safetyReads = 0;
        var order = new List<string>();
        fixture.Guard.Setup(guard => guard.ValidateAsync(fixture.Id, PrinterSafetyOperation.FilamentUnload, null, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                order.Add("guard");
                return Task.FromResult(++safetyReads switch
                {
                    1 => Cold(),
                    2 => new PrinterSafetyValidationResult(true, RequiredHotendTemperatureC: 220, MeasuredHotendTemperatureC: 225),
                    _ => new PrinterSafetyValidationResult(true, RequiredHotendTemperatureC: 220, MeasuredHotendTemperatureC: 260),
                });
            });
        fixture.Printers.Setup(service => service.SetTempsAsync(fixture.Id, 260, null, It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("heat")).ReturnsAsync(PrinterControlOutcome.Ok);
        fixture.Printers.Setup(service => service.ExecuteMmuAsync(fixture.Id, It.IsAny<MmuControlRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("unload")).ReturnsAsync(true);

        Task<ActionResult<CommandResult>> pending = fixture.Controller.MmuGateActionAsync(fixture.Id, Unload(), default);
        Assert.False(pending.IsCompleted);
        fixture.Printers.Verify(service => service.ExecuteMmuAsync(It.IsAny<Guid>(), It.IsAny<MmuControlRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        ActionResult<CommandResult> result = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Value!.Success);
        Assert.Equal(new[] { "guard", "heat", "guard", "guard", "unload" }, order);
        fixture.Printers.Verify(service => service.GetMmuUnloadTemperatureAsync(fixture.Id,
            It.Is<MmuControlRequest>(request => request.GateIndex == 1 && request.Action == MmuControlAction.Unload), 220, It.IsAny<CancellationToken>()), Times.Once);
        fixture.Printers.Verify(service => service.SetTempsAsync(fixture.Id, 260, null, It.IsAny<CancellationToken>()), Times.Once);
        fixture.Printers.Verify(service => service.ExecuteMmuAsync(fixture.Id,
            It.Is<MmuControlRequest>(request => request.Protocol == MmuControlProtocol.Qidibox && request.GateIndex == 1 && request.Action == MmuControlAction.Unload),
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.Actuation.Verify(service => service.AcquireDirectAsync(fixture.Id, It.IsAny<string>(), "mmu_qidibox_unload", It.IsAny<CancellationToken>()), Times.Once);
        fixture.Actuation.Verify(service => service.CompleteDirectAsync(It.IsAny<PrinterActuationLease>(), true, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MmuGateActionAsync_AlreadyHot_DoesNotHeatOrResolveTemperature()
    {
        var fixture = Create();
        fixture.Guard.Setup(guard => guard.ValidateAsync(fixture.Id, PrinterSafetyOperation.FilamentUnload, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterSafetyValidationResult(true, MeasuredHotendTemperatureC: 240));

        Assert.True((await fixture.Controller.MmuGateActionAsync(fixture.Id, Unload(), default)).Value!.Success);

        fixture.Printers.Verify(service => service.SetTempsAsync(It.IsAny<Guid>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Printers.Verify(service => service.GetMmuUnloadTemperatureAsync(It.IsAny<Guid>(), It.IsAny<MmuControlRequest>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("printer_safety_evidence_unknown", 503)]
    [InlineData("printer_operation_unsupported", 422)]
    public async Task MmuGateActionAsync_NonTemperatureSafetyFailure_DoesNotHeatOrUnload(string code, int status)
    {
        var fixture = Create();
        fixture.Guard.Setup(guard => guard.ValidateAsync(fixture.Id, PrinterSafetyOperation.FilamentUnload, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrinterSafetyValidationResult.Reject(status, code, "Unsafe."));

        Assert.Equal(code, Problem(await fixture.Controller.MmuGateActionAsync(fixture.Id, Unload(), default), status).Extensions["code"]);
        VerifyNoUnload(fixture);
        fixture.Printers.Verify(service => service.SetTempsAsync(It.IsAny<Guid>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(PrinterControlOutcome.BackendBusy, false)]
    [InlineData(PrinterControlOutcome.BackendUnreachable, true)]
    public async Task MmuGateActionAsync_HeatingFails_DoesNotUnloadOrRetry(PrinterControlOutcome outcome, bool unknown)
    {
        var fixture = Create();
        fixture.Printers.Setup(service => service.SetTempsAsync(fixture.Id, 260, null, It.IsAny<CancellationToken>())).ReturnsAsync(outcome);

        Assert.Equal("printer_heating_failed", Problem(await fixture.Controller.MmuGateActionAsync(fixture.Id, Unload(), default), 503).Extensions["code"]);
        VerifyNoUnload(fixture);
        fixture.Printers.Verify(service => service.SetTempsAsync(fixture.Id, 260, null, It.IsAny<CancellationToken>()), Times.Once);
        fixture.Actuation.Verify(service => service.MarkDirectUnknownAsync(It.IsAny<PrinterActuationLease>(), "backend_heating_outcome_unknown", It.IsAny<CancellationToken>()), unknown ? Times.Once() : Times.Never());
        fixture.Actuation.Verify(service => service.CompleteDirectAsync(It.IsAny<PrinterActuationLease>(), false, It.IsAny<string>(), It.IsAny<CancellationToken>()), unknown ? Times.Never() : Times.Once());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MmuGateActionAsync_HeatingInterrupted_DoesNotUnloadAndReleasesKnownHeaterLease(bool timeout)
    {
        var fixture = Create();
        using var cancellation = new CancellationTokenSource();
        Task<ActionResult<CommandResult>> pending = fixture.Controller.MmuGateActionAsync(fixture.Id, Unload(), cancellation.Token);
        Assert.False(pending.IsCompleted);
        if (timeout)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(4));
            Assert.Equal("printer_heating_timeout", Problem(await pending.WaitAsync(TimeSpan.FromSeconds(10)), 504).Extensions["code"]);
        }
        else
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        VerifyNoUnload(fixture);
        fixture.Printers.Verify(service => service.SetTempsAsync(fixture.Id, 260, null, It.IsAny<CancellationToken>()), Times.Once);
        fixture.Actuation.Verify(service => service.CompleteDirectAsync(It.IsAny<PrinterActuationLease>(), false, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Actuation.Verify(service => service.MarkDirectUnknownAsync(It.IsAny<PrinterActuationLease>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MmuGateActionAsync_LeaseChangesBeforeOrDuringHeating_DoesNotUnload(int failAt)
    {
        var fixture = Create();
        int checks = 0;
        fixture.Actuation.Setup(service => service.RevalidateDirectAsync(It.IsAny<PrinterActuationLease>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterActuationLease lease, CancellationToken _) => ++checks == failAt
                ? new PrinterActuationResult(PrinterActuationResultCode.FenceConflict)
                : new PrinterActuationResult(PrinterActuationResultCode.Accepted, lease));

        Assert.Equal("printer_actuation_revalidation_failed", Problem(await fixture.Controller.MmuGateActionAsync(fixture.Id, Unload(), default), 409).Extensions["code"]);
        VerifyNoUnload(fixture);
        fixture.Printers.Verify(service => service.SetTempsAsync(fixture.Id, 260, null, It.IsAny<CancellationToken>()), failAt == 1 ? Times.Never() : Times.Once());
    }

    [Fact]
    public async Task MmuGateActionAsync_TelemetryBecomesStaleAfterHeating_DoesNotUnload()
    {
        var fixture = Create();
        fixture.Guard.SetupSequence(guard => guard.ValidateAsync(fixture.Id, PrinterSafetyOperation.FilamentUnload, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Cold()).ReturnsAsync(PrinterSafetyValidationResult.Reject(503, "printer_safety_evidence_unknown", "Stale temperature."));

        Assert.Equal("Stale temperature.", Problem(await fixture.Controller.MmuGateActionAsync(fixture.Id, Unload(), default), 503).Title);
        VerifyNoUnload(fixture);
    }

    [Fact]
    public async Task MmuGateActionAsync_FilamentTemperatureExceedsHardware_DoesNotHeatOrUnload()
    {
        var fixture = Create();
        fixture.Printers.Setup(service => service.GetMmuUnloadTemperatureAsync(fixture.Id, It.IsAny<MmuControlRequest>(), 220, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentOutOfRangeException("temperature"));

        Assert.Equal("printer_unload_temperature_invalid", Problem(await fixture.Controller.MmuGateActionAsync(fixture.Id, Unload(), default), 422).Extensions["code"]);
        VerifyNoUnload(fixture);
        fixture.Printers.Verify(service => service.SetTempsAsync(It.IsAny<Guid>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MmuGateActionAsync_LeaseChangesAfterHeating_DoesNotUnload()
    {
        var fixture = Create();
        fixture.Guard.SetupSequence(guard => guard.ValidateAsync(fixture.Id, PrinterSafetyOperation.FilamentUnload, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Cold()).ReturnsAsync(new PrinterSafetyValidationResult(true, RequiredHotendTemperatureC: 220, MeasuredHotendTemperatureC: 260));
        int checks = 0;
        fixture.Actuation.Setup(service => service.RevalidateDirectAsync(It.IsAny<PrinterActuationLease>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterActuationLease lease, CancellationToken _) => ++checks == 2
                ? new PrinterActuationResult(PrinterActuationResultCode.FenceConflict)
                : new PrinterActuationResult(PrinterActuationResultCode.Accepted, lease));

        ActionResult<CommandResult> result = await fixture.Controller.MmuGateActionAsync(fixture.Id, Unload(), default);

        Assert.Equal(409, Assert.IsType<ConflictObjectResult>(result.Result).StatusCode);
        VerifyNoUnload(fixture);
        fixture.Printers.Verify(service => service.SetTempsAsync(fixture.Id, 260, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MmuGateActionAsync_HeaterThrows_DoesNotUnloadOrRetryAndRetainsUnknownLease()
    {
        var fixture = Create();
        fixture.Printers.Setup(service => service.SetTempsAsync(fixture.Id, 260, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Lost heater response."));

        Assert.Equal("printer_heating_failed", Problem(await fixture.Controller.MmuGateActionAsync(fixture.Id, Unload(), default), 503).Extensions["code"]);
        VerifyNoUnload(fixture);
        fixture.Printers.Verify(service => service.SetTempsAsync(fixture.Id, 260, null, It.IsAny<CancellationToken>()), Times.Once);
        fixture.Actuation.Verify(service => service.MarkDirectUnknownAsync(It.IsAny<PrinterActuationLease>(), "backend_heating_outcome_unknown", It.IsAny<CancellationToken>()), Times.Once);
        fixture.Actuation.Verify(service => service.CompleteDirectAsync(It.IsAny<PrinterActuationLease>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MmuGateActionAsync_AfcUnload_UsesSameValidatedWarmup()
    {
        var fixture = Create();
        fixture.Guard.SetupSequence(guard => guard.ValidateAsync(fixture.Id, PrinterSafetyOperation.FilamentUnload, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Cold()).ReturnsAsync(new PrinterSafetyValidationResult(true, RequiredHotendTemperatureC: 220, MeasuredHotendTemperatureC: 260));

        ActionResult<CommandResult> result = await fixture.Controller.MmuGateActionAsync(fixture.Id,
            new MmuGateActionRequest { Protocol = "Afc", Action = "Unload", LaneName = "lane1" }, default);

        Assert.True(result.Value!.Success);
        fixture.Printers.Verify(service => service.SetTempsAsync(fixture.Id, 260, null, It.IsAny<CancellationToken>()), Times.Once);
        fixture.Printers.Verify(service => service.ExecuteMmuAsync(fixture.Id,
            It.Is<MmuControlRequest>(request => request.Protocol == MmuControlProtocol.Afc && request.LaneName == "lane1" && request.Action == MmuControlAction.Unload),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static ProblemDetails Problem(ActionResult<CommandResult> result, int status)
    {
        ObjectResult response = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(status, response.StatusCode);
        return Assert.IsType<ProblemDetails>(response.Value);
    }

    private static void VerifyNoUnload(Fixture fixture) => fixture.Printers.Verify(service => service.ExecuteMmuAsync(
        It.IsAny<Guid>(), It.IsAny<MmuControlRequest>(), It.IsAny<CancellationToken>()), Times.Never);

    private static Fixture Create()
    {
        Guid id = Guid.NewGuid();
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.FindByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(new Printer { Id = id, Name = "qidi", Backend = (int)PrinterBackend.Moonraker });
        printers.Setup(service => service.GetMmuUnloadTemperatureAsync(id, It.IsAny<MmuControlRequest>(), 220, It.IsAny<CancellationToken>())).ReturnsAsync(260);
        printers.Setup(service => service.SetTempsAsync(id, 260, null, It.IsAny<CancellationToken>())).ReturnsAsync(PrinterControlOutcome.Ok);
        printers.Setup(service => service.ExecuteMmuAsync(id, It.IsAny<MmuControlRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var cache = new Mock<IPrinterStatusCacheReader>();
        cache.Setup(service => service.GetStatus(id)).Returns(new PrinterStatusDto(id, true, "standby"));
        var guard = new Mock<IPrinterSafetyGuard>();
        guard.Setup(service => service.ValidateAsync(id, PrinterSafetyOperation.FilamentUnload, null, It.IsAny<CancellationToken>())).ReturnsAsync(Cold());
        var actuation = new Mock<IPrinterPhysicalActuationService>();
        var clock = new DirectControlTestClock();
        PrintersController controller = PrintersControllerControlGuardsTests.CreateController(printers, cache, out _, guard, actuation, timeProvider: clock);
        return new Fixture(id, printers, guard, actuation, clock, controller);
    }

    private sealed record Fixture(Guid Id, Mock<IPrintersService> Printers, Mock<IPrinterSafetyGuard> Guard,
        Mock<IPrinterPhysicalActuationService> Actuation, DirectControlTestClock Clock, PrintersController Controller);
}
