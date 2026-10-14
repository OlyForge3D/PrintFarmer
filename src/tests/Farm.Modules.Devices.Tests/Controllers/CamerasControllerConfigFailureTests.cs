using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.Startup;
using Farm.Modules.Devices.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Camera = Farm.Infrastructure.Domain.Camera;

namespace Farm.Modules.Devices.Tests.Controllers;

/// <summary>
/// The camera config read must not mask cancellation or unexpected faults as startup or
/// endpoint-specific errors; those propagate to the global exception middleware.
/// </summary>
public class CamerasControllerConfigFailureTests
{
    private readonly Mock<ICameraService> _cameras = new();
    private readonly Mock<IStartupStatus> _startup = new();

    public CamerasControllerConfigFailureTests()
    {
        _startup.SetupGet(s => s.IsReady).Returns(true);
    }

    [Fact]
    public async Task GetCameraConfig_NotReady_Returns503WithoutReadingCamera()
    {
        _startup.SetupGet(s => s.IsReady).Returns(false);

        ActionResult<Farm.Infrastructure.CameraConfigDto> result = await CreateController().GetCameraConfigAsync(Guid.NewGuid(), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(503);
        _cameras.Verify(s => s.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetCameraConfig_Cancelled_PropagatesCancellation()
    {
        CancellationToken cancelled = new(canceled: true);
        _cameras.Setup(s => s.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, CancellationToken>((_, ct) => Task.FromCanceled<Camera?>(ct));

        Func<Task> act = () => CreateController().GetCameraConfigAsync(Guid.NewGuid(), cancelled);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetCameraConfig_UnexpectedInvalidOperation_IsNotMaskedAsInitializing()
    {
        _cameras.Setup(s => s.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("unexpected failure"));

        Func<Task> act = () => CreateController().GetCameraConfigAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("unexpected failure");
    }

    private CamerasController CreateController() => new(
        _cameras.Object,
        Mock.Of<IPrinterCameraEndpointDetectionService>(),
        _startup.Object,
        NullLogger<CamerasController>.Instance,
        Mock.Of<IHttpClientFactory>(),
        Mock.Of<IEgressGuard>());
}
