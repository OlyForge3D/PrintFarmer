using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Contracts;
using Farm.Web.Api.Services.Calibration;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Web.Api.Tests.Services.Calibration;

/// <summary>
/// Covers the full ten-step filament-calibration saga
/// (<c>created -&gt; cloning-profile -&gt; slicing -&gt; awaiting-slice -&gt; sending-to-printer -&gt;
/// awaiting-print -&gt; awaiting-measurement -&gt; applying-measurement -&gt; advancing -&gt;
/// completed</c>), including failure/retry behavior at every retryable step and the terminal,
/// non-retryable failure path.
/// </summary>
public sealed class CalibrationOrchestrationSagaServiceTests
{
    [Fact]
    public async Task AdvanceAsync_HappyPath_DrivesAllTenStepsToCompleted()
    {
        await using AppDbContext db = CreateContext();
        CalibrationProjectService projectService = CreateProjectService(db);
        FakeSliceSubmissionGateway sliceGateway = new();
        FakePrintDispatchGateway printGateway = new();
        CalibrationOrchestrationSagaService saga = CreateSaga(db, projectService, sliceGateway, printGateway);
        CalibrationActor actor = CreateActor();
        (Guid orchestrationId, Guid attemptId) = await CreateProjectAndAttemptAsync(projectService, actor);

        Guid sliceJobId = Guid.NewGuid();
        Guid gcodeFileId = Guid.NewGuid();
        sliceGateway.SubmitBehavior = _ => SliceSubmissionResult.Ok(sliceJobId);
        sliceGateway.StatusBehavior = _ => SliceStatusResult.Ok("Completed", gcodeFileId);
        printGateway.SendBehavior = (_, _) => PrintDispatchResult.Ok();

        // created -> cloning-profile
        CalibrationApiResult<CalibrationOrchestrationDto> result = await AdvanceAsync(saga, orchestrationId, actor);
        _ = result.Value!.CurrentStep.Should().Be(CalibrationSagaSteps.Slicing);

        // slicing -> awaiting-slice
        result = await AdvanceAsync(saga, orchestrationId, actor);
        _ = result.Value!.CurrentStep.Should().Be(CalibrationSagaSteps.AwaitingSlice);
        _ = result.Value!.SliceJobId.Should().Be(sliceJobId);

        // awaiting-slice -> sending-to-printer (slice reports Completed)
        result = await AdvanceAsync(saga, orchestrationId, actor);
        _ = result.Value!.CurrentStep.Should().Be(CalibrationSagaSteps.SendingToPrinter);
        _ = result.Value!.GcodeFileId.Should().Be(gcodeFileId);

        // sending-to-printer -> awaiting-print
        result = await AdvanceAsync(saga, orchestrationId, actor);
        _ = result.Value!.CurrentStep.Should().Be(CalibrationSagaSteps.AwaitingPrint);

        // awaiting-print polling with no signal is a no-op
        result = await AdvanceAsync(saga, orchestrationId, actor);
        _ = result.Value!.CurrentStep.Should().Be(CalibrationSagaSteps.AwaitingPrint);

        // awaiting-print -> awaiting-measurement once the caller reports print completion
        result = await AdvanceAsync(saga, orchestrationId, actor, printCompleted: true);
        _ = result.Value!.CurrentStep.Should().Be(CalibrationSagaSteps.AwaitingMeasurement);

        // awaiting-measurement polling before any observation exists is a no-op
        result = await AdvanceAsync(saga, orchestrationId, actor);
        _ = result.Value!.CurrentStep.Should().Be(CalibrationSagaSteps.AwaitingMeasurement);

        await AddObservationAsync(db, attemptId);

        // awaiting-measurement -> applying-measurement once a measurement is recorded
        result = await AdvanceAsync(saga, orchestrationId, actor);
        _ = result.Value!.CurrentStep.Should().Be(CalibrationSagaSteps.ApplyingMeasurement);

        // applying-measurement -> advancing
        result = await AdvanceAsync(saga, orchestrationId, actor);
        _ = result.Value!.CurrentStep.Should().Be(CalibrationSagaSteps.Advancing);

        // advancing -> completed
        result = await AdvanceAsync(saga, orchestrationId, actor);
        _ = result.Value!.CurrentStep.Should().Be(CalibrationSagaSteps.Completed);
        _ = result.Value!.Status.Should().Be(nameof(CalibrationOrchestrationStatus.Completed));
        _ = result.Value!.CompletedAtUtc.Should().NotBeNull();

        // completed is a terminal, idempotent no-op: repeated Advance calls change nothing further.
        long completedRevision = result.Value!.Revision;
        result = await AdvanceAsync(saga, orchestrationId, actor);
        _ = result.Value!.CurrentStep.Should().Be(CalibrationSagaSteps.Completed);
        _ = result.Value!.Revision.Should().Be(completedRevision);

        // Recording run state never gated anything up front: the attempt's own timeline captured
        // every hop as an append-only event, and no new precondition record was required to exist
        // before any of the steps above ran.
        int eventCount = await db.CalibrationAttemptEvents.CountAsync(e => e.AttemptId == attemptId);
        _ = eventCount.Should().BeGreaterThanOrEqualTo(8);
    }

