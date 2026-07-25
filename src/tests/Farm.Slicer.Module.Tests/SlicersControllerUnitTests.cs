using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Slicer.Module.Api.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests;

public class SlicersControllerUnitTests
{
    [Fact]
    public async Task RegisterAsync_CreatesService_And_Broadcasts()
    {
        Guid newId = Guid.NewGuid();
        string apiKey = "generated-key";
        Mock<ISlicersService> mockService = new Mock<ISlicersService>();
        _ = mockService.Setup(s => s.RegisterAsync(It.IsAny<RegisterSlicerDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((newId, apiKey));

        SlicersController controller = new SlicersController(mockService.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

        RegisterSlicerDto dto = new RegisterSlicerDto
        {
            Name = "unit-orca",
            SlicerType = 1,
            Version = "0.1",
            Host = "http://local",
            MaxConcurrentJobs = 2,
            Tags = "t"
        };

        IActionResult result = await controller.RegisterAsync(dto);

        _ = result.Should().BeOfType<CreatedResult>();

        mockService.Verify(s => s.RegisterAsync(
            It.Is<RegisterSlicerDto>(d => d.Name == "unit-orca"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListAsync_ReturnsSeededServices()
    {
        Mock<ISlicersService> mockService = new Mock<ISlicersService>();
        _ = mockService.Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SlicerService> { new SlicerService { Id = Guid.NewGuid(), Name = "s1" } });

        SlicersController controller = new SlicersController(mockService.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

        IActionResult res = await controller.ListAsync();
        _ = res.Should().BeOfType<OkObjectResult>();
        OkObjectResult? ok = res as OkObjectResult;
        IReadOnlyList<SlicerService>? list = ok!.Value as IReadOnlyList<SlicerService>;
        _ = list.Should().NotBeNull();
        _ = list!.Count.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task HeartbeatAsync_UpdatesAndBroadcasts()
    {
        Guid id = Guid.NewGuid();
        Mock<ISlicersService> mockService = new Mock<ISlicersService>();
        _ = mockService.Setup(s => s.HeartbeatAsync(id, It.IsAny<HeartbeatDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        SlicersController controller = new SlicersController(mockService.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

        HeartbeatDto hb = new HeartbeatDto { Status = "Updated", FreeSlots = 3 };
        IActionResult res = await controller.HeartbeatAsync(id, hb);

        _ = res.Should().BeOfType<NoContentResult>();

        mockService.Verify(s => s.HeartbeatAsync(
            id,
            It.Is<HeartbeatDto>(h => h.Status == "Updated"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeregisterAsync_RemovesAndBroadcasts()
    {
        Guid id = Guid.NewGuid();
        Mock<ISlicersService> mockService = new Mock<ISlicersService>();
        _ = mockService.Setup(s => s.DeregisterAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        SlicersController controller = new SlicersController(mockService.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

        IActionResult res = await controller.DeregisterAsync(id);
        _ = res.Should().BeOfType<NoContentResult>();

        mockService.Verify(s => s.DeregisterAsync(id, It.IsAny<CancellationToken>()), Times.Once);
    }
}
