using System.Security.Claims;
using System.Text.Json;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Contracts.Libraries;
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
        ISliceJobEventService? events = null)
    {
        Mock<IRateLimitService> rateLimit = new Mock<IRateLimitService>();
        _ = rateLimit.Setup(r => r.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlicerRateLimitResult(true, null));

        SliceJobController controller = new SliceJobController(
            jobRepo,
            events ?? new Mock<ISliceJobEventService>().Object,
            NullLogger<SliceJobController>.Instance,
            new Mock<IArtifactsService>().Object,
            rateLimit.Object,
            new SliceJobMetrics(),
            new Mock<IWorkerAuthService>().Object,
            new Mock<IWorkerRepository>().Object,
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
}
