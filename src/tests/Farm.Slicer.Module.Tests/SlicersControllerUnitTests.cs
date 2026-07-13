using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Slicer.Module.Api.Controllers;
using Farm.Slicer.Module.Contracts;
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

        SlicersController controller = new SlicersController(mockService.Object, new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry>().Object);
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

        SlicersController controller = new SlicersController(mockService.Object, new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry>().Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

        IActionResult res = await controller.ListAsync();
        _ = res.Should().BeOfType<OkObjectResult>();
        OkObjectResult? ok = res as OkObjectResult;
        IReadOnlyList<SlicerServiceResponseDto>? list = ok!.Value as IReadOnlyList<SlicerServiceResponseDto>;
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

        SlicersController controller = new SlicersController(mockService.Object, new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry>().Object);
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

        SlicersController controller = new SlicersController(mockService.Object, new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry>().Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

        IActionResult res = await controller.DeregisterAsync(id);
        _ = res.Should().BeOfType<NoContentResult>();

        mockService.Verify(s => s.DeregisterAsync(id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact(DisplayName = "GET /api/slicers/engines returns registered engines grouped by name (issue #578)")]
    public async Task ListEngines_GroupsRegisteredLibrariesByEngineName()
    {
        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary> orca24 =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary>();
        _ = orca24.SetupGet(l => l.SlicerName).Returns("OrcaSlicer");
        _ = orca24.SetupGet(l => l.SlicerVersion).Returns("2.4.1");

        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary> orca23 =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary>();
        _ = orca23.SetupGet(l => l.SlicerName).Returns("OrcaSlicer");
        _ = orca23.SetupGet(l => l.SlicerVersion).Returns("2.3.1");

        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry> registry =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry>();
        _ = registry.Setup(r => r.ListAllLibraries())
            .Returns(new[] { orca24.Object, orca23.Object });

        Mock<ISlicersService> service = new Mock<ISlicersService>();
        _ = service.Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Farm.Slicer.Module.Domain.SlicerService>)Array.Empty<Farm.Slicer.Module.Domain.SlicerService>());

        SlicersController controller = new SlicersController(service.Object, registry.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

        IActionResult result = await controller.ListEnginesAsync();

        OkObjectResult? ok = result as OkObjectResult;
        _ = ok.Should().NotBeNull();

        // Result is an anonymous list — serialize/deserialize round-trip to
        // assert the JSON contract the React client depends on.
        string json = System.Text.Json.JsonSerializer.Serialize(ok!.Value);
        _ = json.Should().Contain("\"engine\":\"OrcaSlicer\"");
        _ = json.Should().Contain("\"2.4.1\"").And.Contain("\"2.3.1\"");
        // Fresh install / legacy fallback: no SlicerService rows exist, so
        // `latest` MUST be null. The frontend uses null as the signal to leave
        // slice jobs UNPINNED, which is what allows a legacy single-worker
        // deployment (advertising only the generic "orcaslicer" capability,
        // no version suffix) to keep claiming jobs. See Vasquez R3 finding.
        _ = json.Should().Contain("\"latest\":null");
        // Availability fallback: without any service rows we still mark every
        // installed version available so the selector isn't visually broken.
        _ = json.Should().Contain("\"available\":true");
    }

    [Fact(DisplayName = "GET /api/slicers/engines emits latest=<newest online> when workers registered (issue #578)")]
    public async Task ListEngines_WithOnlineWorkers_ReturnsNewestOnlineAsLatest()
    {
        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary> orca24 =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary>();
        _ = orca24.SetupGet(l => l.SlicerName).Returns("OrcaSlicer");
        _ = orca24.SetupGet(l => l.SlicerVersion).Returns("2.4.1");
        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary> orca23 =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary>();
        _ = orca23.SetupGet(l => l.SlicerName).Returns("OrcaSlicer");
        _ = orca23.SetupGet(l => l.SlicerVersion).Returns("2.3.1");

        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry> registry =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry>();
        _ = registry.Setup(r => r.ListAllLibraries())
            .Returns(new[] { orca24.Object, orca23.Object });

        // Only the older version has an online worker.
        Farm.Slicer.Module.Domain.SlicerService svc = new()
        {
            Id = Guid.NewGuid(),
            Name = "orca-2.3.1",
            SlicerType = (int)Farm.Slicer.Module.Domain.SlicerType.OrcaSlicer,
            Version = "2.3.1",
            Status = "Online",
            Host = "http://worker-2-3-1:5000",
        };
        Mock<ISlicersService> service = new Mock<ISlicersService>();
        _ = service.Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Farm.Slicer.Module.Domain.SlicerService>)new[] { svc });

        SlicersController controller = new SlicersController(service.Object, registry.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

        IActionResult result = await controller.ListEnginesAsync();
        OkObjectResult? ok = result as OkObjectResult;
        _ = ok.Should().NotBeNull();

        string json = System.Text.Json.JsonSerializer.Serialize(ok!.Value);
        // 2.4.1 has no online worker → available=false, 2.3.1 → available=true.
        _ = json.Should().Contain("\"version\":\"2.4.1\",\"available\":false");
        _ = json.Should().Contain("\"version\":\"2.3.1\",\"available\":true");
        // latest = newest AVAILABLE, so 2.3.1 (not 2.4.1).
        _ = json.Should().Contain("\"latest\":\"2.3.1\"");
    }

    [Fact(DisplayName = "GET /api/slicers/engines with all workers offline marks unavailable but keeps latest for display (issue #578)")]
    public async Task ListEngines_AllOffline_ReturnsUnavailableWithFallbackLatest()
    {
        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary> orca24 =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary>();
        _ = orca24.SetupGet(l => l.SlicerName).Returns("OrcaSlicer");
        _ = orca24.SetupGet(l => l.SlicerVersion).Returns("2.4.1");

        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry> registry =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry>();
        _ = registry.Setup(r => r.ListAllLibraries())
            .Returns(new[] { orca24.Object });

        Farm.Slicer.Module.Domain.SlicerService svc = new()
        {
            Id = Guid.NewGuid(),
            Name = "orca-offline",
            SlicerType = (int)Farm.Slicer.Module.Domain.SlicerType.OrcaSlicer,
            Version = "2.4.1",
            Status = "Offline",
            Host = "http://worker:5000",
        };
        Mock<ISlicersService> service = new Mock<ISlicersService>();
        _ = service.Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Farm.Slicer.Module.Domain.SlicerService>)new[] { svc });

        SlicersController controller = new SlicersController(service.Object, registry.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

        IActionResult result = await controller.ListEnginesAsync();
        string json = System.Text.Json.JsonSerializer.Serialize((result as OkObjectResult)!.Value);

        // Row exists but worker is offline → NOT available (H3/V2 fix — the
        // pre-r2 bug marked everything available in this state).
        _ = json.Should().Contain("\"available\":false");
        // No versions are available, but rows exist, so fall back to newest
        // installed version so the UI still has a label to show. The version
        // dropdown will render every entry as "(offline)" and disabled.
        _ = json.Should().Contain("\"latest\":\"2.4.1\"");
    }
}

