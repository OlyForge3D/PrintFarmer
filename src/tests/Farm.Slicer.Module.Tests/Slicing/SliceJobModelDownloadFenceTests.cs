using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Contracts.Libraries;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Slicer.Module.Tests.Slicing;

public class SliceJobModelDownloadFenceTests
{
    [Fact]
    public async Task DownloadWorkerModelAsync_ClaimChangesWhileOpeningFile_RejectsAndDisposesStream()
    {
        Guid workerId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        Guid claimToken = Guid.NewGuid();
        var worker = new Worker { Id = workerId };
        var job = new SliceJob
        {
            Id = jobId,
            WorkerId = workerId,
            ClaimToken = claimToken,
            Status = SliceJobStatus.Processing,
            LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
            ModelFileUrl = "file:///model.stl",
            ModelFileName = "model.stl",
        };
        var modelStream = new MemoryStream([1, 2, 3]);

        Mock<ISliceJobRepository> jobs = new();
        jobs.SetupSequence(repository => repository.GetByActiveWorkerLeaseAsync(
                jobId,
                workerId,
                claimToken,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(job)
            .ReturnsAsync((SliceJob?)null);
        _ = jobs.Setup(repository => repository.GetByIdAsync(
                jobId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        Mock<IWorkerAuthService> workerAuth = new();
        _ = workerAuth.Setup(service => service.AuthenticateAsync(It.IsAny<HttpContext>()))
            .ReturnsAsync(worker);
        Mock<ISlicerFileStorage> storage = new();
        _ = storage.Setup(service => service.DownloadFileAsync(
                job.ModelFileUrl,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(modelStream);

        SliceJobController controller = new(
            jobs.Object,
            new Mock<ISliceJobEventService>().Object,
            NullLogger<SliceJobController>.Instance,
            new Mock<IArtifactsService>().Object,
            new Mock<IRateLimitService>().Object,
            new SliceJobMetrics(),
            workerAuth.Object,
            new Mock<IWorkerRepository>().Object,
            new Mock<ISlicerRegistry>().Object,
            storage.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

        IActionResult result = await controller.DownloadWorkerModelAsync(
            jobId,
            claimToken,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, ((IStatusCodeActionResult)result).StatusCode);
        Assert.False(modelStream.CanRead);
        jobs.Verify(repository => repository.GetByActiveWorkerLeaseAsync(
            jobId,
            workerId,
            claimToken,
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
