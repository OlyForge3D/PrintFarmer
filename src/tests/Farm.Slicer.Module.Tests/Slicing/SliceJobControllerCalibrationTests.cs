using System.Security.Claims;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Contracts.Libraries;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Metrics;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// Covers the calibration-mode request path added by issue #1938: an unsupported method must
/// fail fast with a clear error, and an accepted method must produce an ordinary slice job that
/// never touches the printer/toolhead calibration saga fields
/// (<see cref="SliceJob.CalibrationProjectId"/>, <see cref="SliceJob.CalibrationAttemptId"/>,
/// <see cref="SliceJob.CalibrationOrchestrationId"/>) so send-to-printer keeps accepting it.
/// </summary>
public sealed class SliceJobControllerCalibrationTests
{
    [Fact]
    public async Task SubmitAsync_UnsupportedCalibrationMethod_ReturnsInvalidRequestWithSupportedMethodsListed()
    {
        SliceJobController controller = CreateController(out _, out _);
        var request = new SubmitSliceJobRequest
        {
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            Priority = 1,
            Calibration = new CalibrationRequest { Method = "pa_pattern" },
        };

        IActionResult result = await controller.SubmitAsync(request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        _ = objectResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        _ = problem.Extensions["code"].Should().Be("unsupported_calibration_method");
        _ = problem.Detail.Should().Contain("flow_rate_pass_1").And.Contain("temperature_tower");

        // Issue #2051 regression: the YOLO methods parse successfully (they are catalogued) but
        // are rejected separately as "not yet slicer-supported" — they must never appear in the
        // "Supported methods" list of an *unrelated* unsupported-method error, or a client would
        // be told to retry with a method that request would also reject.
        _ = problem.Detail.Should().NotContain("flow_rate_yolo_recommended").And.NotContain("flow_rate_yolo_perfectionist");
    }

    [Theory]
    [InlineData("flow_rate_yolo_recommended")]
    [InlineData("flow_rate_yolo_perfectionist")]
    public async Task SubmitAsync_CataloguedButNotYetSlicerSupportedMethod_ReturnsInvalidRequestBeforeQueueing(string wireName)
    {
        // Issue #2051: FlowRateYoloRecommended/FlowRateYoloPerfectionist parse successfully (their
        // wire names/resource metadata are catalogued), but the worker cannot yet apply their
        // delta-based per-object overrides. Rejecting here, at the API boundary, matters: without
        // it the job would be queued, claimed by a worker, and only fail late in
        // OrcaSlicingPipelineService.PrepareCalibrationModel — wasting a worker slot and a job
        // record for a request that could never have succeeded.
        SliceJobController controller = CreateController(out Mock<ISliceJobRepository> repository, out Mock<ISliceJobEventService> events);
        var request = new SubmitSliceJobRequest
        {
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            Priority = 1,
            Calibration = new CalibrationRequest { Method = wireName },
        };

        IActionResult result = await controller.SubmitAsync(request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        _ = objectResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        _ = problem.Extensions["code"].Should().Be("calibration_method_not_yet_supported");
        repository.Verify(
            instance => instance.AddAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()),
            Times.Never);
        events.Verify(
            instance => instance.NotifyJobQueuedAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SubmitAsync_KnownCalibrationMethod_CreatesOrdinarySliceJobWithNoCalibrationSagaFields()
    {
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
            Calibration = new CalibrationRequest
            {
                Method = "flow_rate_pass_1",
                Params = new Dictionary<string, double> { ["start_temperature"] = 220 },
            },
        };

        IActionResult result = await controller.SubmitAsync(request, CancellationToken.None);

        _ = result.Should().BeOfType<CreatedResult>();
        _ = added.Should().NotBeNull();
        _ = added!.CalibrationMethod.Should().Be("flow_rate_pass_1");
        _ = added.CalibrationParamsJson.Should().NotBeNullOrEmpty();

        // The defining constraint from issue #1938: this must remain an ordinary slice job so
        // SlicePrintBridgeController's IsCalibrationSlice(job) gate (which only inspects these
        // three fields) never trips and send-to-printer keeps accepting it.
        _ = added.CalibrationProjectId.Should().BeNull();
        _ = added.CalibrationAttemptId.Should().BeNull();
        _ = added.CalibrationOrchestrationId.Should().BeNull();

        _ = added.ModelFileUrl.Should().Be("calibration:flow_rate_pass_1");
        _ = added.ModelFileName.Should().Be(CalibrationMethods.DefaultModelFileName(CalibrationMethod.FlowRatePass1));

        events.Verify(
            instance => instance.NotifyJobQueuedAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SubmitAsync_RetractionCalibrationMethod_CreatesOrdinarySliceJobWithNoCalibrationSagaFields()
    {
        // Issue #2137: retraction must be end-to-end submittable through the same calibration
        // request path as flow_rate_pass_1/temperature_tower, and must remain an ordinary slice
        // job — see the class-level remarks on why the three saga fields must stay null.
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
            Calibration = new CalibrationRequest
            {
                Method = "retraction",
                Params = new Dictionary<string, double> { ["start_retraction_mm"] = 0.3 },
            },
        };

        IActionResult result = await controller.SubmitAsync(request, CancellationToken.None);

        _ = result.Should().BeOfType<CreatedResult>();
        _ = added.Should().NotBeNull();
        _ = added!.CalibrationMethod.Should().Be("retraction");
        _ = added.CalibrationParamsJson.Should().NotBeNullOrEmpty();

        _ = added.CalibrationProjectId.Should().BeNull();
        _ = added.CalibrationAttemptId.Should().BeNull();
        _ = added.CalibrationOrchestrationId.Should().BeNull();

        _ = added.ModelFileUrl.Should().Be("calibration:retraction");
        _ = added.ModelFileName.Should().Be(CalibrationMethods.DefaultModelFileName(CalibrationMethod.Retraction));

        events.Verify(
            instance => instance.NotifyJobQueuedAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SubmitAsync_PressureAdvanceTowerMethod_CreatesOrdinarySliceJobWithNoCalibrationSagaFields()
    {
        // Issue #2136: mirrors SubmitAsync_KnownCalibrationMethod_CreatesOrdinarySliceJobWithNoCalibrationSagaFields
        // for the pressure advance tower method — proving the wire name is accepted end to end at
        // the submission boundary, and that it too remains an ordinary slice job.
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
            Calibration = new CalibrationRequest
            {
                Method = "pressure_advance_tower",
                Params = new Dictionary<string, double> { ["start_advance"] = 0.0, ["advance_step"] = 0.01 },
            },
        };

        IActionResult result = await controller.SubmitAsync(request, CancellationToken.None);

        _ = result.Should().BeOfType<CreatedResult>();
        _ = added.Should().NotBeNull();
        _ = added!.CalibrationMethod.Should().Be("pressure_advance_tower");
        _ = added.CalibrationParamsJson.Should().NotBeNullOrEmpty();

        _ = added.CalibrationProjectId.Should().BeNull();
        _ = added.CalibrationAttemptId.Should().BeNull();
        _ = added.CalibrationOrchestrationId.Should().BeNull();

        _ = added.ModelFileUrl.Should().Be("calibration:pressure_advance_tower");
        _ = added.ModelFileName.Should().Be(CalibrationMethods.DefaultModelFileName(CalibrationMethod.PressureAdvanceTower));

        events.Verify(
            instance => instance.NotifyJobQueuedAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task SubmitAsync_CalibrationModeWithLegacySagaId_ReturnsInvalidRequest(
        bool setProjectId,
        bool setAttemptId,
        bool setOrchestrationId)
    {
        // A client must never be able to combine calibration mode (issue #1938) with the unrelated
        // printer/toolhead calibration-projects saga's identifiers: that would let a calibration
        // slice masquerade as (or accidentally trip) SlicePrintBridgeController.IsCalibrationSlice,
        // which inspects exactly these three fields and refuses to bridge such jobs to a printer.
        SliceJobController controller = CreateController(out Mock<ISliceJobRepository> repository, out _);

        var request = new SubmitSliceJobRequest
        {
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            Priority = 1,
            Calibration = new CalibrationRequest { Method = "flow_rate_pass_1" },
            CalibrationProjectId = setProjectId ? Guid.NewGuid() : null,
            CalibrationAttemptId = setAttemptId ? Guid.NewGuid() : null,
            CalibrationOrchestrationId = setOrchestrationId ? Guid.NewGuid() : null,
        };

        IActionResult result = await controller.SubmitAsync(request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        _ = objectResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        _ = problem.Extensions["code"].Should().Be("calibration_mode_conflicts_with_saga_ids");
        repository.Verify(instance => instance.AddAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SubmitAsync_MaxVolumetricSpeedMethod_CreatesOrdinarySliceJobWithNoCalibrationSagaFields()
    {
        // Issue #2135 acceptance: the wire name must submit end to end like every other built
        // calibration method, not just parse.
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
            Calibration = new CalibrationRequest
            {
                Method = "max_volumetric_speed",
                Params = new Dictionary<string, double> { ["max_volumetric_speed_ceiling_mm3s"] = 40 },
            },
        };

        IActionResult result = await controller.SubmitAsync(request, CancellationToken.None);

        _ = result.Should().BeOfType<CreatedResult>();
        _ = added.Should().NotBeNull();
        _ = added!.CalibrationMethod.Should().Be("max_volumetric_speed");
        _ = added.CalibrationParamsJson.Should().NotBeNullOrEmpty();

        _ = added.CalibrationProjectId.Should().BeNull();
        _ = added.CalibrationAttemptId.Should().BeNull();
        _ = added.CalibrationOrchestrationId.Should().BeNull();

        _ = added.ModelFileUrl.Should().Be("calibration:max_volumetric_speed");
        _ = added.ModelFileName.Should().Be(CalibrationMethods.DefaultModelFileName(CalibrationMethod.MaximumVolumetricSpeed));

        events.Verify(
            instance => instance.NotifyJobQueuedAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()),
            Times.Once);
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
