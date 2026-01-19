using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Services.PrintJobQueue;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class PrintJobQueueControllerTests
{
    [Fact]
    public async Task EnqueueAsync_ReturnsCreated_WhenServiceReturnsDto()
    {
        var svcMock = new Mock<IPrintJobQueueService>();
        var loggerMock = new Mock<IUnifiedLoggingService>();

        var dto = new PrintJobDto(Guid.NewGuid(), Guid.NewGuid(), "file.gcode", null, null, "Queued", 1, 0.4, "PLA", DateTime.UtcNow);
        svcMock.Setup(s => s.EnqueueAsync(It.IsAny<EnqueuePrintJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dto);

        var controller = new PrintJobQueueController(svcMock.Object, loggerMock.Object);

        var req = new EnqueuePrintJobRequest(dto.GcodeFileId, null, "normal", 0.4, "PLA");
        ActionResult<PrintJobDto> result = await controller.EnqueueAsync(req, CancellationToken.None);

        CreatedAtActionResult createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        PrintJobDto returned = Assert.IsType<PrintJobDto>(createdResult.Value);
        Assert.Equal(dto.Id, returned.Id);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsOk_WithEnumerable()
    {
        var svcMock = new Mock<IPrintJobQueueService>();
        var loggerMock = new Mock<IUnifiedLoggingService>();

        var list = new List<PrintJobDto> { new PrintJobDto(Guid.NewGuid(), Guid.NewGuid(), "file.gcode", null, null, "Queued", 1, 0.4, "PLA", DateTime.UtcNow) };
        svcMock.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(list);

        var controller = new PrintJobQueueController(svcMock.Object, loggerMock.Object);
        ActionResult<IEnumerable<PrintJobDto>> result = await controller.GetAllAsync(CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        IEnumerable<PrintJobDto> returned = Assert.IsAssignableFrom<IEnumerable<PrintJobDto>>(ok.Value!);
        Assert.NotEmpty(returned);
    }
}
