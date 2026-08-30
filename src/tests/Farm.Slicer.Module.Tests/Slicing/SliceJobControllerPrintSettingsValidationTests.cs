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

/// <summary>
/// Covers issue #2229: <c>SliceJobController.SubmitAsync</c> must reject negative print-quality
/// override values (perimeters/wall_loops, infill density, top/bottom shell layers) with a 400
/// before a job is ever persisted, closing the gap where a caller could bypass the frontend's
/// #2223 inline validation by POSTing straight to the API. Zero must remain accepted for these
/// fields, matching OrcaSlicer's own <c>min: 0</c> settings metadata (Spiral vase mode requires
/// <c>top_shell_layers: 0</c>).
/// </summary>
public sealed class SliceJobControllerPrintSettingsValidationTests
{
    [Theory]
    [InlineData("wall_loops", -1)]
    [InlineData("sparse_infill_density", -10)]
    [InlineData("fill_density", -10)]
    [InlineData("top_shell_layers", -3)]
    [InlineData("bottom_shell_layers", -2)]
    public async Task SubmitAsync_NegativeOverrideValue_ReturnsInvalidRequestAndNeverPersists(string key, int value)
    {
        SliceJobController controller = CreateController(out Mock<ISliceJobRepository> repository, out Mock<ISliceJobEventService> events);
        var request = new SubmitSliceJobRequest
        {
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            Priority = 1,
            SlicerProfileJson = $"{{\"overrides\":{{\"{key}\":{value}}}}}",
        };

        IActionResult result = await controller.SubmitAsync(request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        _ = objectResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        _ = problem.Extensions["code"].Should().Be("invalid_print_settings");
        _ = problem.Detail.Should().Contain("cannot be negative");

        repository.Verify(instance => instance.AddAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()), Times.Never);
        events.Verify(
            instance => instance.NotifyJobQueuedAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SubmitAsync_NegativePercentEncodedInfillDensity_ReturnsInvalidRequest()
    {
        // Some profile-import/Advanced-mode paths encode sparse_infill_density as a percent
        // string (e.g. "-15%") rather than a raw number; the coercion must still catch it.
        SliceJobController controller = CreateController(out Mock<ISliceJobRepository> repository, out _);
        var request = new SubmitSliceJobRequest
        {
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            Priority = 1,
            SlicerProfileJson = """{"overrides":{"sparse_infill_density":"-15%"}}""",
        };

        IActionResult result = await controller.SubmitAsync(request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        _ = objectResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        _ = problem.Extensions["code"].Should().Be("invalid_print_settings");

        repository.Verify(instance => instance.AddAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("wall_loops")]
    [InlineData("sparse_infill_density")]
    [InlineData("top_shell_layers")]
    [InlineData("bottom_shell_layers")]
    public async Task SubmitAsync_ZeroOverrideValue_IsAcceptedAndCreatesJob(string key)
    {
        // OrcaSlicer's own vendored settings metadata declares min: 0 for these fields, and
        // Spiral vase mode legitimately requires top_shell_layers: 0 — zero must never be
        // flagged as invalid the way a negative value is (issue #2229 acceptance criterion).
        SliceJobController controller = CreateController(out Mock<ISliceJobRepository> repository, out Mock<ISliceJobEventService> events);
        SliceJob? added = null;
        _ = repository
            .Setup(instance => instance.AddAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()))
            .Callback<SliceJob, CancellationToken>((job, _) => added = job)
            .Returns(Task.CompletedTask);

        var request = new SubmitSliceJobRequest
        {
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            Priority = 1,
            SlicerProfileJson = $"{{\"overrides\":{{\"{key}\":0}}}}",
            Calibration = new CalibrationRequest { Method = "flow_rate_pass_1" },
        };

        IActionResult result = await controller.SubmitAsync(request, CancellationToken.None);

        _ = result.Should().BeOfType<CreatedResult>();
        _ = added.Should().NotBeNull();

        events.Verify(
            instance => instance.NotifyJobQueuedAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SubmitAsync_NoSlicerProfileJson_IsUnaffectedAndCreatesJob()
    {
        SliceJobController controller = CreateController(out Mock<ISliceJobRepository> repository, out Mock<ISliceJobEventService> events);
        _ = repository
            .Setup(instance => instance.AddAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var request = new SubmitSliceJobRequest
        {
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            Priority = 1,
            Calibration = new CalibrationRequest { Method = "flow_rate_pass_1" },
        };

        IActionResult result = await controller.SubmitAsync(request, CancellationToken.None);

        _ = result.Should().BeOfType<CreatedResult>();
        events.Verify(
            instance => instance.NotifyJobQueuedAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SubmitAsync_OverridesWithoutRestrictedKeys_IsUnaffectedAndCreatesJob()
    {
        // A negative value for an unrelated, unconstrained key must not be flagged — this
        // validator only judges the specific known non-negative fields.
        SliceJobController controller = CreateController(out Mock<ISliceJobRepository> repository, out Mock<ISliceJobEventService> events);
        _ = repository
            .Setup(instance => instance.AddAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var request = new SubmitSliceJobRequest
        {
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            Priority = 1,
            SlicerProfileJson = """{"overrides":{"z_offset":-0.2}}""",
            Calibration = new CalibrationRequest { Method = "flow_rate_pass_1" },
        };

        IActionResult result = await controller.SubmitAsync(request, CancellationToken.None);

        _ = result.Should().BeOfType<CreatedResult>();
        events.Verify(
            instance => instance.NotifyJobQueuedAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SubmitAsync_OverridesNotAnObject_ReturnsInvalidRequest()
    {
        // Every downstream consumer expects "overrides" to be a JSON object and calls
        // EnumerateObject() on it; a malformed shape must fail fast with a clear 400 instead of
        // reaching the worker as a late, generic failure.
        SliceJobController controller = CreateController(out Mock<ISliceJobRepository> repository, out _);
        var request = new SubmitSliceJobRequest
        {
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            Priority = 1,
            SlicerProfileJson = """{"overrides":[1,2,3]}""",
        };

        IActionResult result = await controller.SubmitAsync(request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        _ = objectResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        _ = problem.Extensions["code"].Should().Be("invalid_print_settings");

        repository.Verify(instance => instance.AddAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SubmitAsync_NegativeOverrideSurvivingExtruderFilamentEmbed_ReturnsInvalidRequest()
    {
        // Validation must run against the exact SlicerProfileJson persisted on the job (after
        // EmbedExtruderFilamentNames re-serializes it), not the pre-embed request body, otherwise
        // a duplicate top-level "overrides" key could smuggle a negative value past validation.
        SliceJobController controller = CreateController(out Mock<ISliceJobRepository> repository, out _);
        var request = new SubmitSliceJobRequest
        {
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            Priority = 1,
            SlicerProfileJson = """{"overrides":{"wall_loops":-4}}""",
            ExtruderFilamentProfileNames = ["PLA-Left", "PLA-Right"],
        };

        IActionResult result = await controller.SubmitAsync(request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        _ = objectResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        _ = problem.Extensions["code"].Should().Be("invalid_print_settings");

        repository.Verify(instance => instance.AddAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static SliceJobController CreateController(
        out Mock<ISliceJobRepository> repository,
        out Mock<ISliceJobEventService> events)
    {
        Guid userId = Guid.NewGuid();
        repository = new Mock<ISliceJobRepository>();
        _ = repository
            .Setup(instance => instance.AddAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        events = new Mock<ISliceJobEventService>();

        Mock<IRateLimitService> rateLimit = new();
        _ = rateLimit
            .Setup(instance => instance.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlicerRateLimitResult(true));

        return new SliceJobController(
            repository.Object,
            events.Object,
            NullLogger<SliceJobController>.Instance,
            new Mock<IArtifactsService>().Object,
            rateLimit.Object,
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
}
