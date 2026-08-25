using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Web.Api.Contracts;
using Farm.Web.Api.Services.Calibration;
using Farm.Web.Api.Services.Calibration.Generation;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Farm.Web.Api.Tests.Calibration.Generation;

/// <summary>
/// Covers the still-reachable calibration generation saga behaviour: acceptance semantics,
/// infrastructure retry/lease mechanics, and the current immediate terminal failure after
/// acceptance when the snapshot-based context is absent.
/// </summary>
public sealed class CalibrationGenerationSagaTests : IAsyncLifetime
{
    private CalibrationGenerationHarness _harness = null!;

    public async Task InitializeAsync() => _harness = await CalibrationGenerationHarness.CreateAsync();

    public Task DisposeAsync()
    {
        _harness.Dispose();
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "A new generation request is accepted with 202 and a durable status route")]
    public async Task CreateOrResumeAsync_WithNewRequest_Returns202AndStatusRoute()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();

        CalibrationApiResult<CalibrationOrchestrationStatusDto> result =
            await _harness.CreateSaga().CreateOrResumeAsync(
                fixture.ProjectId,
                fixture.AttemptId,
                "generate-0001",
                fixture.Request(),
                fixture.Owner,
                CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status202Accepted, result.Code);
        _ = result.Value!.StatusRoute.Should()
            .Be($"/api/calibration-orchestrations/{fixture.OrchestrationId}");
        _ = result.Value.Id.Should().Be(fixture.OrchestrationId);
    }

    [Fact(DisplayName = "An accepted request adopts the orchestration the attempt already created")]
    public async Task CreateOrResumeAsync_AdoptsExistingOrchestration()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();

        _ = await _harness.CreateSaga().CreateOrResumeAsync(
            fixture.ProjectId,
            fixture.AttemptId,
            "generate-adopt",
            fixture.Request(),
            fixture.Owner,
            CancellationToken.None);

        await using Farm.Infrastructure.Data.AppDbContext core = _harness.CreateCoreContext();
        _ = core.CalibrationOrchestrations.Count(row => row.AttemptId == fixture.AttemptId)
            .Should().Be(1);
        CalibrationOrchestration orchestration = await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = orchestration.OperationId.Should().Be(CalibrationGenerationHarness.AttemptOperationId);
        _ = orchestration.GenerationRequestSha256.Should().NotBeNullOrWhiteSpace();
    }

    [Fact(DisplayName = "An identical repeat request replays with 200 instead of starting a second run")]
    public async Task CreateOrResumeAsync_WithIdenticalRepeat_Returns200Replay()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();
        _ = await _harness.CreateSaga().CreateOrResumeAsync(
            fixture.ProjectId,
            fixture.AttemptId,
            "generate-replay",
            fixture.Request(),
            fixture.Owner,
            CancellationToken.None);

        CalibrationApiResult<CalibrationOrchestrationStatusDto> replay =
            await _harness.CreateSaga().CreateOrResumeAsync(
                fixture.ProjectId,
                fixture.AttemptId,
                "generate-replay",
                fixture.Request(),
                fixture.Owner,
                CancellationToken.None);

        _ = replay.StatusCode.Should().Be(StatusCodes.Status200OK, replay.Code);
        _ = replay.Replayed.Should().BeTrue();
    }

    [Fact(DisplayName = "Concurrent identical requests produce exactly one accepted run")]
    public async Task CreateOrResumeAsync_WithConcurrentIdenticalRequests_AcceptsOnce()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();

        CalibrationApiResult<CalibrationOrchestrationStatusDto>[] results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => _harness.CreateSaga().CreateOrResumeAsync(
                fixture.ProjectId,
                fixture.AttemptId,
                "generate-concurrent",
                fixture.Request(),
                fixture.Owner,
                CancellationToken.None)));

        _ = results.Should().OnlyContain(result =>
            result.StatusCode == StatusCodes.Status202Accepted ||
            result.StatusCode == StatusCodes.Status200OK);
        _ = results.Count(result => result.StatusCode == StatusCodes.Status202Accepted)
            .Should().Be(1);
    }

    [Fact(DisplayName = "The same operation key with a changed payload conflicts with 409")]
    public async Task CreateOrResumeAsync_WithChangedPayload_Returns409()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();
        _ = await _harness.CreateSaga().CreateOrResumeAsync(
            fixture.ProjectId,
            fixture.AttemptId,
            "generate-mismatch",
            fixture.Request(),
            fixture.Owner,
            CancellationToken.None);

        CalibrationGenerateJobRequest changed = new()
        {
            Method = fixture.Method,
            DefinitionVersion = CalibrationMethodOptions.CurrentDefinitionVersion,
            Options = new CalibrationMethodOptionsRequest { StartCelsius = 200 },
        };
        CalibrationApiResult<CalibrationOrchestrationStatusDto> conflict =
            await _harness.CreateSaga().CreateOrResumeAsync(
                fixture.ProjectId,
                fixture.AttemptId,
                "generate-mismatch",
                changed,
                fixture.Owner,
                CancellationToken.None);

        _ = conflict.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _ = conflict.Code.Should().Be("idempotency_payload_mismatch");
    }

    [Fact(DisplayName = "A second operation key cannot adopt a run another key already owns")]
    public async Task CreateOrResumeAsync_WithSecondOperationKey_Returns409()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();
        _ = await _harness.CreateSaga().CreateOrResumeAsync(
            fixture.ProjectId,
            fixture.AttemptId,
            "generate-first",
            fixture.Request(),
            fixture.Owner,
            CancellationToken.None);

        CalibrationApiResult<CalibrationOrchestrationStatusDto> conflict =
            await _harness.CreateSaga().CreateOrResumeAsync(
                fixture.ProjectId,
                fixture.AttemptId,
                "generate-second",
                fixture.Request(),
                fixture.Owner,
                CancellationToken.None);

        _ = conflict.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _ = conflict.Code.Should().Be("incompatible_existing_operation");
    }

    [Fact(DisplayName = "A stale orchestration revision fails the precondition with 412")]
    public async Task CreateOrResumeAsync_WithStaleRevision_Returns412()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();

        CalibrationApiResult<CalibrationOrchestrationStatusDto> result =
            await _harness.CreateSaga().CreateOrResumeAsync(
                fixture.ProjectId,
                fixture.AttemptId,
                "generate-precondition",
                fixture.Request(baseRevision: 99),
                fixture.Owner,
                CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status412PreconditionFailed);
        _ = result.Code.Should().Be("revision_conflict");
    }

    [Theory(DisplayName = "An unsupported or unsafe specification is rejected with structured 422 reasons")]
    [InlineData("not-a-method", "1.0", "method")]
    [InlineData(CalibrationMethodNames.Temperature, "0.9", "definitionVersion")]
    public async Task CreateOrResumeAsync_WithUnsupportedRequest_Returns422(
        string method,
        string definitionVersion,
        string expectedField)
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();

        CalibrationApiResult<CalibrationOrchestrationStatusDto> result =
            await _harness.CreateSaga().CreateOrResumeAsync(
                fixture.ProjectId,
                fixture.AttemptId,
                "generate-invalid",
                new CalibrationGenerateJobRequest
                {
                    Method = method,
                    DefinitionVersion = definitionVersion,
                    Options = new CalibrationMethodOptionsRequest(),
                },
                fixture.Owner,
                CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        _ = result.Code.Should().Be("unsupported_or_unsafe_calibration_specification");
        _ = result.Value!.Problems.Should().ContainSingle(problem => problem.Field == expectedField);
    }

    [Fact(DisplayName = "An option the selected method does not define is rejected with its field")]
    public async Task CreateOrResumeAsync_WithForeignOption_Returns422WithField()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();

        CalibrationApiResult<CalibrationOrchestrationStatusDto> result =
            await _harness.CreateSaga().CreateOrResumeAsync(
                fixture.ProjectId,
                fixture.AttemptId,
                "generate-foreign-option",
                new CalibrationGenerateJobRequest
                {
                    Method = CalibrationMethodNames.Temperature,
                    DefinitionVersion = CalibrationMethodOptions.CurrentDefinitionVersion,
                    Options = new CalibrationMethodOptionsRequest { StartPressureAdvance = 0.05m },
                },
                fixture.Owner,
                CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        _ = result.Value!.Problems.Should().ContainSingle(problem =>
            problem.Field == "options.startPressureAdvance" &&
            problem.Code == CalibrationGenerationProblemCodes.OptionNotAllowedForMethod);
    }

    [Fact(DisplayName = "Generation is refused with 503 while no pinned worker attests the build identity")]
    public async Task CreateOrResumeAsync_WithoutAttestedWorker_Returns503()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();

        CalibrationApiResult<CalibrationOrchestrationStatusDto> result =
            await _harness.CreateSaga().CreateOrResumeAsync(
                fixture.ProjectId,
                fixture.AttemptId,
                "generate-unavailable",
                fixture.Request(),
                fixture.Owner,
                CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        _ = result.Code.Should().Be("generation_dependency_unavailable");
    }

    [Fact(DisplayName = "A caller from another farm cannot see or start another owner's attempt")]
    public async Task CreateOrResumeAsync_ForForeignOwner_Returns404()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();

        CalibrationApiResult<CalibrationOrchestrationStatusDto> result =
            await _harness.CreateSaga().CreateOrResumeAsync(
                fixture.ProjectId,
                fixture.AttemptId,
                "generate-foreign",
                fixture.Request(),
                new CalibrationActor(Guid.NewGuid(), "intruder", false),
                CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact(DisplayName = "Resuming an accepted run now fails terminally because snapshot identity is unavailable")]
    public async Task ResumeAsync_WithConfiguredDependencies_FailsTerminallyWithContextIdentityMissing()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-terminal-failure");

        CalibrationApiResult<CalibrationOrchestrationStatusDto> result =
            await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status200OK);
        _ = result.Value!.Status.Should().Be(nameof(CalibrationOrchestrationStatus.Failed));
        _ = result.Value.CurrentStep.Should().Be(CalibrationGenerationSteps.Failed);
        _ = result.Value.LastErrorCode.Should().Be(CalibrationGenerationProblemCodes.ContextIdentityMissing);
        _ = result.Value.Problems.Should().ContainSingle(problem =>
            problem.Code == CalibrationGenerationProblemCodes.ContextIdentityMissing &&
            problem.Field == "attempt.printerConfigurationSnapshotId");

        CalibrationOrchestration orchestration = await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = orchestration.Status.Should().Be(CalibrationOrchestrationStatus.Failed);
        _ = orchestration.CurrentStep.Should().Be(CalibrationGenerationSteps.Failed);
        _ = orchestration.LastErrorCode.Should().Be(CalibrationGenerationProblemCodes.ContextIdentityMissing);
        _ = orchestration.CompletedAtUtc.Should().NotBeNull();
        _ = (await _harness.CountSliceJobsAsync(fixture.OrchestrationId)).Should().Be(0);
    }

    [Fact(DisplayName = "Resuming an already failed orchestration returns the same terminal status without rerunning")]
    public async Task ResumeAsync_AfterTerminalFailure_ReturnsSameFailedStatus()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-terminal-repeat");

        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);
        CalibrationOrchestration failed = await _harness.GetOrchestrationAsync(fixture.OrchestrationId);

        CalibrationApiResult<CalibrationOrchestrationStatusDto> replay =
            await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        _ = replay.StatusCode.Should().Be(StatusCodes.Status200OK);
        _ = replay.Value!.Status.Should().Be(nameof(CalibrationOrchestrationStatus.Failed));
        _ = replay.Value.CurrentStep.Should().Be(CalibrationGenerationSteps.Failed);
        _ = replay.Value.LastErrorCode.Should().Be(CalibrationGenerationProblemCodes.ContextIdentityMissing);

        CalibrationOrchestration afterReplay = await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = afterReplay.Revision.Should().Be(failed.Revision);
        _ = afterReplay.CompletedAtUtc.Should().Be(failed.CompletedAtUtc);
        _ = (await _harness.CountSliceJobsAsync(fixture.OrchestrationId)).Should().Be(0);
    }

    [Fact(DisplayName = "An unavailable slicing path is a safe retry, not a terminal failure")]
    public async Task ResumeAsync_WithoutSlicingPath_SchedulesSafeRetry()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-retry");

        _ = await _harness.CreateSaga(new CalibrationGenerationHarnessOptions
        {
            SliceSubmissionRoutable = false,
        }).ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        CalibrationOrchestration orchestration = await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = orchestration.Status.Should().Be(CalibrationOrchestrationStatus.WaitingToRetry);
        _ = orchestration.RetryCount.Should().Be(1);
        _ = orchestration.NextRetryAtUtc.Should().NotBeNull();
        _ = orchestration.LastErrorCode.Should()
            .Be(CalibrationGenerationProblemCodes.SliceSubmissionUnavailable);
    }

    [Fact(DisplayName = "The recovery pass resumes a due run and records the terminal context failure")]
    public async Task RecoverDueAsync_ResumesDueOrchestrationAndFailsTerminally()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-recovery");

        int advanced = await _harness.CreateSaga().RecoverDueAsync(10, CancellationToken.None);

        _ = advanced.Should().Be(1);
        CalibrationOrchestration orchestration = await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = orchestration.Status.Should().Be(CalibrationOrchestrationStatus.Failed);
        _ = orchestration.CurrentStep.Should().Be(CalibrationGenerationSteps.Failed);
        _ = orchestration.LastErrorCode.Should().Be(CalibrationGenerationProblemCodes.ContextIdentityMissing);
    }

    [Fact(DisplayName = "A held lease keeps a second concurrent pass from processing the same run")]
    public async Task ResumeAsync_WhileLeaseIsHeld_IsSkipped()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-lease");
        await _harness.MutateOrchestrationAsync(fixture.OrchestrationId, orchestration =>
        {
            orchestration.LeaseOwner = "another-host:1";
            orchestration.LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(5);
        });

        CalibrationApiResult<CalibrationOrchestrationStatusDto> result =
            await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _ = result.Code.Should().Be("orchestration_lease_held");
        _ = (await _harness.CountSliceJobsAsync(fixture.OrchestrationId)).Should().Be(0);
    }

    [Fact(DisplayName = "The durable status document never exposes a path, host or credential after failure")]
    public async Task GetStatusAsync_ReturnsRedactedFailedDocument()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-redaction");
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        CalibrationApiResult<CalibrationOrchestrationStatusDto> status =
            await _harness.CreateSaga().GetStatusAsync(
                fixture.OrchestrationId,
                fixture.Owner,
                CancellationToken.None);

        string serialized = JsonSerializer.Serialize(status.Value);
        _ = status.StatusCode.Should().Be(StatusCodes.Status200OK);
        _ = status.Value!.Status.Should().Be(nameof(CalibrationOrchestrationStatus.Failed));
        _ = status.Value.LastErrorCode.Should().Be(CalibrationGenerationProblemCodes.ContextIdentityMissing);
        _ = serialized.Should().NotContain(_harness.ArtifactRoot);
        _ = serialized.Should().NotContain(_harness.GcodeRoot);
        _ = serialized.Should().NotContain(_harness.ModelRoot);
        _ = serialized.Should().NotContain("registry-issued-worker-key");
        _ = serialized.Should().NotContain("private-worker.internal");
    }

    [Fact(DisplayName = "A foreign caller cannot read another owner's orchestration status")]
    public async Task GetStatusAsync_ForForeignOwner_Returns404()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();

        CalibrationApiResult<CalibrationOrchestrationStatusDto> status =
            await _harness.CreateSaga().GetStatusAsync(
                fixture.OrchestrationId,
                new CalibrationActor(Guid.NewGuid(), "intruder", false),
                CancellationToken.None);

        _ = status.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact(DisplayName = "Cancellation is allowed only before the run owns work in another context")]
    public async Task CancelAsync_BeforeSubmission_Cancels()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-cancel");

        CalibrationApiResult<CalibrationOrchestrationStatusDto> cancelled =
            await _harness.CreateSaga().CancelAsync(
                fixture.OrchestrationId,
                fixture.Owner,
                CancellationToken.None);

        _ = cancelled.StatusCode.Should().Be(StatusCodes.Status200OK, cancelled.Code);
        _ = cancelled.Value!.Status.Should().Be(nameof(CalibrationOrchestrationStatus.Cancelled));
    }

    [Fact(DisplayName = "Cancellation is refused once the orchestration has already failed terminally")]
    public async Task CancelAsync_AfterTerminalFailure_Returns409()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-cancel-late");
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        CalibrationApiResult<CalibrationOrchestrationStatusDto> cancelled =
            await _harness.CreateSaga().CancelAsync(
                fixture.OrchestrationId,
                fixture.Owner,
                CancellationToken.None);

        _ = cancelled.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _ = cancelled.Code.Should().Be("cancellation_not_permitted");
    }

    [Fact(DisplayName = "An accepted run records durable acceptance and failure attempt events")]
    public async Task ResumeAsync_OnTerminalFailure_WritesAcceptedAndFailedAttemptEvents()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-events");
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        IReadOnlyList<CalibrationAttemptEvent> events = await _harness.ListAttemptEventsAsync(fixture.AttemptId);

        _ = events.Select(@event => @event.EventType).Should().Contain([
            "generation-accepted",
            "generation-failed",
        ]);
        _ = events.Should().OnlyContain(@event => @event.CalibrationOrchestrationId == fixture.OrchestrationId);
        _ = events.Should().Contain(@event => @event.ErrorCode == CalibrationGenerationProblemCodes.ContextIdentityMissing);
        _ = (await _harness.CountChangesAsync(fixture.ProjectId)).Should().BeGreaterThan(0);
    }

    private async Task AcceptAsync(
        CalibrationGenerationFixture fixture,
        string operationId,
        CalibrationGenerationHarnessOptions? options = null)
    {
        CalibrationApiResult<CalibrationOrchestrationStatusDto> accepted =
            await _harness.CreateSaga(options).CreateOrResumeAsync(
                fixture.ProjectId,
                fixture.AttemptId,
                operationId,
                fixture.Request(),
                fixture.Owner,
                CancellationToken.None);
        _ = accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted, accepted.Code);
    }
}
