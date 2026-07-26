using System.Security.Claims;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Contracts.Libraries;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Metrics;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Slicer.Module.Tests.Slicing;

public sealed class SliceJobControllerRetryTests
{
    [Fact]
    public async Task RetryAsync_LostCompareAndSwap_ReturnsConflictWithoutQueuedEvent()
    {
        Guid userId = Guid.NewGuid();
        var job = new SliceJob
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Status = SliceJobStatus.Failed,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-1),
        };
        Mock<ISliceJobRepository> repository = new();
        _ = repository.Setup(instance => instance.GetByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        _ = repository.Setup(instance => instance.TryRetryJobAsync(
                job.Id,
                userId,
                job.Status,
                job.UpdatedAt,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((SliceJob?)null);
        Mock<ISliceJobEventService> events = new();
        SliceJobController controller = CreateController(userId, repository.Object, events.Object);

        IActionResult result = await controller.RetryAsync(job.Id, CancellationToken.None);

        _ = result.Should().BeOfType<ConflictObjectResult>();
        events.Verify(
            instance => instance.NotifyJobQueuedAsync(
                It.IsAny<SliceJob>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static SliceJobController CreateController(
        Guid userId,
        ISliceJobRepository repository,
        ISliceJobEventService events) =>
        new(
            repository,
            events,
            NullLogger<SliceJobController>.Instance,
            new Mock<IArtifactsService>().Object,
            new Mock<IRateLimitService>().Object,
            new SliceJobMetrics(),
            new Mock<IWorkerAuthService>().Object,
            new Mock<IWorkerRepository>().Object,
            new Mock<ISlicerRegistry>().Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                            "Test")),
                },
            },
        };
}
