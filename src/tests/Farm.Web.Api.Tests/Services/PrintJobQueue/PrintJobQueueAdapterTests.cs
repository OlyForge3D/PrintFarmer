using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Web.Api.Services.PrintJobQueue;
using Farm.Web.Api.Services.Queue;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.PrintJobQueue;

public class PrintJobQueueAdapterTests
{
    [Fact]
    public async Task EnqueueAsync_DelegatesToJobQueueService_AndReturnsMappedDto()
    {
        var jobQueueMock = new Mock<IJobQueueService>();
        var loggerMock = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();

        var requestDto = new QueuePrintJobDto { GcodeFileId = Guid.NewGuid(), Priority = PrintJobPriority.Normal };
        var returned = new JobQueuePrintJobDto
        {
            Id = Guid.NewGuid(),
            GcodeFileId = requestDto.GcodeFileId,
            GcodeFileName = "file.gcode",
            AssignedPrinterId = null,
            AssignedPrinterName = "",
            Priority = (int)PrintJobPriority.Normal,
            QueuePosition = 1,
            RequiredNozzleDiameter = (decimal?)0.4m,
            RequiredMaterialType = "PLA",
            CreatedAt = DateTime.UtcNow
        };

        jobQueueMock.Setup(s => s.AddJobToQueueAsync(It.IsAny<QueuePrintJobDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(returned);

        var adapter = new PrintJobQueueAdapter(jobQueueMock.Object);

        var enqueueReq = new EnqueuePrintJobRequest(requestDto.GcodeFileId, null, "normal", 0.4, "PLA");

        var result = await adapter.EnqueueAsync(enqueueReq, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(returned.Id, result!.Id);
        Assert.Equal(returned.GcodeFileName, result.GcodeFileName);
        Assert.Equal((double?)0.4, result.RequiredNozzleDiameter);
        jobQueueMock.Verify(s => s.AddJobToQueueAsync(It.IsAny<QueuePrintJobDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
