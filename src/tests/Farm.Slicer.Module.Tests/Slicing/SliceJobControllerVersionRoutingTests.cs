using System.Security.Claims;
using System.Text.Json;
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
using Xunit;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// Unit tests for the dual-engine (issue #578) version routing behaviour of
/// <see cref="SliceJobController.SubmitAsync"/>. Focus is on the *server-derived*
/// capability contract and the version-pin validation gate.
/// </summary>
public class SliceJobControllerVersionRoutingTests
{
    private static SliceJobController BuildController(
        ISliceJobRepository jobRepo,
        ISlicerRegistry registry,
        ISliceJobEventService? events = null,
        IReadOnlyList<Worker>? workers = null)
    {
        Mock<IRateLimitService> rateLimit = new Mock<IRateLimitService>();
        _ = rateLimit.Setup(r => r.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlicerRateLimitResult(true, null));
        Mock<IWorkerRepository> workerRepo = new Mock<IWorkerRepository>();
        _ = workerRepo.Setup(r => r.GetAllAsync(It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((int limit, int offset) => (IReadOnlyList<Worker>)(workers ?? []).Skip(offset).Take(limit).ToArray());

        SliceJobController controller = new SliceJobController(
            jobRepo,
            events ?? new Mock<ISliceJobEventService>().Object,
            NullLogger<SliceJobController>.Instance,
            new Mock<IArtifactsService>().Object,
            rateLimit.Object,
            new SliceJobMetrics(),
            new Mock<IWorkerAuthService>().Object,
            workerRepo.Object,
            registry)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
                            "Test"))
                }
            }
        };
        return controller;
    }

    private static Mock<ISlicerLibrary> MockLibrary(string name, string version)
    {
        Mock<ISlicerLibrary> lib = new Mock<ISlicerLibrary>();
        _ = lib.SetupGet(l => l.SlicerName).Returns(name);
        _ = lib.SetupGet(l => l.SlicerVersion).Returns(version);
        return lib;
    }

    private static Mock<ISlicerRegistry> RegistryWith(params (string Name, string Version)[] libs)
    {
        Mock<ISlicerRegistry> reg = new Mock<ISlicerRegistry>();
        foreach ((string name, string version) in libs)
        {
            _ = reg.Setup(r => r.GetLibrary(name, version))
                .Returns(MockLibrary(name, version).Object);
        }
        _ = reg.Setup(r => r.GetLibraries(It.IsAny<string>()))
            .Returns((string n) => libs
                .Where(l => string.Equals(l.Name, n, StringComparison.OrdinalIgnoreCase))
                .Select(l => MockLibrary(l.Name, l.Version).Object)
                .ToArray());
        _ = reg.Setup(r => r.ListAllLibraries())
            .Returns(libs.Select(l => MockLibrary(l.Name, l.Version).Object).ToArray());
        return reg;
    }

    [Fact(DisplayName = "SubmitAsync rejects unknown version with 400 listing registered versions")]
    public async Task Submit_UnknownVersion_Returns400_WithRegisteredVersions()
    {
        Mock<ISlicerRegistry> registry = RegistryWith(("OrcaSlicer", "2.4.0"), ("OrcaSlicer", "2.3.1"));
        Mock<ISliceJobRepository> repo = new Mock<ISliceJobRepository>();
        SliceJobController controller = BuildController(repo.Object, registry.Object);

        SubmitSliceJobRequest req = new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = "http://example/m.stl",
            ModelFileName = "m.stl",
            SlicerEngine = 0,
            SlicerEngineVersion = "9.9.9",
        };

        IActionResult result = await controller.SubmitAsync(req, CancellationToken.None);

        BadRequestObjectResult? bad = result as BadRequestObjectResult;
        _ = bad.Should().NotBeNull();
        _ = bad!.Value!.ToString()!.Should().Contain("2.4.0").And.Contain("2.3.1").And.Contain("9.9.9");
        repo.Verify(r => r.AddAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact(DisplayName = "SubmitAsync pin persists version and derives ONLY versioned capability tag")]
    public async Task Submit_Pinned_PersistsVersion_AndServerDerivesCapability()
    {
        Mock<ISlicerRegistry> registry = RegistryWith(("OrcaSlicer", "2.4.0"), ("OrcaSlicer", "2.3.1"));
        SliceJob? captured = null;
        Mock<ISliceJobRepository> repo = new Mock<ISliceJobRepository>();
        _ = repo.Setup(r => r.AddAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()))
            .Callback<SliceJob, CancellationToken>((j, _) => captured = j)
            .Returns(Task.CompletedTask);

        SliceJobController controller = BuildController(repo.Object, registry.Object);

        SubmitSliceJobRequest req = new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = "http://example/m.stl",
            ModelFileName = "m.stl",
            SlicerEngine = 0,
            SlicerEngineVersion = "2.3.1",
            // Client tries to inject its own capabilities — must be ignored/overwritten.
            RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { "orcaslicer", "orcaslicer:2.4.0" }),
        };

        IActionResult result = await controller.SubmitAsync(req, CancellationToken.None);

        _ = result.Should().BeOfType<CreatedResult>();
        _ = captured.Should().NotBeNull();
        _ = captured!.SlicerEngineVersion.Should().Be("2.3.1");

        string[]? caps = JsonSerializer.Deserialize<string[]>(captured.RequiredCapabilitiesJson!);
        _ = caps.Should().BeEquivalentTo(new[] { "orcaslicer:2.3.1" });
    }

    [Fact(DisplayName = "SubmitAsync unpinned carries generic engine capability only")]
    public async Task Submit_Unpinned_CarriesGenericCapabilityOnly()
    {
        Mock<ISlicerRegistry> registry = RegistryWith(("OrcaSlicer", "2.4.0"));
        SliceJob? captured = null;
        Mock<ISliceJobRepository> repo = new Mock<ISliceJobRepository>();
        _ = repo.Setup(r => r.AddAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()))
            .Callback<SliceJob, CancellationToken>((j, _) => captured = j)
            .Returns(Task.CompletedTask);

        SliceJobController controller = BuildController(repo.Object, registry.Object);

        SubmitSliceJobRequest req = new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = "http://example/m.stl",
            ModelFileName = "m.stl",
            SlicerEngine = 0,
            SlicerEngineVersion = null,
        };

        IActionResult result = await controller.SubmitAsync(req, CancellationToken.None);

        _ = result.Should().BeOfType<CreatedResult>();
        _ = captured!.SlicerEngineVersion.Should().BeNull();
        string[]? caps = JsonSerializer.Deserialize<string[]>(captured.RequiredCapabilitiesJson!);
        _ = caps.Should().BeEquivalentTo(new[] { "orcaslicer" });
    }

    [Fact]
    public async Task Submit_ProjectBedSelection_RoutesOnlyToVersionedContractWorker()
    {
        Mock<ISlicerRegistry> registry = RegistryWith(("OrcaSlicer", "2.4.0"));
        SliceJob? captured = null;
        Mock<ISliceJobRepository> repo = new Mock<ISliceJobRepository>();
        _ = repo.Setup(r => r.AddAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()))
            .Callback<SliceJob, CancellationToken>((j, _) => captured = j)
            .Returns(Task.CompletedTask);
        SliceJobController controller = BuildController(repo.Object, registry.Object, workers:
        [
            new Worker
            {
                Status = WorkerStatus.Online,
                LastHeartbeat = DateTime.UtcNow,
                TotalSlots = 1,
                CapabilitiesJson = """["orcaslicer:2.4.0:project-bed-selection-v1"]"""
            }
        ]);
        SubmitSliceJobRequest request = new SubmitSliceJobRequest
        {
            ModelFileUrl = "http://example/m.3mf",
            ModelFileName = "m.3mf",
            SlicerEngineVersion = "2.4.0",
            SlicerProfileJson = """{"bedSelection":{"version":1,"explicitBedType":"High Temp Plate"}}""",
            RequiredCapabilitiesJson = """["orcaslicer:2.4.0"]""",
        };

        IActionResult result = await controller.SubmitAsync(request, CancellationToken.None);

        _ = result.Should().BeOfType<CreatedResult>();
        _ = JsonSerializer.Deserialize<string[]>(captured!.RequiredCapabilitiesJson!)
            .Should().Equal("orcaslicer:2.4.0:project-bed-selection-v1");
    }

    [Theory]
    [InlineData(WorkerStatus.Offline, false, 1, 0, """["orcaslicer:2.4.0:project-bed-selection-v1"]""")]
    [InlineData(WorkerStatus.Online, true, 1, 0, """["orcaslicer:2.4.0:project-bed-selection-v1"]""")]
    [InlineData(WorkerStatus.Online, false, 0, 0, """["orcaslicer:2.4.0:project-bed-selection-v1"]""")]
    [InlineData(WorkerStatus.Online, false, 1, 120, """["orcaslicer:2.4.0:project-bed-selection-v1"]""")]
    [InlineData(WorkerStatus.Online, false, 1, 0, """["orcaslicer:2.4.0"]""")]
    [InlineData(WorkerStatus.Online, false, 1, 0, "invalid")]
    public async Task Submit_ProjectBedSelectionWithoutEligibleWorker_Returns503(
        string status, bool disabled, int slots, int heartbeatAge, string capabilities)
    {
        Mock<ISliceJobRepository> repo = new Mock<ISliceJobRepository>();
        SliceJobController controller = BuildController(repo.Object, RegistryWith(("OrcaSlicer", "2.4.0")).Object, workers:
        [
            new Worker
            {
                Status = status,
                IsDisabled = disabled,
                TotalSlots = slots,
                LastHeartbeat = DateTime.UtcNow.AddSeconds(-heartbeatAge),
                CapabilitiesJson = capabilities
            }
        ]);

        IActionResult result = await controller.SubmitAsync(new SubmitSliceJobRequest
        {
            ModelFileUrl = "http://example/m.stl",
            ModelFileName = "m.stl",
            SlicerEngineVersion = "2.4.0",
            SlicerProfileJson = """{"bedSelection":{"version":1}}"""
        }, CancellationToken.None);

        _ = result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        repo.Verify(r => r.AddAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Submit_ProjectBedSelectionWithBusyWorkerOnSecondPage_Queues()
    {
        Worker[] workers = Enumerable.Range(0, 100).Select(_ => new Worker
        {
            Status = WorkerStatus.Online,
            TotalSlots = 1,
            LastHeartbeat = DateTime.UtcNow,
            CapabilitiesJson = "[]"
        }).Append(new Worker
        {
            Status = WorkerStatus.Busy,
            TotalSlots = 1,
            ActiveJobs = 1,
            LastHeartbeat = DateTime.UtcNow,
            CapabilitiesJson = """{"capabilities":["orcaslicer:2.4.0:project-bed-selection-v1"]}"""
        }).ToArray();
        Mock<ISliceJobRepository> repo = new Mock<ISliceJobRepository>();
        _ = repo.Setup(r => r.AddAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        SliceJobController controller = BuildController(repo.Object, RegistryWith(("OrcaSlicer", "2.4.0")).Object, workers: workers);

        IActionResult result = await controller.SubmitAsync(new SubmitSliceJobRequest
        {
            ModelFileUrl = "http://example/m.stl",
            ModelFileName = "m.stl",
            SlicerEngineVersion = "2.4.0",
            SlicerProfileJson = """{"bedSelection":{"version":1}}"""
        }, CancellationToken.None);

        _ = result.Should().BeOfType<CreatedResult>();
    }

    [Theory]
    [InlineData("""{"bedSelection":{"version":1},"bedSelection":{"version":1}}""")]
    [InlineData("""{"bedSelection":{"version":2}}""")]
    [InlineData("""{"bedSelection":{"version":1,"explicitBedType":"Unknown"}}""")]
    [InlineData("""{"bedSelection":{"version":1},"overrides":{"curr_bed_type":"Cool Plate"}}""")]
    [InlineData("""{"bedSelection":{"version":1},"overrides":{},"overrides":{}}""")]
    public async Task Submit_AmbiguousProjectBedSelection_RejectsBeforeEmbedding(string profile)
    {
        Mock<ISlicerRegistry> registry = RegistryWith(("OrcaSlicer", "2.4.0"));
        Mock<ISliceJobRepository> repo = new Mock<ISliceJobRepository>();
        SliceJobController controller = BuildController(repo.Object, registry.Object);
        SubmitSliceJobRequest request = new SubmitSliceJobRequest
        {
            SlicerEngineVersion = "2.4.0",
            SlicerProfileJson = profile,
            ExtruderFilamentProfileNames = ["PLA"],
        };

        IActionResult result = await controller.SubmitAsync(request, CancellationToken.None);

        _ = result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        repo.Verify(r => r.AddAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(0, " ")]
    [InlineData(1, "2.4.0")]
    public async Task Submit_ProjectBedSelectionWithoutOrcaVersion_Rejects(int engine, string? version)
    {
        Mock<ISlicerRegistry> registry = RegistryWith(("OrcaSlicer", "2.4.0"));
        Mock<ISliceJobRepository> repo = new Mock<ISliceJobRepository>();
        SliceJobController controller = BuildController(repo.Object, registry.Object);
        SubmitSliceJobRequest request = new SubmitSliceJobRequest
        {
            SlicerEngine = (Farm.Slicer.Module.Models.SlicerEngineType)engine,
            SlicerEngineVersion = version,
            SlicerProfileJson = """{"bedSelection":{"version":1}}""",
        };

        IActionResult result = await controller.SubmitAsync(request, CancellationToken.None);

        _ = result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        repo.Verify(r => r.AddAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
