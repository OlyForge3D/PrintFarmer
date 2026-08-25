using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Web.Api.Contracts;
using Farm.Web.Api.Services.Calibration;
using Farm.Web.Api.Services.Calibration.Generation;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;

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

    [Fact(DisplayName =
        "An attempt with no printer-configuration snapshot fails explicitly with a known-limitation " +
        "code (#1990), not a dead lookup masquerading as a missing record")]
    public async Task ResumeAsync_WithoutSnapshotId_FailsWithCompatibilitySnapshotUnavailable()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();
        _ = await _harness.CreateSaga().CreateOrResumeAsync(
            fixture.ProjectId,
            fixture.AttemptId,
            "generate-no-snapshot",
            fixture.Request(),
            fixture.Owner,
            CancellationToken.None);

        // Simulate the D4 regression (#1990): CreateAttemptAsync unconditionally sets
        // PrinterConfigurationSnapshotId = null for every new attempt today. Use raw SQL
        // rather than an EF-tracked update: CalibrationAttempt rows are immutable once
        // persisted (AppDbContext.EnsureCalibrationHistoryIsImmutable), which is exactly
        // the invariant a real D4-created attempt never violates — it is born this way.
        await using (Farm.Infrastructure.Data.AppDbContext core = _harness.CreateCoreContext())
        {
            _ = await core.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE CalibrationAttempts SET PrinterConfigurationSnapshotId = NULL WHERE Id = {fixture.AttemptId}");
        }

        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        CalibrationOrchestration orchestration = await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = orchestration.Status.Should().Be(CalibrationOrchestrationStatus.Failed);
        _ = orchestration.LastErrorCode.Should()
            .Be(CalibrationGenerationProblemCodes.CompatibilitySnapshotUnavailable);
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

        // The worker receives the effective documents and the digests of those documents, never the
        // untouched upstream baselines the immutable snapshot keeps as provenance.
        OrcaEffectiveProfileDocument machine =
            OrcaEffectiveProfileFactory.Derive(CalibrationGenerationSeed.MachineProfileJson);
        OrcaEffectiveProfileDocument process =
            OrcaEffectiveProfileFactory.Derive(CalibrationGenerationSeed.ProcessProfileJson);
        OrcaEffectiveProfileDocument filament =
            OrcaEffectiveProfileFactory.Derive(CalibrationGenerationSeed.FilamentProfileJson);
        _ = job.MachineProfileJson.Should().Be(machine.Json);
        _ = job.ProcessProfileJson.Should().Be(process.Json);
        _ = job.FilamentProfileJson.Should().Be(filament.Json);
        _ = job.MachineProfileSha256.Should().Be(machine.Sha256);
        _ = job.ProcessProfileSha256.Should().Be(process.Sha256);
        _ = job.FilamentProfileSha256.Should().Be(filament.Sha256);
        _ = job.Model3DId.Should().NotBeNull();
        _ = job.ModelFileUrl.Should().Be($"/api/slice/{job.Id}/model");
        _ = job.ModelFileUrl.Should().NotContain(_harness.ModelRoot);
    }

    [Fact(DisplayName = "A configured non-default slicer worker can claim its pinned calibration job")]
    public async Task ResumeAsync_WithConfiguredNonDefaultSlicerVersion_SubmitsClaimableJob()
    {
        const string supportedNonDefaultVersion = "2.5.0";
        Guid workerId = await _harness.AddAttestedWorkerAsync(version: supportedNonDefaultVersion);
        CalibrationPinnedSlicerIdentity pinned = new(
            supportedNonDefaultVersion,
            CalibrationContractConstants.SlicerDistribution,
            CalibrationGenerationHarness.ContainerDigest,
            CalibrationGenerationHarness.BinaryDigest,
            workerId);
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync(pinnedIdentity: pinned);
        CalibrationGenerationHarnessOptions options = new()
        {
            SupportedSlicerVersions =
            [
                CalibrationContractConstants.SlicerVersion,
                supportedNonDefaultVersion,
            ],
        };
        await AcceptAsync(fixture, "generate-non-default-version", options);

        _ = await _harness.CreateSaga(options)
            .ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        SliceJob? submitted = await _harness.FindSliceJobAsync(fixture.OrchestrationId);
        SliceJob? claimed = await _harness.ClaimNextSliceJobAsync(workerId);
        _ = submitted.Should().NotBeNull();
        _ = submitted!.SlicerVersion.Should().Be(supportedNonDefaultVersion);
        _ = submitted.PinnedWorkerId.Should().Be(workerId);
        _ = claimed.Should().NotBeNull();
        _ = claimed!.Id.Should().Be(submitted.Id);
    }

    [Fact(DisplayName = "An upstream profile's command fields are neutralized before the worker sees them")]
    public async Task ResumeAsync_WithUpstreamCommandFields_DeliversNeutralizedProfilesOnly()
    {
        const string machineJson =
            """{"name":"Upstream Machine","nozzle_diameter":["0.4"],"printable_area":["0x0","235x0","235x235","0x235"],"machine_start_gcode":"G28 ; home\nM104 S200","machine_end_gcode":"M104 S0","printer_notes":"PRINTER_MODEL_UPSTREAM","post_process":[]}""";
        const string processJson =
            """{"name":"Upstream Process","layer_height":"0.2","line_width":"0.45","wall_loops":"2","before_layer_change_gcode":";BEFORE_LAYER_CHANGE","layer_change_gcode":";AFTER_LAYER_CHANGE"}""";
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync(
            profiles: new CalibrationGenerationSeed.ProfileSet(
                machineJson,
                processJson,
                CalibrationGenerationSeed.FilamentProfileJson,
                0.4));
        _ = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-neutralized");

        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        SliceJob job = (await _harness.FindSliceJobAsync(fixture.OrchestrationId))!;
        _ = job.MachineProfileJson.Should().NotContain("G28");
        _ = job.MachineProfileJson.Should().NotContain("M104");
        _ = job.MachineProfileJson.Should().NotContain("PRINTER_MODEL_UPSTREAM");
        _ = job.MachineProfileJson.Should().Contain("\"nozzle_diameter\":[\"0.4\"]");
        _ = job.ProcessProfileJson.Should().NotContain("LAYER_CHANGE");
        _ = job.ProcessProfileJson.Should().Contain("\"layer_height\":\"0.2\"");

        OrcaEffectiveProfileDocument machine = OrcaEffectiveProfileFactory.Derive(machineJson);
        _ = machine.NeutralizedKeys.Should().Equal(
            "machine_end_gcode",
            "machine_start_gcode",
            "post_process",
            "printer_notes");
        _ = job.MachineProfileJson.Should().Be(machine.Json);
        _ = job.MachineProfileSha256.Should().Be(machine.Sha256);

        // The immutable snapshot still holds the untouched upstream baseline and its digest.
        await using Farm.Infrastructure.Data.AppDbContext core = _harness.CreateCoreContext();
        PrinterConfigurationSnapshot snapshot = await Microsoft.EntityFrameworkCore
            .EntityFrameworkQueryableExtensions
            .SingleAsync(core.PrinterConfigurationSnapshots, row => row.AttemptId == fixture.AttemptId);
        _ = snapshot.ExactMachineProfileJson.Should().Be(machineJson);
        _ = snapshot.MachineProfileSha256.Should()
            .Be(CalibrationCanonicalJson.ComputeTextSha256(machineJson));
        _ = snapshot.MachineProfileSha256.Should().NotBe(job.MachineProfileSha256);
    }

    [Fact(DisplayName = "A run accepted under a superseded plan manifest schema resumes under it")]
    public async Task ResumeAsync_WithSupersededPlanManifestSchemaCheckpoint_CompletesWithoutFailing()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        Guid workerId = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-superseded-schema");

        CapturingPlanCompiler capture = new();
        _ = await _harness
            .CreateSaga(new CalibrationGenerationHarnessOptions { PlanCompiler = capture })
            .ResumeAsync(fixture.OrchestrationId, CancellationToken.None);
        OrcaCalibrationPlan plan = capture.Compiled!;

        // Reproduces a run that a build writing the 1.0 plan manifest accepted and submitted: only
        // the durable digest differs, because only the way the manifest is written down changed.
        string legacyDigest = CalibrationCanonicalJson.ComputeTextSha256(
            OrcaCalibrationPlanManifestSchema.Serialize(
                plan.Manifest,
                OrcaCalibrationPlanManifestSchema.SingleProfileDigest));
        _ = legacyDigest.Should().NotBe(plan.ManifestSha256);
        await _harness.MutateOrchestrationAsync(
            fixture.OrchestrationId,
            orchestration => orchestration.PlanManifestSha256 = legacyDigest);

        _ = await _harness.CompleteWorkerJobAsync(fixture.OrchestrationId, workerId);
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        CalibrationOrchestration completed =
            await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = completed.Status.Should().Be(CalibrationOrchestrationStatus.Completed);
        _ = completed.LastErrorCode.Should().BeNull();
        _ = completed.GcodeFileId.Should().NotBeNull();

        // The trusted upgrade rewrites nothing durable, and the promoted program still names the
        // plan digest this run was accepted with.
        _ = completed.PlanManifestSha256.Should().Be(legacyDigest);
        string promoted = Encoding.UTF8.GetString(
            await ReadArtifactBytesAsync(completed.FinalArtifactId!.Value));
        _ = promoted.Should().Contain($"planManifestSha256={legacyDigest}");
        _ = promoted.Should().NotContain(plan.ManifestSha256);
        _ = (await _harness.CountSliceJobsAsync(fixture.OrchestrationId)).Should().Be(1);
        _ = (await _harness.CountGcodeFilesAsync()).Should().Be(1);
    }

    [Fact(DisplayName = "A resume after a superseded-schema restart is still exactly once")]
    public async Task ResumeAsync_AfterSupersededSchemaRestart_DoesNotDuplicateAnyEffect()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        Guid workerId = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-superseded-restart");

        CapturingPlanCompiler capture = new();
        _ = await _harness
            .CreateSaga(new CalibrationGenerationHarnessOptions { PlanCompiler = capture })
            .ResumeAsync(fixture.OrchestrationId, CancellationToken.None);
        string legacyDigest = CalibrationCanonicalJson.ComputeTextSha256(
            OrcaCalibrationPlanManifestSchema.Serialize(
                capture.Compiled!.Manifest,
                OrcaCalibrationPlanManifestSchema.SingleProfileDigest));
        await _harness.MutateOrchestrationAsync(
            fixture.OrchestrationId,
            orchestration => orchestration.PlanManifestSha256 = legacyDigest);
        _ = await _harness.CompleteWorkerJobAsync(fixture.OrchestrationId, workerId);
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        Guid promotedFileId =
            (await _harness.GetOrchestrationAsync(fixture.OrchestrationId)).GcodeFileId!.Value;
        int artifactCount = (await _harness.ListArtifactsAsync(fixture.OrchestrationId)).Count;

        await _harness.RewindToStepAsync(fixture.OrchestrationId, CalibrationGenerationSteps.ComposingGcode);
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        CalibrationOrchestration resumed =
            await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = resumed.Status.Should().Be(CalibrationOrchestrationStatus.Completed);
        _ = resumed.GcodeFileId.Should().Be(promotedFileId);
        _ = resumed.PlanManifestSha256.Should().Be(legacyDigest);
        _ = (await _harness.ListArtifactsAsync(fixture.OrchestrationId)).Count.Should().Be(artifactCount);
        _ = (await _harness.CountGcodeFilesAsync()).Should().Be(1);
    }

    [Fact(DisplayName = "A plan digest no schema explains is still a terminal mismatch")]
    public async Task ResumeAsync_WithUnexplainedPlanManifestDigest_FailsDurably()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-plan-drift");
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        await _harness.MutateOrchestrationAsync(
            fixture.OrchestrationId,
            orchestration => orchestration.PlanManifestSha256 = new string('a', 64));

        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        CalibrationOrchestration orchestration =
            await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = orchestration.Status.Should().Be(CalibrationOrchestrationStatus.Failed);
        _ = orchestration.LastErrorCode.Should()
            .Be(CalibrationGenerationProblemCodes.PlanModelMismatch);
        _ = (await _harness.CountGcodeFilesAsync()).Should().Be(0);
    }

    [Fact(DisplayName = "An unattributed hash-matched model is not adopted; a new owned model is stored instead")]
    public async Task ResumeAsync_WithUnattributedHashMatch_StoresNewOwnedModelInstead()
    {
        Guid owner = Guid.NewGuid();
        _ = await _harness.AddAttestedWorkerAsync();

        // First run stores the deterministic generated body, owned by the project owner.
        CalibrationGenerationFixture first = await _harness.SeedAttemptAsync(ownerId: owner);
        await AcceptAsync(first, "generate-hash-first");
        _ = await _harness.CreateSaga().ResumeAsync(first.OrchestrationId, CancellationToken.None);
        SliceJob firstJob = (await _harness.FindSliceJobAsync(first.OrchestrationId))!;
        Guid unattributedModelId = firstJob.Model3DId!.Value;

        // Simulate a pre-existing row whose uploader was never recorded (e.g. legacy data).
        await using (Farm.Slicer.Module.Data.SlicerDbContext slicer = _harness.CreateSlicerContext())
        {
            Model3D unattributed = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .SingleAsync(slicer.Models3D, model => model.Id == unattributedModelId);
            unattributed.UploadedByUserId = null;
            _ = await slicer.SaveChangesAsync();
        }

        // Second run uses the same method/options, so it recomputes the identical content hash.
        CalibrationGenerationFixture second = await _harness.SeedAttemptAsync(ownerId: owner);
        await AcceptAsync(second, "generate-hash-second");
        _ = await _harness.CreateSaga().ResumeAsync(second.OrchestrationId, CancellationToken.None);
        SliceJob secondJob = (await _harness.FindSliceJobAsync(second.OrchestrationId))!;

        _ = secondJob.Model3DId.Should().NotBeNull();
        _ = secondJob.Model3DId.Should().NotBe(unattributedModelId);

        await using Farm.Slicer.Module.Data.SlicerDbContext verify = _harness.CreateSlicerContext();
        Model3D stored = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .SingleAsync(verify.Models3D, model => model.Id == secondJob.Model3DId!.Value);
        _ = stored.UploadedByUserId.Should().Be(owner);
    }

    [Fact(DisplayName = "Concurrent owners persist identical generated geometry without orphaning bytes")]
    public async Task ResumeAsync_WithConcurrentOwners_ConvergesOnModelHashRace()
    {
        Guid firstOwner = Guid.NewGuid();
        Guid secondOwner = Guid.NewGuid();
        CalibrationGenerationFixture first = await _harness.SeedAttemptAsync(ownerId: firstOwner);
        CalibrationGenerationFixture second = await _harness.SeedAttemptAsync(ownerId: secondOwner);
        _ = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(first, "generate-concurrent-owner-first");
        await AcceptAsync(second, "generate-concurrent-owner-second");

        ModelStorageRaceGate gate = new();
        CalibrationGenerationHarnessOptions options = new()
        {
            ModelRepositoryDecorator = gate.Decorate,
        };
        ICalibrationGenerationSaga firstSaga = _harness.CreateSaga(options);
        ICalibrationGenerationSaga secondSaga = _harness.CreateSaga(options);

        _ = await Task.WhenAll(
            firstSaga.ResumeAsync(first.OrchestrationId, CancellationToken.None),
            secondSaga.ResumeAsync(second.OrchestrationId, CancellationToken.None));

        SliceJob firstJob = (await _harness.FindSliceJobAsync(first.OrchestrationId))!;
        SliceJob secondJob = (await _harness.FindSliceJobAsync(second.OrchestrationId))!;
        _ = firstJob.Model3DId.Should().NotBeNull();
        _ = secondJob.Model3DId.Should().NotBeNull();
        _ = secondJob.Model3DId.Value.Should().NotBe(firstJob.Model3DId.Value);
        _ = firstJob.ModelSha256.Should().MatchRegex("^[A-F0-9]{64}$");
        _ = secondJob.ModelSha256.Should().Be(firstJob.ModelSha256);

        await using Farm.Slicer.Module.Data.SlicerDbContext slicer = _harness.CreateSlicerContext();
        List<Model3D> stored = await slicer.Models3D.AsNoTracking().ToListAsync();
        _ = stored.Should().HaveCount(2);
        _ = stored.Select(model => model.UploadedByUserId).Should()
            .BeEquivalentTo([firstOwner, secondOwner]);
        _ = stored.Count(model => model.FileHash == firstJob.ModelSha256).Should().Be(1);

        string[] modelFiles = Directory.GetFiles(_harness.ModelRoot, "*.stl");
        _ = modelFiles.Should().HaveCount(stored.Count, "every staged file must have durable metadata");
        foreach (Model3D model in stored)
        {
            string path = Path.Join(_harness.ModelRoot, model.FileName);
            _ = File.Exists(path).Should().BeTrue();
            byte[] bytes = await File.ReadAllBytesAsync(path);
            _ = Convert.ToHexString(SHA256.HashData(bytes)).Should().Be(firstJob.ModelSha256);
        }
    }

    [Fact(DisplayName = "A restart after a cross-owner model commit reuses the durable owner model")]
    public async Task ResumeAsync_AfterModelCommitBeforeCheckpoint_ReusesOwnerModel()
    {
        Guid canonicalOwner = Guid.NewGuid();
        Guid restartingOwner = Guid.NewGuid();
        CalibrationGenerationFixture canonical = await _harness.SeedAttemptAsync(ownerId: canonicalOwner);
        CalibrationGenerationFixture restarting = await _harness.SeedAttemptAsync(ownerId: restartingOwner);
        _ = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(canonical, "generate-restart-canonical-owner");
        await AcceptAsync(restarting, "generate-restart-owner");

        _ = await _harness.CreateSaga().ResumeAsync(canonical.OrchestrationId, CancellationToken.None);
        _ = await _harness.CreateSaga().ResumeAsync(restarting.OrchestrationId, CancellationToken.None);
        SliceJob committedJob = (await _harness.FindSliceJobAsync(restarting.OrchestrationId))!;
        Guid committedModelId = committedJob.Model3DId!.Value;
        string committedDigest = committedJob.ModelSha256!;

        await using (Farm.Slicer.Module.Data.SlicerDbContext slicer = _harness.CreateSlicerContext())
        {
            SliceJob downstreamJob = await slicer.SliceJobs
                .SingleAsync(candidate => candidate.CalibrationOrchestrationId == restarting.OrchestrationId);
            _ = slicer.SliceJobs.Remove(downstreamJob);
            _ = await slicer.SaveChangesAsync();
        }

        await _harness.MutateOrchestrationAsync(restarting.OrchestrationId, orchestration =>
        {
            orchestration.Model3DId = null;
            orchestration.SliceJobId = null;
            orchestration.PlanManifestSha256 = null;
            orchestration.CurrentStep = CalibrationGenerationSteps.ResolvingModel;
            orchestration.Status = CalibrationOrchestrationStatus.Running;
            orchestration.RetryCount = 0;
            orchestration.NextRetryAtUtc = null;
            orchestration.LastErrorCode = null;
            orchestration.LastErrorJson = null;
            orchestration.LeaseOwner = null;
            orchestration.LeaseExpiresAtUtc = null;
        });

        _ = await _harness.CreateSaga().ResumeAsync(restarting.OrchestrationId, CancellationToken.None);

        SliceJob resumedJob = (await _harness.FindSliceJobAsync(restarting.OrchestrationId))!;
        _ = resumedJob.Model3DId.Should().Be(committedModelId);
        _ = resumedJob.ModelSha256.Should().Be(committedDigest);
        _ = (await _harness.CountStoredModelsAsync(restartingOwner)).Should().Be(1);
        _ = Directory.GetFiles(_harness.ModelRoot, "*.stl").Should().HaveCount(2);
        await using Farm.Slicer.Module.Data.SlicerDbContext verify = _harness.CreateSlicerContext();
        Model3D committed = await verify.Models3D.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == committedModelId);
        byte[] bytes = await File.ReadAllBytesAsync(Path.Join(_harness.ModelRoot, committed.FileName));
        _ = Convert.ToHexString(SHA256.HashData(bytes)).Should().Be(committedDigest);
    }

    [Fact(DisplayName = "A model save that throws after commit retains its durable bytes")]
    public async Task ResumeAsync_WhenModelSaveThrowsAfterCommit_ReconcilesDurableModel()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-commit-unknown");
        ModelSaveCommitUnknownGate gate = new();
        CalibrationGenerationHarnessOptions options = new()
        {
            ModelRepositoryDecorator = gate.Decorate,
        };

        _ = await _harness.CreateSaga(options)
            .ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        SliceJob job = (await _harness.FindSliceJobAsync(fixture.OrchestrationId))!;
        _ = job.Model3DId.Should().NotBeNull();
        _ = job.ModelSha256.Should().MatchRegex("^[A-F0-9]{64}$");
        await using Farm.Slicer.Module.Data.SlicerDbContext slicer = _harness.CreateSlicerContext();
        Model3D model = await slicer.Models3D.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == job.Model3DId);
        string storedPath = Path.Join(_harness.ModelRoot, model.FileName);
        _ = File.Exists(storedPath).Should().BeTrue();
        byte[] storedBytes = await File.ReadAllBytesAsync(storedPath);
        _ = Convert.ToHexString(SHA256.HashData(storedBytes)).Should().Be(job.ModelSha256);
        _ = Directory.GetFiles(_harness.ModelRoot, "*.stl").Should().ContainSingle();
    }

    [Fact(DisplayName = "An uncertain uncommitted model save reuses its staging path after restart")]
    public async Task ResumeAsync_WhenUncommittedModelSaveIsUncertain_ReusesStagedBytes()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-uncommitted-uncertain");
        ModelSaveNoCommitUncertainGate gate = new();
        CalibrationGenerationHarnessOptions options = new()
        {
            ModelRepositoryDecorator = gate.Decorate,
        };

        Func<Task> interrupted = () => _harness.CreateSaga(options)
            .ResumeAsync(fixture.OrchestrationId, CancellationToken.None);
        _ = await interrupted.Should().ThrowAsync<DbUpdateException>();

        _ = (await _harness.CountStoredModelsAsync(fixture.Owner.UserId)).Should().Be(0);
        _ = (await _harness.FindSliceJobAsync(fixture.OrchestrationId)).Should().BeNull();
        string stagedPath = Directory.GetFiles(_harness.ModelRoot, "*.stl").Should().ContainSingle().Subject;
        byte[] stagedBytes = await File.ReadAllBytesAsync(stagedPath);
        string expectedDigest = Convert.ToHexString(SHA256.HashData(stagedBytes));

        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        SliceJob job = (await _harness.FindSliceJobAsync(fixture.OrchestrationId))!;
        _ = job.ModelSha256.Should().Be(expectedDigest);
        _ = (await _harness.CountStoredModelsAsync(fixture.Owner.UserId)).Should().Be(1);
        await using Farm.Slicer.Module.Data.SlicerDbContext slicer = _harness.CreateSlicerContext();
        Model3D model = await slicer.Models3D.AsNoTracking().SingleAsync(
            candidate => candidate.UploadedByUserId == fixture.Owner.UserId);
        string recoveredPath = Path.Join(_harness.ModelRoot, model.FileName);
        _ = recoveredPath.Should().Be(stagedPath);
        _ = Directory.GetFiles(_harness.ModelRoot, "*.stl").Should().ContainSingle();
        byte[] recoveredBytes = await File.ReadAllBytesAsync(recoveredPath);
        _ = recoveredBytes.Should().Equal(stagedBytes);
        _ = Convert.ToHexString(SHA256.HashData(recoveredBytes)).Should().Be(expectedDigest);
    }

    [Fact(DisplayName = "Invalid and unattributed model ID collisions remain fenced across restart")]
    public async Task ResumeAsync_WithInvalidUnattributedIdCollisions_UsesUnfilteredOccupancy()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-invalid-id-collision");
        GeneratedModelStorageKeys keys = ComputeGeneratedModelStorageKeys(fixture);
        Guid[] collisionIds = await SeedGeneratedModelCollisionsAsync(keys, isValid: false);
        ModelSaveNoCommitUncertainGate gate = new();
        CalibrationGenerationHarnessOptions options = new()
        {
            ModelRepositoryDecorator = gate.Decorate,
        };

        Func<Task> interrupted = () => _harness.CreateSaga(options)
            .ResumeAsync(fixture.OrchestrationId, CancellationToken.None);
        _ = await interrupted.Should().ThrowAsync<DbUpdateException>();
        string stagedPath = Directory.GetFiles(_harness.ModelRoot, "*.stl")
            .Should().ContainSingle().Subject;

        _ = await _harness.CreateSaga().ResumeAsync(
            fixture.OrchestrationId,
            CancellationToken.None);

        SliceJob job = (await _harness.FindSliceJobAsync(fixture.OrchestrationId))!;
        _ = job.Model3DId.Should().NotBeNull();
        _ = collisionIds.Should().NotContain(job.Model3DId!.Value);
        await using Farm.Slicer.Module.Data.SlicerDbContext slicer = _harness.CreateSlicerContext();
        Model3D stored = await slicer.Models3D.AsNoTracking()
            .SingleAsync(model => model.Id == job.Model3DId);
        _ = Path.Join(_harness.ModelRoot, stored.FileName).Should().Be(stagedPath);
        _ = Directory.GetFiles(_harness.ModelRoot, "*.stl").Should().ContainSingle();
    }

    [Fact(DisplayName = "Collision-row changes cannot strand uncertain generated-model staging")]
    public async Task ResumeAsync_WhenCollisionRowsChange_ReusesCollisionIndependentStagingPath()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        _ = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-changing-id-collision");
        GeneratedModelStorageKeys keys = ComputeGeneratedModelStorageKeys(fixture);
        Guid[] collisionIds = await SeedGeneratedModelCollisionsAsync(keys, isValid: true);
        ModelSaveNoCommitUncertainGate gate = new();
        CalibrationGenerationHarnessOptions options = new()
        {
            ModelRepositoryDecorator = gate.Decorate,
        };

        Func<Task> interrupted = () => _harness.CreateSaga(options)
            .ResumeAsync(fixture.OrchestrationId, CancellationToken.None);
        _ = await interrupted.Should().ThrowAsync<DbUpdateException>();
        string stagedPath = Directory.GetFiles(_harness.ModelRoot, "*.stl")
            .Should().ContainSingle().Subject;
        await using (Farm.Slicer.Module.Data.SlicerDbContext slicer = _harness.CreateSlicerContext())
        {
            _ = await slicer.Models3D
                .Where(model => collisionIds.Contains(model.Id))
                .ExecuteDeleteAsync();
        }

        _ = await _harness.CreateSaga().ResumeAsync(
            fixture.OrchestrationId,
            CancellationToken.None);

        SliceJob job = (await _harness.FindSliceJobAsync(fixture.OrchestrationId))!;
        await using Farm.Slicer.Module.Data.SlicerDbContext verify = _harness.CreateSlicerContext();
        Model3D stored = await verify.Models3D.AsNoTracking()
            .SingleAsync(model => model.Id == job.Model3DId);
        string recoveredPath = Path.Join(_harness.ModelRoot, stored.FileName);
        _ = recoveredPath.Should().Be(stagedPath);
        _ = Directory.GetFiles(_harness.ModelRoot, "*.stl").Should().ContainSingle();
        byte[] bytes = await File.ReadAllBytesAsync(recoveredPath);
        _ = Convert.ToHexString(SHA256.HashData(bytes)).Should().Be(job.ModelSha256);
    }

    [Fact(DisplayName = "A failed same-owner generation cannot delete a concurrent attempt's staging")]
    public async Task ResumeAsync_WithConcurrentSameOwnerFailure_PreservesSurvivorBytes()
    {
        Guid owner = Guid.NewGuid();
        CalibrationGenerationFixture failedFixture = await _harness.SeedAttemptAsync(ownerId: owner);
        CalibrationGenerationFixture survivorFixture = await _harness.SeedAttemptAsync(ownerId: owner);
        _ = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(failedFixture, "generate-same-owner-failed");
        await AcceptAsync(survivorFixture, "generate-same-owner-survivor");
        SameOwnerModelSaveFailureGate gate = new();
        CalibrationGenerationHarnessOptions options = new()
        {
            ModelRepositoryDecorator = gate.Decorate,
        };
        ICalibrationGenerationSaga failedSaga = _harness.CreateSaga(options);
        ICalibrationGenerationSaga survivorSaga = _harness.CreateSaga(options);

        Task survivor = survivorSaga.ResumeAsync(
            survivorFixture.OrchestrationId,
            CancellationToken.None);
        await gate.SurvivorSaveReached.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Func<Task> failed = () => failedSaga.ResumeAsync(
                failedFixture.OrchestrationId,
                CancellationToken.None);
            _ = await failed.Should().ThrowAsync<DbUpdateException>();
        }
        finally
        {
            gate.ReleaseSurvivor();
        }

        await survivor;

        _ = gate.FailedFileName.Should().NotBe(gate.SurvivorFileName);
        SliceJob job = (await _harness.FindSliceJobAsync(survivorFixture.OrchestrationId))!;
        await using Farm.Slicer.Module.Data.SlicerDbContext slicer = _harness.CreateSlicerContext();
        Model3D stored = await slicer.Models3D.AsNoTracking()
            .SingleAsync(model => model.Id == job.Model3DId);
        string storedPath = Path.Join(_harness.ModelRoot, stored.FileName);
        _ = File.Exists(storedPath).Should().BeTrue();
        _ = Directory.GetFiles(_harness.ModelRoot, "*.stl")
            .Should().ContainSingle().Which.Should().Be(storedPath);
        byte[] bytes = await File.ReadAllBytesAsync(storedPath);
        _ = Convert.ToHexString(SHA256.HashData(bytes)).Should().Be(job.ModelSha256);
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

    [Fact(DisplayName = "A reclaimed job promotes only the artifact accepted by its current claim")]
    public async Task ResumeAsync_AfterReclaim_SelectsAcceptedCurrentClaimArtifact()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        Guid currentWorkerId = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-reclaimed-artifact");
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);
        (Guid staleArtifactId, Guid acceptedArtifactId) =
            await _harness.CompleteReclaimedWorkerJobAsync(
                fixture.OrchestrationId,
                Guid.NewGuid(),
                currentWorkerId);

        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        CalibrationOrchestration orchestration =
            await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = orchestration.Status.Should().Be(CalibrationOrchestrationStatus.Completed);
        _ = orchestration.SourceArtifactId.Should().Be(acceptedArtifactId);
        _ = orchestration.SourceArtifactId.Should().NotBe(staleArtifactId);
        _ = orchestration.WorkerId.Should().Be(currentWorkerId);
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

    [Fact(DisplayName = "A worker that claimed the job and is busy is reconciled, not treated as unavailable")]
    public async Task ResumeAsync_WithWorkerBusyOnClaimedJob_ReconcilesExistingJob()
    {
        CalibrationGenerationFixture fixture = await _harness.SeedAttemptAsync();
        Guid workerId = await _harness.AddAttestedWorkerAsync();
        await AcceptAsync(fixture, "generate-busy-worker");

        // The only worker claims the job: this is what a real claim does to the durable worker row,
        // and it must not make the same worker look "unavailable" to the pinned identity/attestation
        // check the very next time the saga resumes.
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);
        await _harness.MutateWorkerAsync(workerId, worker => worker.ActiveJobs = worker.TotalSlots);

        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        CalibrationOrchestration afterBusyResume = await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = afterBusyResume.CurrentStep.Should().Be(CalibrationGenerationSteps.AwaitingWorker);
        _ = afterBusyResume.Status.Should().Be(CalibrationOrchestrationStatus.Running);
        _ = afterBusyResume.LastErrorCode.Should().NotBe(CalibrationGenerationProblemCodes.PinnedWorkerUnavailable);

        // Reconciling the same busy worker's job must not queue a second one behind it.
        _ = (await _harness.CountSliceJobsAsync(fixture.OrchestrationId)).Should().Be(1);

        // Once the busy worker finishes the job it was already running, the run completes without
        // ever requiring a free slot to open up first.
        _ = await _harness.CompleteWorkerJobAsync(fixture.OrchestrationId, workerId);
        _ = await _harness.CreateSaga().ResumeAsync(fixture.OrchestrationId, CancellationToken.None);

        CalibrationOrchestration completed = await _harness.GetOrchestrationAsync(fixture.OrchestrationId);
        _ = completed.Status.Should().Be(CalibrationOrchestrationStatus.Completed);
        _ = completed.LastErrorCode.Should().BeNull();
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

    private async Task<Guid[]> SeedGeneratedModelCollisionsAsync(
        GeneratedModelStorageKeys keys,
        bool isValid)
    {
        Guid[] collisionIds = [keys.LegacyModelId, keys.StagingModelId];
        DateTime now = DateTime.UtcNow;
        await using Farm.Slicer.Module.Data.SlicerDbContext slicer = _harness.CreateSlicerContext();
        slicer.Models3D.AddRange(collisionIds.Select(id => new Model3D
        {
            Id = id,
            Name = "occupied generated-model identity",
            FileName = $"{id:N}.collision.stl",
            FilePath = string.Empty,
            FileSizeBytes = 1,
            FileHash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes($"collision:{id:N}"))),
            FileFormat = ModelFileFormat.STL,
            UploadedByUserId = null,
            UploadedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            IsValid = isValid,
        }));
        _ = await slicer.SaveChangesAsync();
        return collisionIds;
    }

    private static GeneratedModelStorageKeys ComputeGeneratedModelStorageKeys(
        CalibrationGenerationFixture fixture)
    {
        CalibrationGeneratedGeometry geometry =
            CalibrationBodyGeometryFactory.Build(fixture.Specification);
        string contentSha256 = Convert.ToHexString(SHA256.HashData(geometry.Content.Span));
        string ownerScopedHash = CalibrationCanonicalJson.ComputeSha256(new
        {
            purpose = "calibration-generated-model-storage",
            ownerUserId = fixture.Owner.UserId,
            contentSha256,
        }).ToUpperInvariant();
        string stagingIdentity = CalibrationCanonicalJson.ComputeSha256(new
        {
            purpose = "calibration-generated-model-staging",
            ownerUserId = fixture.Owner.UserId,
            contentSha256,
            orchestrationId = fixture.OrchestrationId,
        }).ToUpperInvariant();

        return new GeneratedModelStorageKeys(
            ComputeDeterministicGuid($"calibration-generated-model:{ownerScopedHash}"),
            ComputeDeterministicGuid($"calibration-generated-model:{stagingIdentity}:0"));
    }

    private static Guid ComputeDeterministicGuid(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }

    private sealed record GeneratedModelStorageKeys(
        Guid LegacyModelId,
        Guid StagingModelId);

    private sealed class SameOwnerModelSaveFailureGate
    {
        private readonly TaskCompletionSource _survivorSaveReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSurvivor =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _participantCount;

        public Task SurvivorSaveReached => _survivorSaveReached.Task;

        public string? FailedFileName { get; private set; }

        public string? SurvivorFileName { get; private set; }

        public void ReleaseSurvivor() => _releaseSurvivor.TrySetResult();

        public IModel3DFileRepository Decorate(IModel3DFileRepository inner)
        {
            int participant = Interlocked.Increment(ref _participantCount);
            if (participant > 2)
            {
                throw new InvalidOperationException(
                    "The same-owner model gate supports exactly two repositories.");
            }

            Mock<IModel3DFileRepository> gated = new(MockBehavior.Strict);
            _ = gated.Setup(repository => repository.GetByHashAsync(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .Returns((string hash, CancellationToken cancellationToken) =>
                    inner.GetByHashAsync(hash, cancellationToken));
            _ = gated.Setup(repository => repository.GetByIdAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()))
                .Returns((Guid id, CancellationToken cancellationToken) =>
                    inner.GetByIdAsync(id, cancellationToken));
            _ = gated.Setup(repository => repository.GetByIdUnfilteredAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()))
                .Returns((Guid id, CancellationToken cancellationToken) =>
                    inner.GetByIdUnfilteredAsync(id, cancellationToken));
            _ = gated.Setup(repository => repository.GetByIdForReconciliationAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()))
                .Returns((Guid id, CancellationToken cancellationToken) =>
                    inner.GetByIdForReconciliationAsync(id, cancellationToken));
            _ = gated.Setup(repository => repository.AddAsync(
                    It.IsAny<Model3D>(),
                    It.IsAny<CancellationToken>()))
                .Returns((Model3D model, CancellationToken cancellationToken) =>
                {
                    if (participant == 1)
                    {
                        FailedFileName = model.FileName;
                    }
                    else
                    {
                        SurvivorFileName = model.FileName;
                    }

                    return inner.AddAsync(model, cancellationToken);
                });
            _ = gated.Setup(repository => repository.SaveChangesAsync(
                    It.IsAny<CancellationToken>()))
                .Returns(async (CancellationToken cancellationToken) =>
                {
                    if (participant == 1)
                    {
                        throw new DbUpdateException(
                            "The failed same-owner model save did not commit.");
                    }

                    _survivorSaveReached.TrySetResult();
                    await _releaseSurvivor.Task.WaitAsync(
                        TimeSpan.FromSeconds(10),
                        cancellationToken);
                    await inner.SaveChangesAsync(cancellationToken);
                });
            return gated.Object;
        }
    }

    private sealed class ModelStorageRaceGate
    {
        private readonly TaskCompletionSource _bothHashesRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _bothSavesReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _winnerSaved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _participantCount;
        private int _hashReadCount;
        private int _saveCount;

        public IModel3DFileRepository Decorate(IModel3DFileRepository inner)
        {
            int participant = Interlocked.Increment(ref _participantCount);
            if (participant > 2)
            {
                throw new InvalidOperationException("The model race gate supports exactly two repositories.");
            }

            Mock<IModel3DFileRepository> gated = new(MockBehavior.Strict);
            _ = gated.Setup(repository => repository.GetByHashAsync(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .Returns(async (string hash, CancellationToken cancellationToken) =>
                {
                    Model3D? existing = await inner.GetByHashAsync(hash, cancellationToken);
                    if (Interlocked.Increment(ref _hashReadCount) == 2)
                    {
                        _bothHashesRead.TrySetResult();
                    }

                    await _bothHashesRead.Task.WaitAsync(
                        TimeSpan.FromSeconds(10),
                        cancellationToken);
                    return existing;
                });
            _ = gated.Setup(repository => repository.GetByIdAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()))
                .Returns((Guid id, CancellationToken cancellationToken) =>
                    inner.GetByIdAsync(id, cancellationToken));
            _ = gated.Setup(repository => repository.GetByIdUnfilteredAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()))
                .Returns((Guid id, CancellationToken cancellationToken) =>
                    inner.GetByIdUnfilteredAsync(id, cancellationToken));
            _ = gated.Setup(repository => repository.GetByIdForReconciliationAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()))
                .Returns((Guid id, CancellationToken cancellationToken) =>
                    inner.GetByIdForReconciliationAsync(id, cancellationToken));
            _ = gated.Setup(repository => repository.AddAsync(
                    It.IsAny<Model3D>(),
                    It.IsAny<CancellationToken>()))
                .Returns((Model3D model, CancellationToken cancellationToken) =>
                    inner.AddAsync(model, cancellationToken));
            _ = gated.Setup(repository => repository.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .Returns(async (CancellationToken cancellationToken) =>
                {
                    if (Interlocked.Increment(ref _saveCount) == 2)
                    {
                        _bothSavesReached.TrySetResult();
                    }

                    await _bothSavesReached.Task.WaitAsync(
                        TimeSpan.FromSeconds(10),
                        cancellationToken);
                    if (participant == 1)
                    {
                        try
                        {
                            await inner.SaveChangesAsync(cancellationToken);
                            _winnerSaved.TrySetResult();
                        }
                        catch (Exception ex)
                        {
                            _winnerSaved.TrySetException(ex);
                            throw;
                        }

                        return;
                    }

                    await _winnerSaved.Task.WaitAsync(
                        TimeSpan.FromSeconds(10),
                        cancellationToken);
                    await inner.SaveChangesAsync(cancellationToken);
                });
            return gated.Object;
        }
    }

    private sealed class ModelSaveCommitUnknownGate
    {
        private int _throwAfterCommit = 1;

        public IModel3DFileRepository Decorate(IModel3DFileRepository inner)
        {
            Mock<IModel3DFileRepository> gated = new(MockBehavior.Strict);
            _ = gated.Setup(repository => repository.GetByHashAsync(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .Returns((string hash, CancellationToken cancellationToken) =>
                    inner.GetByHashAsync(hash, cancellationToken));
            _ = gated.Setup(repository => repository.GetByIdAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()))
                .Returns((Guid id, CancellationToken cancellationToken) =>
                    inner.GetByIdAsync(id, cancellationToken));
            _ = gated.Setup(repository => repository.GetByIdUnfilteredAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()))
                .Returns((Guid id, CancellationToken cancellationToken) =>
                    inner.GetByIdUnfilteredAsync(id, cancellationToken));
            _ = gated.Setup(repository => repository.GetByIdForReconciliationAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()))
                .Returns((Guid id, CancellationToken cancellationToken) =>
                    inner.GetByIdForReconciliationAsync(id, cancellationToken));
            _ = gated.Setup(repository => repository.AddAsync(
                    It.IsAny<Model3D>(),
                    It.IsAny<CancellationToken>()))
                .Returns((Model3D model, CancellationToken cancellationToken) =>
                    inner.AddAsync(model, cancellationToken));
            _ = gated.Setup(repository => repository.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .Returns(async (CancellationToken cancellationToken) =>
                {
                    await inner.SaveChangesAsync(cancellationToken);
                    if (Interlocked.Exchange(ref _throwAfterCommit, 0) == 1)
                    {
                        throw new DbUpdateException("The model commit outcome is unknown.");
                    }
                });
            return gated.Object;
        }
    }

    private sealed class ModelSaveNoCommitUncertainGate
    {
        public IModel3DFileRepository Decorate(IModel3DFileRepository inner)
        {
            Mock<IModel3DFileRepository> gated = new(MockBehavior.Strict);
            _ = gated.Setup(repository => repository.GetByHashAsync(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .Returns((string hash, CancellationToken cancellationToken) =>
                    inner.GetByHashAsync(hash, cancellationToken));
            _ = gated.Setup(repository => repository.GetByIdAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()))
                .Returns((Guid id, CancellationToken cancellationToken) =>
                    inner.GetByIdAsync(id, cancellationToken));
            _ = gated.Setup(repository => repository.GetByIdUnfilteredAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()))
                .Returns((Guid id, CancellationToken cancellationToken) =>
                    inner.GetByIdUnfilteredAsync(id, cancellationToken));
            _ = gated.Setup(repository => repository.GetByIdForReconciliationAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Model reconciliation is unavailable."));
            _ = gated.Setup(repository => repository.AddAsync(
                    It.IsAny<Model3D>(),
                    It.IsAny<CancellationToken>()))
                .Returns((Model3D model, CancellationToken cancellationToken) =>
                    inner.AddAsync(model, cancellationToken));
            _ = gated.Setup(repository => repository.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new DbUpdateException("The model save did not commit."));
            return gated.Object;
        }
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
            Path.Join(_harness.ArtifactRoot, artifact.RelativePath),
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
        return Path.Join(_harness.ArtifactRoot, artifact.RelativePath);
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

    /// <summary>
    /// The production plan compiler, with the plan a real pass compiled kept for inspection.
    /// </summary>
    /// <remarks>
    /// The saga's behaviour is unchanged: the plan is compiled by the production compiler and
    /// returned untouched. Recording it is the only way a test can compute what a superseded
    /// manifest schema would have written for the very same plan.
    /// </remarks>
    private sealed class CapturingPlanCompiler : IOrcaCalibrationPlanCompiler
    {
        private readonly OrcaCalibrationPlanCompiler _inner = new();

        /// <summary>Gets the last plan a pass compiled successfully.</summary>
        public OrcaCalibrationPlan? Compiled { get; private set; }

        /// <inheritdoc/>
        public CalibrationGenerationResult<OrcaCalibrationPlan> Compile(
            CalibrationSpecification specification,
            CalibrationValidatedModel model)
        {
            CalibrationGenerationResult<OrcaCalibrationPlan> result =
                _inner.Compile(specification, model);
            Compiled = result.Value ?? Compiled;
            return result;
        }
    }
}
