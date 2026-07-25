using System.Text;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Slicer.Module.Domain;
using Farm.Web.Api.Contracts;
using Farm.Web.Api.Services.Calibration;
using Farm.Web.Api.Services.Calibration.Generation;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Farm.Web.Api.Tests.Calibration.Generation;

/// <summary>
/// Covers the durable calibration generation saga: acceptance semantics, idempotency, restart
/// recovery at every checkpoint, unknown-outcome reconciliation and lineage propagation.
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

    [Fact(DisplayName = "A specification that no longer recompiles is a durable terminal failure")]
    public async Task ResumeAsync_WithChangedSpecification_FailsTerminally()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync(tamperSpecification: true);
        _ = await _harness.AddAttestedWorkerAsync();
        _ = await _harness.CreateSaga().CreateOrResumeAsync(
            fixture.ProjectId,
            fixture.AttemptId,
            "generate-tampered",
            fixture.Request(),
            fixture.Owner,
            CancellationToken.None);

        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        CalibrationOrchestration orchestration = await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = orchestration.Status.Should().Be(CalibrationOrchestrationStatus.Failed);
        _ = orchestration.LastErrorCode.Should()
            .Be(CalibrationGenerationProblemCodes.SpecificationHashMismatch);
        _ = (await _harness.CountSliceJobsAsync(fixture.OrchestrationId)).Should().Be(0);
    }

    [Fact(DisplayName = "The submitted slice job carries the full calibration and profile lineage")]
    public async Task ResumeAsync_SubmitsSliceJobWithCompleteLineage()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-lineage");

        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        SliceJob job = (await _harness.FindSliceJobAsync(fixture.OrchestrationId))!;
        _ = job.CalibrationProjectId.Should().Be(fixture.ProjectId);
        _ = job.CalibrationAttemptId.Should().Be(fixture.AttemptId);
        _ = job.CalibrationOrchestrationId.Should().Be(fixture.OrchestrationId);
        _ = job.SlicerEngineName.Should().Be("OrcaSlicer");
        _ = job.SlicerDistribution.Should().Be(CalibrationContractConstants.SlicerDistribution);
        _ = job.SlicerVersion.Should().Be(CalibrationContractConstants.SlicerVersion);
        _ = job.SlicerContainerDigest.Should().Be(CalibrationGenerationHarness.ContainerDigest);
        _ = job.Checksum.Should().Be(fixture.Specification.Sha256);
        _ = job.CorrelationId.Should().NotBeNull();
        _ = job.MachineProfileJson.Should().NotBeNullOrWhiteSpace();
        _ = job.ProcessProfileJson.Should().NotBeNullOrWhiteSpace();
        _ = job.FilamentProfileJson.Should().NotBeNullOrWhiteSpace();
        _ = job.MachineProfileSha256.Should().NotBeNullOrWhiteSpace();
        _ = job.Model3DId.Should().NotBeNull();
        _ = job.ModelFileUrl.Should().Be($"/api/slice/{job.Id}/model");
        _ = job.ModelFileUrl.Should().NotContain(_harness.ModelRoot);
    }

    [Fact(DisplayName = "A run reaches completion and promotes a verified, annotated artifact")]
    public async Task ResumeAsync_CompletesAndPromotesVerifiedArtifact()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        Guid workerId = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-complete");

        CalibrationOrchestration orchestration = await RunToCompletionAsync(fixture, workerId);

        _ = orchestration.Status.Should().Be(CalibrationOrchestrationStatus.Completed);
        _ = orchestration.CurrentStep.Should().Be(CalibrationGenerationSteps.Completed);
        _ = orchestration.GcodeFileId.Should().NotBeNull();
        _ = orchestration.SpecificationSha256.Should().Be(fixture.Specification.Sha256);
        _ = orchestration.PlanManifestSha256.Should().NotBeNullOrWhiteSpace();
        _ = orchestration.GcodeSha256.Should().NotBeNullOrWhiteSpace();
        _ = orchestration.ManifestSha256.Should().NotBeNullOrWhiteSpace();
        _ = orchestration.GeneratorVersion.Should().Be(CalibrationGeneratorIdentity.Current.Version);
        _ = orchestration.SlicerContainerDigest.Should().Be(CalibrationGenerationHarness.ContainerDigest);
        _ = orchestration.SlicerBinarySha256.Should().Be(CalibrationGenerationHarness.BinaryDigest);
        _ = orchestration.WorkerId.Should().Be(workerId);
    }

    [Fact(DisplayName = "The promoted library file carries the calibration lineage and pinned identity")]
    public async Task ResumeAsync_PromotesGcodeFileWithLineage()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        Guid workerId = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-promoted-lineage");

        CalibrationOrchestration orchestration = await RunToCompletionAsync(fixture, workerId);

        GcodeFile promoted = await _harness.GetGcodeFileAsync(orchestration.GcodeFileId!.Value);
        _ = promoted.CalibrationProjectId.Should().Be(fixture.ProjectId);
        _ = promoted.CalibrationAttemptId.Should().Be(fixture.AttemptId);
        _ = promoted.CalibrationOrchestrationId.Should().Be(fixture.OrchestrationId);
        _ = promoted.SpecificationSha256.Should().Be(fixture.Specification.Sha256);
        _ = promoted.PinnedSlicerVersion.Should().Be(CalibrationContractConstants.SlicerVersion);
        _ = promoted.SlicerContainerDigest.Should().Be(CalibrationGenerationHarness.ContainerDigest);
        _ = promoted.ContentSha256.Should().Be(orchestration.GcodeSha256!.ToUpperInvariant());
        _ = promoted.IsImmutable.Should().BeTrue();
    }

    [Theory(DisplayName = "A restart at any checkpoint resumes without duplicating a side effect")]
    [InlineData(CalibrationGenerationSteps.ValidatingContext)]
    [InlineData(CalibrationGenerationSteps.ResolvingModel)]
    [InlineData(CalibrationGenerationSteps.CompilingPlan)]
    [InlineData(CalibrationGenerationSteps.AwaitingWorker)]
    [InlineData(CalibrationGenerationSteps.VerifyingArtifact)]
    [InlineData(CalibrationGenerationSteps.ComposingGcode)]
    [InlineData(CalibrationGenerationSteps.Promoting)]
    public async Task ResumeAsync_AfterRestartAtCheckpoint_CompletesExactlyOnce(string step)
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        Guid workerId = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, $"generate-restart-{step}");
        CalibrationOrchestration completed = await RunToCompletionAsync(fixture, workerId);
        Guid promotedFileId = completed.GcodeFileId!.Value;
        int artifactCount = (await _harness.ListArtifactsAsync(fixture.OrchestrationId)).Count;

        await _harness.RewindToStepAsync(fixture.OrchestrationId, step);
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        CalibrationOrchestration resumed = await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = resumed.Status.Should().Be(CalibrationOrchestrationStatus.Completed);
        _ = resumed.GcodeFileId.Should().Be(promotedFileId);
        _ = (await _harness.CountSliceJobsAsync(fixture.OrchestrationId)).Should().Be(1);
        _ = (await _harness.ListArtifactsAsync(fixture.OrchestrationId)).Count.Should().Be(artifactCount);
        _ = (await _harness.CountGcodeFilesAsync()).Should().Be(1);
    }

    [Fact(DisplayName = "An unknown submit outcome is reconciled from the correlated job, not resubmitted")]
    public async Task ResumeAsync_WithUnknownSubmitOutcome_AdoptsCorrelatedJob()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-unknown-submit");
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);
        Guid submittedJobId = (await _harness.FindSliceJobAsync(fixture.OrchestrationId))!.Id;

        // Reproduces a crash between the durable submission and its checkpoint.
        await _harness.MutateOrchestrationAsync(fixture.OrchestrationId, orchestration =>
        {
            orchestration.SliceJobId = null;
            orchestration.CurrentStep = CalibrationGenerationSteps.SubmittingSliceJob;
            orchestration.LeaseOwner = null;
            orchestration.LeaseExpiresAtUtc = null;
        });
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        CalibrationOrchestration orchestration = await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = orchestration.SliceJobId.Should().Be(submittedJobId);
        _ = (await _harness.CountSliceJobsAsync(fixture.OrchestrationId)).Should().Be(1);
    }

    [Fact(DisplayName = "An unknown artifact upload outcome is reconciled by digest, not re-uploaded")]
    public async Task ResumeAsync_WithUnknownUploadOutcome_ReusesExistingArtifact()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        Guid workerId = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-unknown-upload");
        CalibrationOrchestration completed = await RunToCompletionAsync(fixture, workerId);
        Guid finalArtifactId = completed.FinalArtifactId!.Value;
        int artifactCount = (await _harness.ListArtifactsAsync(fixture.OrchestrationId)).Count;

        await _harness.MutateOrchestrationAsync(fixture.OrchestrationId, orchestration =>
        {
            orchestration.FinalArtifactId = null;
            orchestration.Status = CalibrationOrchestrationStatus.Running;
            orchestration.CurrentStep = CalibrationGenerationSteps.ComposingGcode;
            orchestration.CompletedAtUtc = null;
            orchestration.LeaseOwner = null;
            orchestration.LeaseExpiresAtUtc = null;
        });
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        CalibrationOrchestration resumed = await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = resumed.FinalArtifactId.Should().Be(finalArtifactId);
        _ = (await _harness.ListArtifactsAsync(fixture.OrchestrationId)).Count.Should().Be(artifactCount);
    }

    [Fact(DisplayName = "An unknown promotion outcome replays the promotion instead of promoting twice")]
    public async Task ResumeAsync_WithUnknownPromotionOutcome_ReplaysSamePromotion()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        Guid workerId = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-unknown-promotion");
        CalibrationOrchestration completed = await RunToCompletionAsync(fixture, workerId);
        Guid promotedFileId = completed.GcodeFileId!.Value;

        await _harness.MutateOrchestrationAsync(fixture.OrchestrationId, orchestration =>
        {
            orchestration.GcodeFileId = null;
            orchestration.Status = CalibrationOrchestrationStatus.Running;
            orchestration.CurrentStep = CalibrationGenerationSteps.Promoting;
            orchestration.CompletedAtUtc = null;
            orchestration.LeaseOwner = null;
            orchestration.LeaseExpiresAtUtc = null;
        });
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        CalibrationOrchestration resumed = await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = resumed.GcodeFileId.Should().Be(promotedFileId);
        _ = (await _harness.CountGcodeFilesAsync()).Should().Be(1);
    }

    [Fact(DisplayName = "A worker that never reports keeps the run waiting rather than failing it")]
    public async Task ResumeAsync_WhileWorkerHasNotReported_StaysRecoverable()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-waiting");

        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        CalibrationOrchestration orchestration = await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = orchestration.CurrentStep.Should().Be(CalibrationGenerationSteps.AwaitingWorker);
        _ = orchestration.Status.Should().Be(CalibrationOrchestrationStatus.Running);
        _ = orchestration.NextRetryAtUtc.Should().NotBeNull();
        _ = (await _harness.CountSliceJobsAsync(fixture.OrchestrationId)).Should().Be(1);
    }

    [Fact(DisplayName = "A failed worker job becomes a durable, inspectable terminal failure")]
    public async Task ResumeAsync_WithFailedWorkerJob_FailsDurably()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        Guid workerId = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-worker-failure");
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);
        _ = await _harness.CompleteWorkerJobAsync(
            fixture.OrchestrationId,
            workerId,
            status: SliceJobStatus.Failed);

        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        CalibrationOrchestration orchestration = await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = orchestration.Status.Should().Be(CalibrationOrchestrationStatus.Failed);
        _ = orchestration.LastErrorCode.Should().Be(CalibrationGenerationProblemCodes.SliceJobFailed);
        _ = orchestration.LastErrorJson.Should().NotBeNullOrWhiteSpace();
        _ = (await _harness.CountGcodeFilesAsync()).Should().Be(0);
    }

    [Fact(DisplayName = "A completed job without a worker artifact fails instead of promoting nothing")]
    public async Task ResumeAsync_WithoutWorkerArtifact_FailsDurably()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        Guid workerId = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-missing-artifact");
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);
        _ = await _harness.CompleteWorkerJobAsync(fixture.OrchestrationId, workerId, produceArtifact: false);

        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        CalibrationOrchestration orchestration = await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = orchestration.Status.Should().Be(CalibrationOrchestrationStatus.Failed);
        _ = orchestration.LastErrorCode.Should().Be(CalibrationGenerationProblemCodes.SliceArtifactMissing);
    }

    [Fact(DisplayName = "A worker artifact whose bytes drifted from its digest is refused")]
    public async Task ResumeAsync_WithTamperedWorkerArtifact_FailsDurably()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        Guid workerId = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-tampered-artifact");
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);
        Guid artifactId = (await _harness.CompleteWorkerJobAsync(fixture.OrchestrationId, workerId))!.Value;
        await TamperArtifactBytesAsync(artifactId);

        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        CalibrationOrchestration orchestration = await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = orchestration.Status.Should().Be(CalibrationOrchestrationStatus.Failed);
        _ = orchestration.LastErrorCode.Should()
            .Be(CalibrationGenerationProblemCodes.SliceArtifactUnverifiable);
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

    [Fact(DisplayName = "The recovery pass resumes a due run and advances it")]
    public async Task RecoverDueAsync_ResumesDueOrchestration()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-recovery");

        int advanced = await _harness.CreateSaga().RecoverDueAsync(10, CancellationToken.None);

        _ = advanced.Should().Be(1);
        _ = (await _harness.FindSliceJobAsync(fixture.OrchestrationId)).Should().NotBeNull();
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

    [Fact(DisplayName = "Every durable step is journaled as an attempt event")]
    public async Task ResumeAsync_WritesAttemptEventsForEveryDurableStep()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        Guid workerId = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-events");
        _ = await RunToCompletionAsync(fixture, workerId);

        IReadOnlyList<CalibrationAttemptEvent> events =
            await _harness.ListAttemptEventsAsync(fixture.AttemptId);

        _ = events.Select(@event => @event.EventType).Should().Contain(
        [
            "generation-accepted",
            "slice-job-submitted",
            "slice-artifact-verified",
            "gcode-annotated",
            "generation-completed",
        ]);
        _ = events.Should().OnlyContain(@event => @event.CalibrationOrchestrationId == fixture.OrchestrationId);
        _ = (await _harness.CountChangesAsync(fixture.ProjectId)).Should().BeGreaterThan(0);
    }

    [Fact(DisplayName = "The durable status document never exposes a path, host or credential")]
    public async Task GetStatusAsync_ReturnsRedactedDocument()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        Guid workerId = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-redaction");
        _ = await RunToCompletionAsync(fixture, workerId);

        CalibrationApiResult<CalibrationOrchestrationStatusDto> status =
            await _harness.CreateSaga().GetStatusAsync(
                fixture.OrchestrationId,
                fixture.Owner,
                CancellationToken.None);

        string serialized = System.Text.Json.JsonSerializer.Serialize(status.Value);
        _ = status.StatusCode.Should().Be(StatusCodes.Status200OK);
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

    [Fact(DisplayName = "Cancellation is refused once a slice job exists in the slicer context")]
    public async Task CancelAsync_AfterSubmission_Returns409()
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

    [Fact(DisplayName = "Unreadable final artifact bytes schedule a retry and recover when storage returns")]
    public async Task ResumeAsync_WithUnreadableFinalArtifact_RetriesThenRecovers()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        Guid workerId = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-artifact-transient");
        CalibrationOrchestration completed = await RunToCompletionAsync(fixture, workerId);
        Guid finalArtifactId = completed.FinalArtifactId!.Value;
        Guid promotedFileId = completed.GcodeFileId!.Value;
        byte[] storedBytes = await ReadArtifactBytesAsync(finalArtifactId);
        await RewindToPromotingAsync(fixture.OrchestrationId);
        DeleteArtifactBytes(await ResolveArtifactPathAsync(finalArtifactId));

        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        CalibrationOrchestration waiting = await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = waiting.Status.Should().Be(CalibrationOrchestrationStatus.WaitingToRetry);
        _ = waiting.LastErrorCode.Should().Be(CalibrationGenerationProblemCodes.PromotionUnavailable);
        _ = waiting.NextRetryAtUtc.Should().NotBeNull();

        await WriteArtifactBytesAsync(finalArtifactId, storedBytes);
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        CalibrationOrchestration recovered = await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = recovered.Status.Should().Be(CalibrationOrchestrationStatus.Completed);
        _ = recovered.GcodeFileId.Should().Be(promotedFileId);
        _ = (await _harness.CountGcodeFilesAsync()).Should().Be(1);
    }

    [Fact(DisplayName = "A final artifact that really is empty is a terminal malformed program")]
    public async Task ResumeAsync_WithEmptyFinalArtifact_FailsTerminally()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        Guid workerId = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-artifact-empty");
        CalibrationOrchestration completed = await RunToCompletionAsync(fixture, workerId);
        await RewindToPromotingAsync(fixture.OrchestrationId);
        await WriteArtifactBytesAsync(completed.FinalArtifactId!.Value, []);

        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        CalibrationOrchestration failed = await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = failed.Status.Should().Be(CalibrationOrchestrationStatus.Failed);
        _ = failed.LastErrorCode.Should().Be(CalibrationGenerationProblemCodes.GcodeMalformed);
    }

    private async Task AcceptAsync(CalibrationGenerationFixture fixture, string operationId)
    {
        CalibrationApiResult<CalibrationOrchestrationStatusDto> accepted =
            await _harness.CreateSaga().CreateOrResumeAsync(
                fixture.ProjectId,
                fixture.AttemptId,
                operationId,
                fixture.Request(),
                fixture.Owner,
                CancellationToken.None);
        _ = accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted, accepted.Code);
    }

    private async Task<CalibrationOrchestration> RunToCompletionAsync(
        CalibrationGenerationFixture fixture,
        Guid workerId)
    {
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);
        _ = await _harness.CompleteWorkerJobAsync(fixture.OrchestrationId, workerId);
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);
        return await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
    }

    private async Task TamperArtifactBytesAsync(Guid artifactId)
    {
        await using Farm.Slicer.Module.Data.SlicerDbContext slicer = _harness.CreateSlicerContext();
        Artifact artifact = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .SingleAsync(slicer.Artifacts, candidate => candidate.Id == artifactId);
        await File.WriteAllBytesAsync(
            Path.Combine(_harness.ArtifactRoot, artifact.RelativePath),
            Encoding.UTF8.GetBytes(";tampered\nG28\n"));
    }

    /// <summary>Resolves the stored bytes path of an artifact.</summary>
    /// <param name="artifactId">The artifact identity.</param>
    /// <returns>The absolute path of the stored bytes.</returns>
    private async Task<string> ResolveArtifactPathAsync(Guid artifactId)
    {
        await using Farm.Slicer.Module.Data.SlicerDbContext slicer = _harness.CreateSlicerContext();
        Artifact artifact = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .SingleAsync(slicer.Artifacts, candidate => candidate.Id == artifactId);
        return Path.Combine(_harness.ArtifactRoot, artifact.RelativePath);
    }

    private async Task<byte[]> ReadArtifactBytesAsync(Guid artifactId) =>
        await File.ReadAllBytesAsync(await ResolveArtifactPathAsync(artifactId));

    private async Task WriteArtifactBytesAsync(Guid artifactId, byte[] content) =>
        await File.WriteAllBytesAsync(await ResolveArtifactPathAsync(artifactId), content);

    private static void DeleteArtifactBytes(string path) => File.Delete(path);

    /// <summary>Reproduces an interrupted run that still owes its promotion.</summary>
    /// <param name="orchestrationId">The orchestration identity.</param>
    /// <returns>A task that completes when the durable row is rewritten.</returns>
    private Task RewindToPromotingAsync(Guid orchestrationId) =>
        _harness.MutateOrchestrationAsync(orchestrationId, orchestration =>
        {
            orchestration.GcodeFileId = null;
            orchestration.Status = CalibrationOrchestrationStatus.Running;
            orchestration.CurrentStep = CalibrationGenerationSteps.Promoting;
            orchestration.CompletedAtUtc = null;
            orchestration.NextRetryAtUtc = null;
            orchestration.RetryCount = 0;
            orchestration.LeaseOwner = null;
            orchestration.LeaseExpiresAtUtc = null;
        });
}
