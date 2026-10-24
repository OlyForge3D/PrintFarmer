using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
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

public sealed class PrintersControllerRuntimeFactorTests
{
    private readonly Guid _id = Guid.NewGuid();
    private readonly Mock<IPrintersService> _printers = new();
    private readonly Mock<IPrinterBackendCapabilitiesService> _capabilities = new();
    private readonly Mock<IQueueResourceAuthorizationService> _authorization = new();
    private readonly Mock<IPrinterPhysicalActuationService> _actuation = new();

    private PrintersController CreateController(string? state = "idle", bool online = true, bool supported = true, bool exists = true)
    {
        PrinterStatusDto status = new(_id, online, state, SpeedFactorPercent: 125, FlowFactorPercent: 90);
        _printers.Setup(s => s.GetStatusDtoAsync(_id, It.IsAny<CancellationToken>())).ReturnsAsync(status);
        _printers.Setup(s => s.FindByIdAsync(_id, It.IsAny<CancellationToken>())).ReturnsAsync(new Printer { Id = _id });
        _printers.Setup(s => s.SetSpeedFactorAsync(_id, 123.45, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _printers.Setup(s => s.SetFlowFactorAsync(_id, 123.45, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _capabilities.Setup(s => s.GetByPrinterIdAsync(_id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exists ? new PrinterBackendCapabilitiesDto(_id, "fixture", PrinterBackend.Moonraker)
            {
                SupportsSpeedFactorControl = supported,
                SupportsFlowFactorControl = supported,
            } : null);
        var cache = new Mock<IPrinterStatusCacheReader>();
        cache.Setup(c => c.GetStatus(_id)).Returns(status);
        return PrintersControllerControlGuardsTests.CreateController(
            _printers, cache, out _, actuation: _actuation, resourceAuthorization: _authorization, capabilitiesService: _capabilities);
    }

    private Task<ActionResult<CommandResult>> SetAsync(PrintersController controller, bool speed, double? percent = 123.45) =>
        speed
            ? controller.SetSpeedFactorAsync(_id, new RuntimeFactorRequest { Percent = percent }, default)
            : controller.SetFlowFactorAsync(_id, new RuntimeFactorRequest { Percent = percent }, default);

    private void VerifyNoDispatch()
    {
        _printers.Verify(s => s.SetSpeedFactorAsync(It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
        _printers.Verify(s => s.SetFlowFactorAsync(It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true, "idle", false)]
    [InlineData(false, "ready", false)]
    [InlineData(true, "standby", false)]
    [InlineData(false, "complete", false)]
    [InlineData(true, "completed", false)]
    [InlineData(false, "cancelled", false)]
    [InlineData(true, "printing", true)]
    [InlineData(false, "printing", true)]
    [InlineData(true, "paused", true)]
    [InlineData(false, "paused", true)]
    public async Task SetRuntimeFactorAsync_KnownOnlineState_AcknowledgesOnlyRequestedFactor(bool speed, string state, bool active)
    {
        PrintersController controller = CreateController(state);
        ActionResult<CommandResult> result = await SetAsync(controller, speed);
        Assert.True(Assert.IsType<CommandResult>(result.Result is ObjectResult body ? body.Value : result.Value).Success);
        _printers.Verify(s => s.SetSpeedFactorAsync(_id, 123.45, It.IsAny<CancellationToken>()), speed ? Times.Once() : Times.Never());
        _printers.Verify(s => s.SetFlowFactorAsync(_id, 123.45, It.IsAny<CancellationToken>()), speed ? Times.Never() : Times.Once());
        string operation = speed ? "set_speed_factor" : "set_flow_factor";
        _actuation.Verify(s => s.AcquireActiveAsync(_id, It.IsAny<string>(), operation, It.IsAny<CancellationToken>()), active ? Times.Once() : Times.Never());
        _actuation.Verify(s => s.AcquireDirectAsync(_id, It.IsAny<string>(), operation, It.IsAny<CancellationToken>()), active ? Times.Never() : Times.Once());
        _actuation.Verify(s => s.CompleteDirectAsync(It.IsAny<PrinterActuationLease>(), true, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(200.01)]
    [InlineData(0.99)]
    [InlineData(100.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task SetRuntimeFactorAsync_InvalidPercent_RejectsBothRoutesBeforeDispatch(double? percent)
    {
        PrintersController controller = CreateController();
        foreach (bool speed in new[] { true, false })
        {
            Assert.Equal(400, Assert.IsType<ObjectResult>((await SetAsync(controller, speed, percent)).Result).StatusCode);
        }

        VerifyNoDispatch();
        _capabilities.Verify(s => s.GetByPrinterIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("idle", false, true, true, 503)]
    [InlineData("error", true, true, true, 409)]
    [InlineData("shutdown", true, true, true, 409)]
    [InlineData("starting", true, true, true, 409)]
    [InlineData(null, true, true, true, 409)]
    [InlineData("idle", true, false, true, 501)]
    [InlineData("idle", true, true, false, 404)]
    public async Task SetRuntimeFactorAsync_UnavailableOrUnsupported_RejectsBothRoutes(
        string? state, bool online, bool supported, bool exists, int expected)
    {
        PrintersController controller = CreateController(state, online, supported, exists);
        foreach (bool speed in new[] { true, false })
        {
            ActionResult<CommandResult> result = await SetAsync(controller, speed);
            int? code = result.Result is NotFoundResult ? 404 : Assert.IsAssignableFrom<ObjectResult>(result.Result).StatusCode;
            Assert.Equal(expected, code);
        }

        VerifyNoDispatch();
        _actuation.Verify(s => s.AcquireDirectAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _actuation.Verify(s => s.AcquireActiveAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetRuntimeFactorAsync_SubmitDenied_HidesPrinterAndDispatchesNothing()
    {
        PrintersController controller = CreateController();
        _authorization.Setup(s => s.CanAccessPrinterAsync(It.IsAny<ClaimsPrincipal>(), _id,
            PrinterGroupAccessLevel.Submit, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        Assert.IsType<NotFoundResult>((await SetAsync(controller, true)).Result);
        Assert.IsType<NotFoundResult>((await SetAsync(controller, false)).Result);
        VerifyNoDispatch();
        _capabilities.Verify(s => s.GetByPrinterIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task SetRuntimeFactorAsync_DisabledOrMaintenance_DispatchesNothing(bool enabled, bool maintenance)
    {
        PrintersController controller = CreateController();
        _printers.Setup(s => s.FindByIdAsync(_id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Printer { Id = _id, IsEnabled = enabled, InMaintenance = maintenance });
        Assert.IsType<ConflictObjectResult>((await SetAsync(controller, true)).Result);
        Assert.IsType<ConflictObjectResult>((await SetAsync(controller, false)).Result);
        VerifyNoDispatch();
    }

    [Theory]
    [InlineData(true, "idle", false)]
    [InlineData(false, "printing", false)]
    [InlineData(true, "paused", true)]
    [InlineData(false, "idle", true)]
    public async Task SetRuntimeFactorAsync_BackendUnconfirmedOrThrows_ReportsUnknownWithoutRetry(bool speed, string state, bool throws)
    {
        PrintersController controller = CreateController(state);
        if (speed)
        {
            var setup = _printers.Setup(s => s.SetSpeedFactorAsync(_id, 123.45, It.IsAny<CancellationToken>()));
            if (throws)
            {
                setup.ThrowsAsync(new HttpRequestException("fixture"));
            }
            else
            {
                setup.ReturnsAsync(false);
            }
        }
        else
        {
            var setup = _printers.Setup(s => s.SetFlowFactorAsync(_id, 123.45, It.IsAny<CancellationToken>()));
            if (throws)
            {
                setup.ThrowsAsync(new HttpRequestException("fixture"));
            }
            else
            {
                setup.ReturnsAsync(false);
            }
        }

        ObjectResult result = Assert.IsType<ObjectResult>((await SetAsync(controller, speed)).Result);
        Assert.Equal(503, result.StatusCode);
        Assert.False(Assert.IsType<CommandResult>(result.Value).Success);
        _actuation.Verify(s => s.MarkDirectUnknownAsync(It.IsAny<PrinterActuationLease>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _actuation.Verify(s => s.CompleteDirectAsync(It.IsAny<PrinterActuationLease>(), true, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true, "idle")]
    [InlineData(false, "idle")]
    [InlineData(true, "printing")]
    [InlineData(false, "printing")]
    public async Task SetRuntimeFactorAsync_CallerCancelledAfterSend_PropagatesCancellationAndMarksUnknownOnce(bool speed, string state)
    {
        PrintersController controller = CreateController(state);
        using var cancellation = new CancellationTokenSource();
        async Task<bool> CancelAsync(Guid id, double percent, CancellationToken token)
        {
            cancellation.Cancel();
            return await Task.FromCanceled<bool>(token);
        }

        if (speed)
        {
            _printers.Setup(service => service.SetSpeedFactorAsync(_id, 123.45, It.IsAny<CancellationToken>()))
                .Returns(CancelAsync);
        }
        else
        {
            _printers.Setup(service => service.SetFlowFactorAsync(_id, 123.45, It.IsAny<CancellationToken>()))
                .Returns(CancelAsync);
        }

        var request = new RuntimeFactorRequest { Percent = 123.45 };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => speed
            ? controller.SetSpeedFactorAsync(_id, request, cancellation.Token)
            : controller.SetFlowFactorAsync(_id, request, cancellation.Token));

        _actuation.Verify(service => service.MarkDirectUnknownAsync(
            It.IsAny<PrinterActuationLease>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _actuation.Verify(service => service.CompleteDirectAsync(
            It.IsAny<PrinterActuationLease>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _printers.Verify(service => service.SetSpeedFactorAsync(
            _id, 123.45, It.IsAny<CancellationToken>()), speed ? Times.Once() : Times.Never());
        _printers.Verify(service => service.SetFlowFactorAsync(
            _id, 123.45, It.IsAny<CancellationToken>()), speed ? Times.Never() : Times.Once());
    }
}
