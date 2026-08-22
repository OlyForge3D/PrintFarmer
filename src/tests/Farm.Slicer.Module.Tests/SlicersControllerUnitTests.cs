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
        IEnumerable<SlicerServiceResponse>? list = ok!.Value as IEnumerable<SlicerServiceResponse>;
        _ = list.Should().NotBeNull();
        _ = list!.Should().ContainSingle();
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
        _ = mockService.Setup(s => s.DeregisterAsync(id, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        SlicersController controller = new SlicersController(mockService.Object, new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry>().Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

        IActionResult res = await controller.DeregisterAsync(id);
        _ = res.Should().BeOfType<NoContentResult>();

        mockService.Verify(s => s.DeregisterAsync(id, false, It.IsAny<CancellationToken>()), Times.Once);
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

    [Fact(DisplayName = "GET /api/slicers/engines drops a never-configured sibling version while keeping the configured/online one as latest (issue #1772)")]
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

        // Only the older version has ANY worker (Online) — the newer 2.4.1 has
        // never had a worker configured at all (issue #1772), so it must be
        // dropped entirely rather than surfaced as a disabled option.
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
        // 2.4.1 has never had a configured worker → dropped entirely (issue #1772).
        _ = json.Should().NotContain("2.4.1");
        // 2.3.1 is configured (has a service row) and online → available=true.
        _ = json.Should().Contain("\"version\":\"2.3.1\",\"available\":true");
        // latest = the only remaining (configured, online) version.
        _ = json.Should().Contain("\"latest\":\"2.3.1\"");
    }

    [Fact(DisplayName = "GET /api/slicers/engines emits latest=<first online entry in registry order> when multiple configured versions are online (issue #578)")]
    public async Task ListEngines_MultipleConfiguredOnlineVersions_ReturnsFirstOnlineInRegistryOrderAsLatest()
    {
        // Both versions are configured AND online; `latest` selection is
        // `versionEntries.FirstOrDefault(v => v.available)`, i.e. registry
        // group order — NOT a numeric "newest version" comparison. This test
        // exercises that actual selection behavior directly, which the
        // mixed configured/unconfigured test above no longer covers (Hicks
        // finding on #1792: that test lost its ability to prove "latest"
        // selection once one candidate stopped competing).
        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary> orca242 =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary>();
        _ = orca242.SetupGet(l => l.SlicerName).Returns("OrcaSlicer");
        _ = orca242.SetupGet(l => l.SlicerVersion).Returns("2.4.2");
        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary> orca231b =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary>();
        _ = orca231b.SetupGet(l => l.SlicerName).Returns("OrcaSlicer");
        _ = orca231b.SetupGet(l => l.SlicerVersion).Returns("2.3.1");

        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry> registry2 =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry>();
        _ = registry2.Setup(r => r.ListAllLibraries())
            .Returns(new[] { orca242.Object, orca231b.Object });

        Farm.Slicer.Module.Domain.SlicerService svc242 = new()
        {
            Id = Guid.NewGuid(),
            Name = "orca-2.4.2",
            SlicerType = (int)Farm.Slicer.Module.Domain.SlicerType.OrcaSlicer,
            Version = "2.4.2",
            Status = "Online",
            Host = "http://worker-2-4-2:5000",
        };
        Farm.Slicer.Module.Domain.SlicerService svc231 = new()
        {
            Id = Guid.NewGuid(),
            Name = "orca-2.3.1",
            SlicerType = (int)Farm.Slicer.Module.Domain.SlicerType.OrcaSlicer,
            Version = "2.3.1",
            Status = "Online",
            Host = "http://worker-2-3-1b:5000",
        };
        Mock<ISlicersService> service2 = new Mock<ISlicersService>();
        _ = service2.Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Farm.Slicer.Module.Domain.SlicerService>)new[] { svc242, svc231 });

        SlicersController controller2 = new SlicersController(service2.Object, registry2.Object);
        controller2.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

        IActionResult result2 = await controller2.ListEnginesAsync();
        string json2 = System.Text.Json.JsonSerializer.Serialize((result2 as OkObjectResult)!.Value);

        // Both versions are configured and online → both remain, both available.
        _ = json2.Should().Contain("\"version\":\"2.4.2\",\"available\":true");
        _ = json2.Should().Contain("\"version\":\"2.3.1\",\"available\":true");
        // `latest` resolves to whichever entry comes first in registry/group
        // order (2.4.2, per the registry mock order above) — the endpoint
        // does not perform semantic version comparison.
        _ = json2.Should().Contain("\"latest\":\"2.4.2\"");
    }

    [Fact(DisplayName = "GET /api/slicers/engines drops registry versions with no configured worker in any status (issue #1772)")]
    public async Task ListEngines_VersionWithNoConfiguredWorker_IsExcludedEntirely()
    {
        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary> orca242 =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary>();
        _ = orca242.SetupGet(l => l.SlicerName).Returns("OrcaSlicer");
        _ = orca242.SetupGet(l => l.SlicerVersion).Returns("2.4.2");

        // 2.3.1 is installed in the plugin registry but has never had ANY
        // worker (Online or Offline) configured for it — the exact scenario
        // from issue #1772 (stale plugin left installed after every worker
        // moved to 2.4.2).
        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary> orca231 =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary>();
        _ = orca231.SetupGet(l => l.SlicerName).Returns("OrcaSlicer");
        _ = orca231.SetupGet(l => l.SlicerVersion).Returns("2.3.1");

        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry> registry =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry>();
        _ = registry.Setup(r => r.ListAllLibraries())
            .Returns(new[] { orca242.Object, orca231.Object });

        Farm.Slicer.Module.Domain.SlicerService svc = new()
        {
            Id = Guid.NewGuid(),
            Name = "orca-2.4.2",
            SlicerType = (int)Farm.Slicer.Module.Domain.SlicerType.OrcaSlicer,
            Version = "2.4.2",
            Status = "Online",
            Host = "http://worker-2-4-2:5000",
        };
        Mock<ISlicersService> service = new Mock<ISlicersService>();
        _ = service.Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Farm.Slicer.Module.Domain.SlicerService>)new[] { svc });

        SlicersController controller = new SlicersController(service.Object, registry.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

        IActionResult result = await controller.ListEnginesAsync();
        string json = System.Text.Json.JsonSerializer.Serialize((result as OkObjectResult)!.Value);

        // 2.3.1 never had a worker configured → absent from the payload entirely.
        _ = json.Should().NotContain("2.3.1");
        // 2.4.2 is configured and online → remains, available, and is latest.
        _ = json.Should().Contain("\"version\":\"2.4.2\",\"available\":true");
        _ = json.Should().Contain("\"latest\":\"2.4.2\"");
    }

    [Fact(DisplayName = "GET /api/slicers/engines drops a version whose only worker row is stale beyond the configured-freshness window (issue #1812)")]
    public async Task ListEngines_VersionWithOnlyOrphanedStaleWorker_IsExcludedEntirely()
    {
        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary> orca242 =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary>();
        _ = orca242.SetupGet(l => l.SlicerName).Returns("OrcaSlicer");
        _ = orca242.SetupGet(l => l.SlicerVersion).Returns("2.4.2");

        // 2.3.1 HAD a worker registered at some point, but that worker was
        // removed from the deployment (container deleted / feature flag
        // turned off) long ago and never deregistered — the exact repro from
        // issue #1812. Its row's LastSeen is far older than
        // WorkerStatus.ConfiguredFreshnessSeconds (7 days), so it must no
        // longer count as "configured" even though a row still exists.
        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary> orca231 =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary>();
        _ = orca231.SetupGet(l => l.SlicerName).Returns("OrcaSlicer");
        _ = orca231.SetupGet(l => l.SlicerVersion).Returns("2.3.1");

        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry> registry =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry>();
        _ = registry.Setup(r => r.ListAllLibraries())
            .Returns(new[] { orca242.Object, orca231.Object });

        Farm.Slicer.Module.Domain.SlicerService freshSvc = new()
        {
            Id = Guid.NewGuid(),
            Name = "orca-2.4.2",
            SlicerType = (int)Farm.Slicer.Module.Domain.SlicerType.OrcaSlicer,
            Version = "2.4.2",
            Status = "Online",
            Host = "http://worker-2-4-2:5000",
            LastSeen = DateTime.UtcNow,
        };
        Farm.Slicer.Module.Domain.SlicerService orphanedSvc = new()
        {
            Id = Guid.NewGuid(),
            Name = "orca-2.3.1-removed",
            SlicerType = (int)Farm.Slicer.Module.Domain.SlicerType.OrcaSlicer,
            Version = "2.3.1",
            Status = "Offline",
            Host = "http://worker-2-3-1:5000",
            LastSeen = DateTime.UtcNow.AddSeconds(-(Farm.Slicer.Module.Domain.WorkerStatus.ConfiguredFreshnessSeconds + 60)),
        };
        Mock<ISlicersService> service = new Mock<ISlicersService>();
        _ = service.Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Farm.Slicer.Module.Domain.SlicerService>)new[] { freshSvc, orphanedSvc });

        SlicersController controller = new SlicersController(service.Object, registry.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

        IActionResult result = await controller.ListEnginesAsync();
        string json = System.Text.Json.JsonSerializer.Serialize((result as OkObjectResult)!.Value);

        // 2.3.1's only row is stale beyond the configured-freshness window →
        // treated as orphaned/reaped and absent from the payload entirely,
        // even though a SlicerService row for it still physically exists.
        _ = json.Should().NotContain("2.3.1");
        // 2.4.2 is fresh and online → remains, available, and is latest.
        _ = json.Should().Contain("\"version\":\"2.4.2\",\"available\":true");
        _ = json.Should().Contain("\"latest\":\"2.4.2\"");
    }

    [Fact(DisplayName = "GET /api/slicers/engines counts a recently-seen Offline row as configured (freshness gate is independent of Online status, issue #1812)")]
    public async Task ListEngines_RecentOfflineRow_StillCountsAsConfigured()
    {
        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary> orca24 =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary>();
        _ = orca24.SetupGet(l => l.SlicerName).Returns("OrcaSlicer");
        _ = orca24.SetupGet(l => l.SlicerVersion).Returns("2.4.2");

        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry> registry =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry>();
        _ = registry.Setup(r => r.ListAllLibraries())
            .Returns(new[] { orca24.Object });

        // Offline, but LastSeen is recent (1 hour ago) — well within the 7-day
        // configured-freshness window. Must still count as "configured" (and
        // therefore stay listed as an unavailable option) — the freshness
        // gate added for #1812 must not be conflated with the separate
        // 60-second Online/available freshness gate.
        Farm.Slicer.Module.Domain.SlicerService svc = new()
        {
            Id = Guid.NewGuid(),
            Name = "orca-2.4.2-recently-offline",
            SlicerType = (int)Farm.Slicer.Module.Domain.SlicerType.OrcaSlicer,
            Version = "2.4.2",
            Status = "Offline",
            Host = "http://worker:5000",
            LastSeen = DateTime.UtcNow.AddHours(-1),
        };
        Mock<ISlicersService> service = new Mock<ISlicersService>();
        _ = service.Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Farm.Slicer.Module.Domain.SlicerService>)new[] { svc });

        SlicersController controller = new SlicersController(service.Object, registry.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

        IActionResult result = await controller.ListEnginesAsync();
        string json = System.Text.Json.JsonSerializer.Serialize((result as OkObjectResult)!.Value);

        // Configured (recent row) but offline right now → stays listed, unavailable.
        _ = json.Should().Contain("\"version\":\"2.4.2\",\"available\":false");
        _ = json.Should().Contain("\"latest\":null");
    }

    [Fact(DisplayName = "GET /api/slicers/engines with zero SlicerService rows keeps every registry version available and latest null (legacy/fresh-install fallback, issue #1812)")]
    public async Task ListEngines_NoServiceRowsAtAll_KeepsAllVersionsAvailableWithNullLatest()
    {
        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary> orca242 =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary>();
        _ = orca242.SetupGet(l => l.SlicerName).Returns("OrcaSlicer");
        _ = orca242.SetupGet(l => l.SlicerVersion).Returns("2.4.2");

        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry> registry =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry>();
        _ = registry.Setup(r => r.ListAllLibraries())
            .Returns(new[] { orca242.Object });

        // No SlicerService rows registered at all (fresh install / legacy
        // single-worker deployment). This must remain unaffected by the new
        // #1812 freshness gate: there is nothing to filter against, so every
        // registry version stays available and `latest` stays null so jobs
        // remain unpinned for a generic-capability worker.
        Mock<ISlicersService> service = new Mock<ISlicersService>();
        _ = service.Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Farm.Slicer.Module.Domain.SlicerService>)Array.Empty<Farm.Slicer.Module.Domain.SlicerService>());

        SlicersController controller = new SlicersController(service.Object, registry.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

        IActionResult result = await controller.ListEnginesAsync();
        string json = System.Text.Json.JsonSerializer.Serialize((result as OkObjectResult)!.Value);

        _ = json.Should().Contain("\"version\":\"2.4.2\",\"available\":true");
        _ = json.Should().Contain("\"latest\":null");
    }

    [Fact(DisplayName = "GET /api/slicers/engines keeps a configured-but-offline version listed and disabled (issue #1772)")]
    public async Task ListEngines_ConfiguredVersionThatIsOffline_StaysListedButUnavailable()
    {
        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary> orca24 =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary>();
        _ = orca24.SetupGet(l => l.SlicerName).Returns("OrcaSlicer");
        _ = orca24.SetupGet(l => l.SlicerVersion).Returns("2.4.2");

        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry> registry =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry>();
        _ = registry.Setup(r => r.ListAllLibraries())
            .Returns(new[] { orca24.Object });

        // A worker WAS configured for 2.4.2 but is currently offline — distinct
        // from "never configured", so this version must remain listed even
        // though it is not available right now.
        Farm.Slicer.Module.Domain.SlicerService svc = new()
        {
            Id = Guid.NewGuid(),
            Name = "orca-2.4.2-offline",
            SlicerType = (int)Farm.Slicer.Module.Domain.SlicerType.OrcaSlicer,
            Version = "2.4.2",
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

        // Configured (has a row) but offline → stays listed, unavailable.
        _ = json.Should().Contain("\"version\":\"2.4.2\",\"available\":false");
        _ = json.Should().Contain("\"latest\":null");
    }

    [Fact(DisplayName = "GET /api/slicers/engines keeps the full version list for an engine with ZERO configured workers (Bishop/Hicks #1792 regression guard)")]
    public async Task ListEngines_EngineWithNoConfiguredVersionsAtAll_KeepsFullListInsteadOfEmptyingIt()
    {
        // OrcaSlicer has two registry versions but NO service row of any
        // status matches OrcaSlicer at all — nobody has ever configured a
        // single OrcaSlicer worker. PrusaSlicer has a service row instead, so
        // `anyServiceRows` is globally true, which is what would trigger the
        // per-version drop filter. The React submit guards
        // (NewSliceJobPage.tsx / QuickSliceModal.tsx) detect "no worker for
        // this engine" via `versions.length > 0 && !latest && !anyAvailable`,
        // so OrcaSlicer's versionEntries must NOT collapse to an empty array
        // here — that would silently defeat the guard and let a job dispatch
        // unpinned to an engine with zero workers (the regression Bishop and
        // Hicks flagged on the initial version of this PR).
        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary> orca242 =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary>();
        _ = orca242.SetupGet(l => l.SlicerName).Returns("OrcaSlicer");
        _ = orca242.SetupGet(l => l.SlicerVersion).Returns("2.4.2");

        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary> orca231 =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerLibrary>();
        _ = orca231.SetupGet(l => l.SlicerName).Returns("OrcaSlicer");
        _ = orca231.SetupGet(l => l.SlicerVersion).Returns("2.3.1");

        Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry> registry =
            new Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry>();
        _ = registry.Setup(r => r.ListAllLibraries())
            .Returns(new[] { orca242.Object, orca231.Object });

        Farm.Slicer.Module.Domain.SlicerService prusaSvc = new()
        {
            Id = Guid.NewGuid(),
            Name = "prusa-worker",
            SlicerType = (int)Farm.Slicer.Module.Domain.SlicerType.PrusaSlicer,
            Version = "2.9.0",
            Status = "Online",
            Host = "http://prusa-worker:5000",
        };
        Mock<ISlicersService> service = new Mock<ISlicersService>();
        _ = service.Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Farm.Slicer.Module.Domain.SlicerService>)new[] { prusaSvc });

        SlicersController controller = new SlicersController(service.Object, registry.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

        IActionResult result = await controller.ListEnginesAsync();
        string json = System.Text.Json.JsonSerializer.Serialize((result as OkObjectResult)!.Value);

        // OrcaSlicer has zero configured workers of any kind → both versions
        // remain listed (unavailable), NOT dropped to an empty array, so the
        // frontend's "no worker available" submit guard can still fire.
        _ = json.Should().Contain("\"engine\":\"OrcaSlicer\"");
        _ = json.Should().Contain("\"version\":\"2.4.2\",\"available\":false");
        _ = json.Should().Contain("\"version\":\"2.3.1\",\"available\":false");
    }

    [Fact(DisplayName = "GET /api/slicers/engines with all workers offline marks unavailable and returns null latest (issue #578, Hicks H#1)")]
    public async Task ListEngines_AllOffline_ReturnsUnavailableWithNullLatest()
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

        // Row exists but worker is offline → NOT available.
        _ = json.Should().Contain("\"available\":false");
        // No versions are available AND service rows exist → latest is null
        // (Hicks H#1: pinning to any offline version would hang unclaimable).
        // Frontend must render dropdown from versionEntries but block submission.
        _ = json.Should().Contain("\"latest\":null");
    }
}