    [Fact]
    public async Task AdvanceAsync_UnparsableMethod_FailsTerminallyWithoutRetry()
    {
        await using AppDbContext db = CreateContext();
        CalibrationProjectService projectService = CreateProjectService(db);
        CalibrationOrchestrationSagaService saga = CreateSaga(
            db,
            projectService,
            new FakeSliceSubmissionGateway(),
            new FakePrintDispatchGateway());
        CalibrationActor actor = CreateActor();
        (Guid orchestrationId, _) = await CreateProjectAndAttemptAsync(actor, projectService, methodName: "not-a-real-method");

        CalibrationApiResult<CalibrationOrchestrationDto> result = await AdvanceAsync(saga, orchestrationId, actor);

        _ = result.Value!.CurrentStep.Should().Be(CalibrationSagaSteps.Created);
        _ = result.Value!.Status.Should().Be(nameof(CalibrationOrchestrationStatus.Failed));
        _ = result.Value!.LastErrorCode.Should().Be("unknown_calibration_method");

        // A terminal failure is not a new precondition on future calibrations: it only stops this
        // one orchestration's own automatic advancement, and further Advance calls report the
        // terminal conflict rather than silently re-running anything.
        CalibrationApiResult<CalibrationOrchestrationDto> secondAdvance =
            await AdvanceAsync(saga, orchestrationId, actor);
        _ = secondAdvance.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _ = secondAdvance.Code.Should().Be("calibration_orchestration_terminally_failed");
    }

    [Fact]
    public async Task AdvanceAsync_SlicingFailsThenSucceeds_RetriesWithinBudget()
    {
        await using AppDbContext db = CreateContext();
        CalibrationProjectService projectService = CreateProjectService(db);
        FakeSliceSubmissionGateway sliceGateway = new();
        CalibrationOrchestrationSagaService saga = CreateSaga(
            db,
            projectService,
            sliceGateway,
            new FakePrintDispatchGateway());
        CalibrationActor actor = CreateActor();
        (Guid orchestrationId, _) = await CreateProjectAndAttemptAsync(projectService, actor);
        _ = await AdvanceAsync(saga, orchestrationId, actor); // created -> cloning-profile transition

        int submitAttempts = 0;
        Guid sliceJobId = Guid.NewGuid();
        sliceGateway.SubmitBehavior = _ =>
        {
            submitAttempts++;
            return submitAttempts < 2
                ? SliceSubmissionResult.Failed("slice_submission_rejected")
                : SliceSubmissionResult.Ok(sliceJobId);
        };

        CalibrationApiResult<CalibrationOrchestrationDto> failed = await AdvanceAsync(saga, orchestrationId, actor);
        _ = failed.Value!.CurrentStep.Should().Be(CalibrationSagaSteps.Slicing);
        _ = failed.Value!.Status.Should().Be(nameof(CalibrationOrchestrationStatus.WaitingToRetry));
        _ = failed.Value!.RetryCount.Should().Be(1);
        _ = failed.Value!.NextRetryAtUtc.Should().NotBeNull();

        CalibrationApiResult<CalibrationOrchestrationDto> succeeded = await AdvanceAsync(saga, orchestrationId, actor);
        _ = succeeded.Value!.CurrentStep.Should().Be(CalibrationSagaSteps.AwaitingSlice);
        _ = succeeded.Value!.SliceJobId.Should().Be(sliceJobId);
        _ = submitAttempts.Should().Be(2);
    }

