using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Slicer.Module.Api.Controllers.Calibration;
using Farm.Slicer.Module.Api.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests;

public class WorkerCompatibilityControllerTests
{
    [Fact]
    public async Task GetWorkerCompatibilityAsync_ReturnsServiceSnapshot()
    {
        Guid workerId = Guid.NewGuid();
        WorkerCompatibilitySnapshotDto snapshot = new(
            new WorkerCompatibilityPinnedIdentityDto("2.3.1", "upstream", "sha256:digest", "sha256:binary", workerId),
            ["2.3.1"],
            true);
        Mock<ISlicerHostWorkerCompatibilityService> mockService = new();
        _ = mockService
            .Setup(service => service.GetWorkerCompatibilityAsync("2.3.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);

        WorkerCompatibilityController controller = new(mockService.Object);

        IActionResult result = await controller.GetWorkerCompatibilityAsync("2.3.1", CancellationToken.None);

        OkObjectResult ok = result.Should().BeOfType<OkObjectResult>().Subject;
        _ = ok.Value.Should().BeSameAs(snapshot);
        mockService.Verify(
            service => service.GetWorkerCompatibilityAsync("2.3.1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetWorkerCompatibilityAsync_BlankRequiredVersion_PassesNull(string? requiredVersion)
    {
        Mock<ISlicerHostWorkerCompatibilityService> mockService = new();
        _ = mockService
            .Setup(service => service.GetWorkerCompatibilityAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(WorkerCompatibilitySnapshotDto.Empty);

        WorkerCompatibilityController controller = new(mockService.Object);

        IActionResult result = await controller.GetWorkerCompatibilityAsync(requiredVersion, CancellationToken.None);

        _ = result.Should().BeOfType<OkObjectResult>();
        mockService.Verify(
            service => service.GetWorkerCompatibilityAsync(null, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
