using Farm.Infrastructure;
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
    [Theory]
    [InlineData("Qidibox", true)]
    [InlineData("Qidibox", false)]
    [InlineData("Afc", true)]
    [InlineData("Afc", false)]
    public async Task MmuGateActionAsync_Unload_SendsNativeCommandWithoutHeatingOrPolling(string protocol, bool accepted)
    {
        Guid id = Guid.NewGuid();
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.FindByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Farm.Infrastructure.Domain.Printer { Id = id });
        printers.Setup(service => service.ExecuteMmuAsync(id, It.IsAny<MmuControlRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(accepted);
        var guard = new Mock<IPrinterSafetyGuard>(MockBehavior.Strict);
        var actuation = new Mock<IPrinterPhysicalActuationService>();
        var status = new Mock<IPrinterStatusCacheReader>(MockBehavior.Strict);
        status.Setup(cache => cache.GetStatus(id)).Returns((PrinterStatusDto?)null);
        PrintersController controller = PrintersControllerControlGuardsTests.CreateController(
            printers, status, out _, guard, actuation);
        var request = new MmuGateActionRequest
        {
            Protocol = protocol,
            Action = "Unload",
            GateIndex = protocol == "Qidibox" ? 1 : null,
            LaneName = protocol == "Afc" ? "lane1" : null,
        };

        ActionResult<CommandResult> response = await controller.MmuGateActionAsync(id, request, default);

        CommandResult result = accepted
            ? Assert.IsType<CommandResult>(response.Value)
            : Assert.IsType<CommandResult>(Assert.IsType<ObjectResult>(response.Result).Value);
        Assert.Equal(accepted, result.Success);
        if (!accepted)
        {
            Assert.Equal(503, Assert.IsType<ObjectResult>(response.Result).StatusCode);
            Assert.Contains("did not prove", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        printers.Verify(service => service.ExecuteMmuAsync(id,
            It.Is<MmuControlRequest>(command =>
                command.Action == MmuControlAction.Unload &&
                command.Protocol == (protocol == "Qidibox" ? MmuControlProtocol.Qidibox : MmuControlProtocol.Afc) &&
                command.GateIndex == request.GateIndex && command.LaneName == request.LaneName),
            It.IsAny<CancellationToken>()), Times.Once);
        printers.Verify(service => service.SetTempsAsync(
            It.IsAny<Guid>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Never);
        printers.Verify(service => service.GetMmuUnloadTemperatureAsync(
            It.IsAny<Guid>(), It.IsAny<MmuControlRequest>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
        printers.Verify(service => service.GetMovementStatusAsync(
            It.IsAny<Farm.Infrastructure.Domain.Printer>(), It.IsAny<CancellationToken>()), Times.Never);
        printers.Verify(service => service.GetStatusDtoAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        guard.VerifyNoOtherCalls();
        actuation.Verify(service => service.RevalidateDirectAsync(
            It.IsAny<PrinterActuationLease>(), It.IsAny<CancellationToken>()), Times.Once);
        actuation.Verify(service => service.MarkDirectUnknownAsync(
            It.IsAny<PrinterActuationLease>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), accepted ? Times.Never() : Times.Once());
    }

    [Fact]
    public async Task MmuGateActionAsync_LeaseChanged_DoesNotSendOrHeat()
    {
        Guid id = Guid.NewGuid();
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.FindByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Farm.Infrastructure.Domain.Printer { Id = id });
        var actuation = new Mock<IPrinterPhysicalActuationService>();
        PrintersController controller = PrintersControllerControlGuardsTests.CreateController(
            printers, new Mock<IPrinterStatusCacheReader>(), out _, actuation: actuation);
        actuation.Setup(service => service.RevalidateDirectAsync(It.IsAny<PrinterActuationLease>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterActuationResult(PrinterActuationResultCode.FenceConflict));

        ActionResult<CommandResult> response = await controller.MmuGateActionAsync(
            id, new MmuGateActionRequest { Protocol = "Qidibox", Action = "Unload", GateIndex = 1 }, default);

        Assert.IsType<ConflictObjectResult>(response.Result);
        printers.Verify(service => service.ExecuteMmuAsync(
            It.IsAny<Guid>(), It.IsAny<MmuControlRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        printers.Verify(service => service.SetTempsAsync(
            It.IsAny<Guid>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