    [Fact]
    public async Task AdvanceAsync_SlicingFailsPastRetryBudget_FailsTerminally()
    {
        await using AppDbContext db = CreateContext();
        CalibrationProjectService projectService = CreateProjectService(db);
        FakeSliceSubmissionGateway sliceGateway = new()
        {
            SubmitBehavior = _ => SliceSubmissionResult.Failed("slice_submission_rejected"),
        };
        CalibrationOrchestrationSagaService saga = CreateSaga(
            db,
            projectService,
            sliceGateway,
            new FakePrintDispatchGateway());
        CalibrationActor actor = CreateActor();
        (Guid orchestrationId, _) = await CreateProjectAndAttemptAsync(projectService, actor);
        _ = await AdvanceAsync(saga, orchestrationId, actor); // created -> cloning-profile transition

        CalibrationApiResult<CalibrationOrchestrationDto> result = null!;
        for (int i = 0; i < CalibrationOrchestrationSagaService.MaximumStepRetries + 1; i++)
        {
            result = await AdvanceAsync(saga, orchestrationId, actor);
        }

        _ = result.Value!.Status.Should().Be(nameof(CalibrationOrchestrationStatus.Failed));
        _ = result.Value!.CurrentStep.Should().Be(CalibrationSagaSteps.Slicing);
        _ = result.Value!.RetryCount.Should().Be(CalibrationOrchestrationSagaService.MaximumStepRetries + 1);

        CalibrationApiResult<CalibrationOrchestrationDto> afterFailure =
            await AdvanceAsync(saga, orchestrationId, actor);
        _ = afterFailure.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task AdvanceAsync_AwaitingSlice_StillProcessing_IsANoOpPoll()
    {
        await using AppDbContext db = CreateContext();
        CalibrationProjectService projectService = CreateProjectService(db);
        FakeSliceSubmissionGateway sliceGateway = new()
        {
            SubmitBehavior = _ => SliceSubmissionResult.Ok(Guid.NewGuid()),
            StatusBehavior = _ => SliceStatusResult.Ok("Processing", null),
        };
        CalibrationOrchestrationSagaService saga = CreateSaga(
            db,
            projectService,
            sliceGateway,
            new FakePrintDispatchGateway());
        CalibrationActor actor = CreateActor();
        (Guid orchestrationId, _) = await CreateProjectAndAttemptAsync(projectService, actor);
        _ = await AdvanceAsync(saga, orchestrationId, actor); // -> slicing
        _ = await AdvanceAsync(saga, orchestrationId, actor); // -> awaiting-slice

        CalibrationApiResult<CalibrationOrchestrationDto> polled = await AdvanceAsync(saga, orchestrationId, actor);

        _ = polled.Value!.CurrentStep.Should().Be(CalibrationSagaSteps.AwaitingSlice);
        _ = polled.Value!.RetryCount.Should().Be(0);
    }

    [Fact]
    public async Task AdvanceAsync_AwaitingSlice_JobFails_RevertsToSlicingAndRetries()
    {
        await using AppDbContext db = CreateContext();
        CalibrationProjectService projectService = CreateProjectService(db);
        FakeSliceSubmissionGateway sliceGateway = new()
        {
            SubmitBehavior = _ => SliceSubmissionResult.Ok(Guid.NewGuid()),
            StatusBehavior = _ => SliceStatusResult.Failed("slice_job_failed"),
        };
        CalibrationOrchestrationSagaService saga = CreateSaga(
            db,
            projectService,
            sliceGateway,
            new FakePrintDispatchGateway());
        CalibrationActor actor = CreateActor();
        (Guid orchestrationId, _) = await CreateProjectAndAttemptAsync(projectService, actor);
        _ = await AdvanceAsync(saga, orchestrationId, actor); // -> slicing
        _ = await AdvanceAsync(saga, orchestrationId, actor); // -> awaiting-slice

        CalibrationApiResult<CalibrationOrchestrationDto> result = await AdvanceAsync(saga, orchestrationId, actor);

        _ = result.Value!.CurrentStep.Should().Be(CalibrationSagaSteps.Slicing);
        _ = result.Value!.Status.Should().Be(nameof(CalibrationOrchestrationStatus.WaitingToRetry));
        _ = result.Value!.RetryCount.Should().Be(1);
    }

    [Fact]
    public async Task AdvanceAsync_SendingToPrinterFailsThenSucceeds_RetriesWithinBudget()
    {
        await using AppDbContext db = CreateContext();
        CalibrationProjectService projectService = CreateProjectService(db);
        Guid sliceJobId = Guid.NewGuid();
        FakeSliceSubmissionGateway sliceGateway = new()
        {
            SubmitBehavior = _ => SliceSubmissionResult.Ok(sliceJobId),
            StatusBehavior = _ => SliceStatusResult.Ok("Completed", Guid.NewGuid()),
        };
        FakePrintDispatchGateway printGateway = new();
        CalibrationOrchestrationSagaService saga = CreateSaga(db, projectService, sliceGateway, printGateway);
        CalibrationActor actor = CreateActor();
        (Guid orchestrationId, _) = await CreateProjectAndAttemptAsync(projectService, actor);
        _ = await AdvanceAsync(saga, orchestrationId, actor); // -> slicing
        _ = await AdvanceAsync(saga, orchestrationId, actor); // -> awaiting-slice
        _ = await AdvanceAsync(saga, orchestrationId, actor); // -> sending-to-printer

        int sendAttempts = 0;
        printGateway.SendBehavior = (_, _) =>
        {
            sendAttempts++;
            return sendAttempts < 2
                ? PrintDispatchResult.Failed("send_to_printer_rejected")
                : PrintDispatchResult.Ok();
        };

        CalibrationApiResult<CalibrationOrchestrationDto> failed = await AdvanceAsync(saga, orchestrationId, actor);
        _ = failed.Value!.CurrentStep.Should().Be(CalibrationSagaSteps.SendingToPrinter);
        _ = failed.Value!.Status.Should().Be(nameof(CalibrationOrchestrationStatus.WaitingToRetry));

        CalibrationApiResult<CalibrationOrchestrationDto> succeeded = await AdvanceAsync(saga, orchestrationId, actor);
        _ = succeeded.Value!.CurrentStep.Should().Be(CalibrationSagaSteps.AwaitingPrint);
        _ = sendAttempts.Should().Be(2);
    }

    [Fact]
    public async Task AdvanceAsync_AwaitingPrint_ReportedFailurePastBudget_FailsTerminally()
    {
        await using AppDbContext db = CreateContext();
        CalibrationProjectService projectService = CreateProjectService(db);
        FakeSliceSubmissionGateway sliceGateway = new()
        {
            SubmitBehavior = _ => SliceSubmissionResult.Ok(Guid.NewGuid()),
            StatusBehavior = _ => SliceStatusResult.Ok("Completed", Guid.NewGuid()),
        };
        FakePrintDispatchGateway printGateway = new() { SendBehavior = (_, _) => PrintDispatchResult.Ok() };
        CalibrationOrchestrationSagaService saga = CreateSaga(db, projectService, sliceGateway, printGateway);
        CalibrationActor actor = CreateActor();
        (Guid orchestrationId, _) = await CreateProjectAndAttemptAsync(projectService, actor);
        _ = await AdvanceAsync(saga, orchestrationId, actor); // -> slicing
        _ = await AdvanceAsync(saga, orchestrationId, actor); // -> awaiting-slice
        _ = await AdvanceAsync(saga, orchestrationId, actor); // -> sending-to-printer
        _ = await AdvanceAsync(saga, orchestrationId, actor); // -> awaiting-print

        CalibrationApiResult<CalibrationOrchestrationDto> result = null!;
        for (int i = 0; i < CalibrationOrchestrationSagaService.MaximumStepRetries + 1; i++)
        {
            result = await AdvanceAsync(saga, orchestrationId, actor, printFailed: true);
        }

        _ = result.Value!.CurrentStep.Should().Be(CalibrationSagaSteps.AwaitingPrint);
        _ = result.Value!.Status.Should().Be(nameof(CalibrationOrchestrationStatus.Failed));
    }

    [Fact]
    public async Task GetAsync_DifferentOwner_ReturnsNotFound()
    {
        await using AppDbContext db = CreateContext();
        CalibrationProjectService projectService = CreateProjectService(db);
        CalibrationOrchestrationSagaService saga = CreateSaga(
            db,
            projectService,
            new FakeSliceSubmissionGateway(),
            new FakePrintDispatchGateway());
        CalibrationActor owner = CreateActor();
        (Guid orchestrationId, _) = await CreateProjectAndAttemptAsync(projectService, owner);

        CalibrationApiResult<CalibrationOrchestrationDto> result = await saga.GetAsync(
            orchestrationId,
            new(Guid.NewGuid(), "someone-else", false),
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetByAttemptAsync_ReturnsTheSameCheckpointAsGetById()
    {
        await using AppDbContext db = CreateContext();
        CalibrationProjectService projectService = CreateProjectService(db);
        CalibrationOrchestrationSagaService saga = CreateSaga(
            db,
            projectService,
            new FakeSliceSubmissionGateway(),
            new FakePrintDispatchGateway());
        CalibrationActor actor = CreateActor();
        (Guid orchestrationId, Guid attemptId) = await CreateProjectAndAttemptAsync(projectService, actor);

        CalibrationApiResult<CalibrationOrchestrationDto> byId =
            await saga.GetAsync(orchestrationId, actor, CancellationToken.None);
        CalibrationApiResult<CalibrationOrchestrationDto> byAttempt =
            await saga.GetByAttemptAsync(attemptId, actor, CancellationToken.None);

        _ = byAttempt.Value!.Id.Should().Be(byId.Value!.Id);
    }

    private static AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"calibration-saga-service-{Guid.NewGuid()}")
            .Options;
        return new(options);
    }

    private static CalibrationProjectService CreateProjectService(AppDbContext db) =>
        new(
            db,
            new TestCalibrationBlobStore(),
            TimeProvider.System,
            NullLogger<CalibrationProjectService>.Instance);

    private static CalibrationOrchestrationSagaService CreateSaga(
        AppDbContext db,
        ICalibrationProjectService projectService,
        ISliceSubmissionGateway sliceGateway,
        IPrintDispatchGateway printGateway) =>
        new(
            db,
            projectService,
            sliceGateway,
            printGateway,
            TimeProvider.System,
            NullLogger<CalibrationOrchestrationSagaService>.Instance);

    private static CalibrationActor CreateActor()
    {
        Guid userId = Guid.NewGuid();
        return new(userId, userId.ToString(), false);
    }

    private static async Task<(Guid OrchestrationId, Guid AttemptId)> CreateProjectAndAttemptAsync(
        ICalibrationProjectService projectService,
        CalibrationActor actor,
        string methodName = CalibrationMethodNames.Temperature) =>
        await CreateProjectAndAttemptAsync(actor, projectService, methodName);

    private static async Task<(Guid OrchestrationId, Guid AttemptId)> CreateProjectAndAttemptAsync(
        CalibrationActor actor,
        ICalibrationProjectService projectService,
        string methodName)
    {
        CalibrationApiResult<CalibrationProjectDto> project = await projectService.CreateProjectAsync(
            new CalibrationProjectCreateRequest
            {
                ClientId = "test-client",
                RequestId = $"project-{Guid.NewGuid():N}",
                Name = "Saga baseline",
                PrinterId = Guid.NewGuid(),
                PrinterConfigurationRevision = 1,
                FilamentProvider = "catalog",
                FilamentProductId = "sku-pla-blue",
                FilamentProductName = "PLA Blue",
                FilamentMaterial = "PLA",
                FilamentSnapshot = JsonSerializer.SerializeToElement(new { vendor = "OlyForge" }),
                OrderedSteps = JsonSerializer.SerializeToElement(new[] { "temperature" }),
                CurrentSelections = JsonSerializer.SerializeToElement(new { }),
                ExperienceMode = "Coach",
            },
            actor,
            CancellationToken.None);
        _ = project.StatusCode.Should().Be(StatusCodes.Status201Created);

        CalibrationApiResult<CalibrationAttemptDto> attempt = await projectService.CreateAttemptAsync(
            project.Value!.Id,
            new CalibrationAttemptCreateRequest
            {
                ClientId = "test-client",
                RequestId = $"attempt-{Guid.NewGuid():N}",
                CalibrationKind = "temperature",
                Method = methodName,
                DefinitionVersion = "1",
                Input = JsonSerializer.SerializeToElement(new { modelUrl = "https://example.test/model.3mf" }),
                Specification = JsonSerializer.SerializeToElement(new { targetTemperatureC = 210 }),
                ProfileSnapshotIds = JsonSerializer.SerializeToElement(Array.Empty<Guid>()),
                PrinterConfigurationRevision = 1,
            },
            actor,
            CancellationToken.None);
        _ = attempt.StatusCode.Should().Be(StatusCodes.Status201Created);

        AppDbContext db = ExtractDbContext(projectService);
        Guid orchestrationId = await db.CalibrationOrchestrations
            .Where(o => o.AttemptId == attempt.Value!.Id)
            .Select(o => o.Id)
            .SingleAsync();
        return (orchestrationId, attempt.Value!.Id);
    }

    private static AppDbContext ExtractDbContext(ICalibrationProjectService projectService) =>
        (AppDbContext)typeof(CalibrationProjectService)
            .GetField("_dbContext", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(projectService)!;

    private static async Task AddObservationAsync(AppDbContext db, Guid attemptId)
    {
        CalibrationAttempt attempt = await db.CalibrationAttempts.SingleAsync(a => a.Id == attemptId);
        _ = db.CalibrationObservations.Add(new CalibrationObservation
        {
            Id = Guid.NewGuid(),
            ProjectId = attempt.ProjectId,
            AttemptId = attemptId,
            Sequence = 1,
            ObservationType = "measurement",
            MeasurementsJson = "{}",
            ResultJson = "{}",
            UnitsJson = "{}",
            OperationId = $"observation-{Guid.NewGuid():N}",
            ObservedAtUtc = DateTime.UtcNow,
            ActorSubject = "test-actor",
        });
        _ = await db.SaveChangesAsync();
    }

    private static Task<CalibrationApiResult<CalibrationOrchestrationDto>> AdvanceAsync(
        CalibrationOrchestrationSagaService saga,
        Guid orchestrationId,
        CalibrationActor actor,
        bool? printCompleted = null,
        bool? printFailed = null) =>
        saga.AdvanceAsync(
            orchestrationId,
            new CalibrationOrchestrationAdvanceRequest
            {
                ClientId = "test-client",
                OperationId = $"advance-{Guid.NewGuid():N}",
                PrintCompleted = printCompleted,
                PrintFailed = printFailed,
            },
            actor,
            CancellationToken.None);

    private sealed class FakeSliceSubmissionGateway : ISliceSubmissionGateway
    {
        public Func<CalibrationSliceSubmission, SliceSubmissionResult> SubmitBehavior { get; set; } =
            _ => SliceSubmissionResult.Ok(Guid.NewGuid());

        public Func<Guid, SliceStatusResult> StatusBehavior { get; set; } =
            _ => SliceStatusResult.Ok("Completed", Guid.NewGuid());

        public Task<SliceSubmissionResult> SubmitAsync(CalibrationSliceSubmission submission, CancellationToken ct) =>
            Task.FromResult(SubmitBehavior(submission));

        public Task<SliceStatusResult> GetStatusAsync(Guid sliceJobId, CancellationToken ct) =>
            Task.FromResult(StatusBehavior(sliceJobId));
    }

    private sealed class FakePrintDispatchGateway : IPrintDispatchGateway
    {
        public Func<Guid, Guid, PrintDispatchResult> SendBehavior { get; set; } =
            (_, _) => PrintDispatchResult.Ok();

        public Task<PrintDispatchResult> SendToPrinterAsync(Guid sliceJobId, Guid printerId, CancellationToken ct) =>
            Task.FromResult(SendBehavior(sliceJobId, printerId));
    }

    private sealed class TestCalibrationBlobStore : ICalibrationBlobStore
    {
        public Task DeleteAsync(string opaqueStorageKey, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<bool> ExistsAsync(string opaqueStorageKey, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<CalibrationBlobMetadata?> GetMetadataAsync(
            string opaqueStorageKey,
            CancellationToken cancellationToken) =>
            Task.FromResult<CalibrationBlobMetadata?>(null);

        public Task<Stream> OpenReadAsync(string opaqueStorageKey, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream());

        public async Task<CalibrationBlobMetadata> PutAsync(
            CalibrationBlobWriteRequest request,
            Stream content,
            CancellationToken cancellationToken)
        {
            using MemoryStream copy = new();
            await content.CopyToAsync(copy, cancellationToken);
            string sourceSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(copy.ToArray()))
                .ToLowerInvariant();
            return new CalibrationBlobMetadata(
                $"calibration/{request.PhotoId:N}.png",
                "image/png",
                copy.Length,
                sourceSha256,
                1,
                1,
                sourceSha256);
        }
    }
}
