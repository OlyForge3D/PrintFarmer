using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Module.Services;
using Farm.Web.Api.Contracts;
using Farm.Web.Api.Services.Gcode;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>
/// Default <see cref="ICalibrationGenerationSaga"/>.
/// </summary>
/// <remarks>
/// <para>
/// The saga owns a single <see cref="CalibrationOrchestration"/> row, which the attempt aggregate
/// already created. It never inserts a second one. Each durable step writes its checkpoint with the
/// orchestration's optimistic concurrency token before performing the side effect it names, so a crash
/// leaves either "not started" or "started, outcome unknown" — and the unknown case is always resolved
/// from durable evidence (a correlated slice job, an artifact digest, a promotion record) instead of by
/// repeating the effect.
/// </para>
/// <para>
/// The promoted program is the annotated, statically validated trusted program. The pinned upstream
/// slicer output is verified and preserved as immutable lineage rather than spliced into the validated
/// bytes, because splicing would invalidate the manifest byte offsets the safety validator depends on.
/// </para>
/// </remarks>
public sealed class CalibrationGenerationSaga(
    AppDbContext dbContext,
    ICalibrationProjectService projectService,
    ICalibrationSpecificationCompiler specificationCompiler,
    ICalibrationModelValidator modelValidator,
    IOrcaCalibrationPlanCompiler planCompiler,
    IKlipperCalibrationGcodeGenerator gcodeGenerator,
    ICalibrationGcodeAnnotator annotator,
    ICalibrationGcodeSafetyValidator safetyValidator,
    ICalibrationGenerationCapabilityProbe capabilityProbe,
    IGcodeArtifactPromoter promoter,
    IStoragePathService storagePaths,
    TimeProvider timeProvider,
    ILogger<CalibrationGenerationSaga> logger,
    ISliceJobRepository? sliceJobs = null,
    IArtifactsService? artifacts = null,
    IModelStorageResolver? modelStorage = null,
    IModel3DFileRepository? models = null) : ICalibrationGenerationSaga
{
    /// <summary>Idempotency scope every generation operation key is recorded under.</summary>
    public const string IdempotencyScope = "calibration.generate-job";

    private const int MaximumRetries = 8;
    private const int MaximumRecoveryBatch = 50;
    private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WorkerPollDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions ProblemJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly ICalibrationProjectService _projectService =
        projectService ?? throw new ArgumentNullException(nameof(projectService));

    private readonly ICalibrationSpecificationCompiler _specificationCompiler =
        specificationCompiler ?? throw new ArgumentNullException(nameof(specificationCompiler));

    private readonly ICalibrationModelValidator _modelValidator =
        modelValidator ?? throw new ArgumentNullException(nameof(modelValidator));

    private readonly IOrcaCalibrationPlanCompiler _planCompiler =
        planCompiler ?? throw new ArgumentNullException(nameof(planCompiler));

    private readonly IKlipperCalibrationGcodeGenerator _gcodeGenerator =
        gcodeGenerator ?? throw new ArgumentNullException(nameof(gcodeGenerator));

    private readonly ICalibrationGcodeAnnotator _annotator =
        annotator ?? throw new ArgumentNullException(nameof(annotator));

    private readonly ICalibrationGcodeSafetyValidator _safetyValidator =
        safetyValidator ?? throw new ArgumentNullException(nameof(safetyValidator));

    private readonly ICalibrationGenerationCapabilityProbe _capabilityProbe =
        capabilityProbe ?? throw new ArgumentNullException(nameof(capabilityProbe));

    private readonly IGcodeArtifactPromoter _promoter = promoter ?? throw new ArgumentNullException(nameof(promoter));
    private readonly IStoragePathService _storagePaths =
        storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));

    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<CalibrationGenerationSaga> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly ISliceJobRepository? _sliceJobs = sliceJobs;
    private readonly IArtifactsService? _artifacts = artifacts;
    private readonly IModelStorageResolver? _modelStorage = modelStorage;
    private readonly IModel3DFileRepository? _models = models;

    private readonly string _leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <inheritdoc/>
    public async Task<CalibrationApiResult<CalibrationOrchestrationStatusDto>> CreateOrResumeAsync(
        Guid projectId,
        Guid attemptId,
        string? operationId,
        CalibrationGenerateJobRequest request,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        if (string.IsNullOrWhiteSpace(operationId) || operationId.Trim().Length > 128)
        {
            return Failure(StatusCodes.Status400BadRequest, "idempotency_key_required");
        }

        string normalizedOperationId = operationId.Trim();
        CalibrationGenerationResult<CalibrationMethodOptions> bound = CalibrationMethodOptionsBinder.Bind(
            request.Method,
            request.DefinitionVersion,
            request.Options);
        if (!bound.IsValid)
        {
            return Unprocessable(bound.Problems);
        }

        CalibrationProject? project = await _dbContext.CalibrationProjects
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == projectId && candidate.DeletedAtUtc == null, cancellationToken);
        if (project is null)
        {
            return NotFound();
        }

        if (!actor.IsFarmAdmin && project.OwnerUserId != actor.UserId)
        {
            // Farm isolation: a non-owner must not learn whether the project exists at all.
            return NotFound();
        }

        CalibrationAttempt? attempt = await _dbContext.CalibrationAttempts
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.Id == attemptId && candidate.ProjectId == projectId,
                cancellationToken);
        if (attempt is null)
        {
            return NotFound();
        }

        if (!string.Equals(attempt.Method, request.Method?.Trim(), StringComparison.Ordinal))
        {
            return Unprocessable(
            [
                new(
                    CalibrationGenerationProblemCodes.AttemptMethodMismatch,
                    "method",
                    "The requested method does not match the immutable attempt method."),
            ]);
        }

        CalibrationOrchestration? orchestration = await _dbContext.CalibrationOrchestrations
            .FirstOrDefaultAsync(candidate => candidate.AttemptId == attemptId, cancellationToken);
        if (orchestration is null)
        {
            return Failure(StatusCodes.Status409Conflict, "orchestration_not_initialized");
        }

        if (request.BaseRevision is { } baseRevision && baseRevision != orchestration.Revision)
        {
            return CalibrationApiResult<CalibrationOrchestrationStatusDto>.Failure(
                StatusCodes.Status412PreconditionFailed,
                "revision_conflict");
        }

        string requestSha256 = ComputeRequestSha256(projectId, attemptId, request);
        CalibrationApiResult<CalibrationOrchestrationStatusDto>? replay = await FindReplayAsync(
            actor,
            normalizedOperationId,
            requestSha256,
            orchestration,
            cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        if (!string.IsNullOrEmpty(orchestration.GenerationRequestSha256) &&
            !string.Equals(orchestration.GenerationRequestSha256, requestSha256, StringComparison.Ordinal))
        {
            return Failure(StatusCodes.Status409Conflict, "idempotency_payload_mismatch");
        }

        // A run already owned by a different generation operation key must not be adopted by this one.
        bool ownedByAnotherOperation = await _dbContext.CalibrationIdempotencyRecords
            .AsNoTracking()
            .AnyAsync(
                record => record.Scope == IdempotencyScope &&
                    record.ResourceId == orchestration.Id &&
                    record.OperationId != normalizedOperationId,
                cancellationToken);
        if (ownedByAnotherOperation)
        {
            return Failure(StatusCodes.Status409Conflict, "incompatible_existing_operation");
        }

        CalibrationGenerationCapabilityDto capability =
            await _capabilityProbe.GetCapabilityAsync(cancellationToken);
        if (!capability.Operational)
        {
            return Failure(StatusCodes.Status503ServiceUnavailable, "generation_dependency_unavailable");
        }

        if (orchestration.Status is CalibrationOrchestrationStatus.Completed
            or CalibrationOrchestrationStatus.Failed
            or CalibrationOrchestrationStatus.Cancelled)
        {
            await RecordIdempotencyAsync(
                actor,
                project,
                normalizedOperationId,
                requestSha256,
                orchestration,
                StatusCodes.Status200OK,
                cancellationToken);
            return CalibrationApiResult<CalibrationOrchestrationStatusDto>.Success(
                Project(orchestration),
                StatusCodes.Status200OK,
                replayed: true);
        }

        DateTime nowUtc = UtcNow();

        // The orchestration's operation identifier belongs to the attempt aggregate that created it.
        // The generation operation key is tracked separately, through the idempotency record and the
        // canonical request digest, so the prerequisite's semantics stay intact.
        orchestration.GenerationRequestSha256 = requestSha256;
        orchestration.Status = CalibrationOrchestrationStatus.Running;
        orchestration.CurrentStep = orchestration.CurrentStep is CalibrationGenerationSteps.Created or ""
            ? CalibrationGenerationSteps.ValidatingContext
            : orchestration.CurrentStep;
        orchestration.StepStartedAtUtc ??= nowUtc;
        orchestration.NextRetryAtUtc = nowUtc;
        orchestration.UpdatedAtUtc = nowUtc;
        orchestration.Revision++;

        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _dbContext.ChangeTracker.Clear();
            CalibrationOrchestration? current = await _dbContext.CalibrationOrchestrations
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == orchestration.Id, cancellationToken);
            return current is null
                ? NotFound()
                : CalibrationApiResult<CalibrationOrchestrationStatusDto>.Success(
                    Project(current),
                    StatusCodes.Status200OK,
                    replayed: true);
        }

        await RecordIdempotencyAsync(
            actor,
            project,
            normalizedOperationId,
            requestSha256,
            orchestration,
            StatusCodes.Status202Accepted,
            cancellationToken);
        await AppendEventAsync(
            project,
            orchestration,
            "generation-accepted",
            errorCode: null,
            problems: [],
            cancellationToken);

        return CalibrationApiResult<CalibrationOrchestrationStatusDto>.Success(
            Project(orchestration),
            StatusCodes.Status202Accepted);
    }

    /// <inheritdoc/>
    public async Task<CalibrationApiResult<CalibrationOrchestrationStatusDto>> GetStatusAsync(
        Guid orchestrationId,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        CalibrationOrchestration? orchestration = await _dbContext.CalibrationOrchestrations
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == orchestrationId, cancellationToken);
        if (orchestration is null)
        {
            return NotFound();
        }

        CalibrationProject? project = await _dbContext.CalibrationProjects
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == orchestration.ProjectId, cancellationToken);
        return project is null || (!actor.IsFarmAdmin && project.OwnerUserId != actor.UserId)
            ? NotFound()
            : CalibrationApiResult<CalibrationOrchestrationStatusDto>.Success(Project(orchestration));
    }

    /// <inheritdoc/>
    public async Task<CalibrationApiResult<CalibrationOrchestrationStatusDto>> ResumeAsync(
        Guid orchestrationId,
        CancellationToken cancellationToken) =>
        await AdvanceAsync(orchestrationId, reconcileOnly: false, cancellationToken);

    /// <inheritdoc/>
    public async Task<CalibrationApiResult<CalibrationOrchestrationStatusDto>> ReconcileAsync(
        Guid orchestrationId,
        CancellationToken cancellationToken) =>
        await AdvanceAsync(orchestrationId, reconcileOnly: true, cancellationToken);

    /// <inheritdoc/>
    public async Task<int> RecoverDueAsync(int maxOrchestrations, CancellationToken cancellationToken)
    {
        int limit = Math.Clamp(maxOrchestrations, 1, MaximumRecoveryBatch);
        DateTime nowUtc = UtcNow();
        List<Guid> due = await _dbContext.CalibrationOrchestrations
            .AsNoTracking()
            .Where(orchestration =>
                orchestration.GenerationRequestSha256 != null &&
                (orchestration.Status == CalibrationOrchestrationStatus.Running ||
                    orchestration.Status == CalibrationOrchestrationStatus.WaitingToRetry ||
                    orchestration.Status == CalibrationOrchestrationStatus.Pending) &&
                (orchestration.NextRetryAtUtc == null || orchestration.NextRetryAtUtc <= nowUtc) &&
                (orchestration.LeaseExpiresAtUtc == null || orchestration.LeaseExpiresAtUtc <= nowUtc))
            .OrderBy(orchestration => orchestration.UpdatedAtUtc)
            .Select(orchestration => orchestration.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

        int advanced = 0;
        foreach (Guid orchestrationId in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _dbContext.ChangeTracker.Clear();
            CalibrationApiResult<CalibrationOrchestrationStatusDto> result =
                await AdvanceAsync(orchestrationId, reconcileOnly: false, cancellationToken);
            if (result.IsSuccess)
            {
                advanced++;
            }
        }

        return advanced;
    }

    /// <inheritdoc/>
    public async Task<CalibrationApiResult<CalibrationOrchestrationStatusDto>> CancelAsync(
        Guid orchestrationId,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        CalibrationOrchestration? orchestration = await _dbContext.CalibrationOrchestrations
            .FirstOrDefaultAsync(candidate => candidate.Id == orchestrationId, cancellationToken);
        if (orchestration is null)
        {
            return NotFound();
        }

        CalibrationProject? project = await _dbContext.CalibrationProjects
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == orchestration.ProjectId, cancellationToken);
        if (project is null || (!actor.IsFarmAdmin && project.OwnerUserId != actor.UserId))
        {
            return NotFound();
        }

        if (orchestration.Status is CalibrationOrchestrationStatus.Cancelled)
        {
            return CalibrationApiResult<CalibrationOrchestrationStatusDto>.Success(
                Project(orchestration),
                StatusCodes.Status200OK,
                replayed: true);
        }

        // The aggregate has no semantics for withdrawing work another bounded context already owns,
        // and this scope deliberately does not invent them.
        if (orchestration.SliceJobId is not null ||
            orchestration.Status is CalibrationOrchestrationStatus.Completed
                or CalibrationOrchestrationStatus.Failed)
        {
            return Failure(StatusCodes.Status409Conflict, "cancellation_not_permitted");
        }

        DateTime nowUtc = UtcNow();
        orchestration.Status = CalibrationOrchestrationStatus.Cancelled;
        orchestration.CurrentStep = CalibrationGenerationSteps.Cancelled;
        orchestration.CompletedAtUtc = nowUtc;
        orchestration.UpdatedAtUtc = nowUtc;
        orchestration.LeaseOwner = null;
        orchestration.LeaseExpiresAtUtc = null;
        orchestration.NextRetryAtUtc = null;
        orchestration.Revision++;
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _dbContext.ChangeTracker.Clear();
            return Failure(StatusCodes.Status409Conflict, "cancellation_not_permitted");
        }

        await AppendEventAsync(project, orchestration, "generation-cancelled", null, [], cancellationToken);
        return CalibrationApiResult<CalibrationOrchestrationStatusDto>.Success(Project(orchestration));
    }

    private async Task<CalibrationApiResult<CalibrationOrchestrationStatusDto>> AdvanceAsync(
        Guid orchestrationId,
        bool reconcileOnly,
        CancellationToken cancellationToken)
    {
        CalibrationOrchestration? orchestration = await _dbContext.CalibrationOrchestrations
            .FirstOrDefaultAsync(candidate => candidate.Id == orchestrationId, cancellationToken);
        if (orchestration is null)
        {
            return NotFound();
        }

        if (orchestration.GenerationRequestSha256 is null ||
            orchestration.Status is CalibrationOrchestrationStatus.Completed
                or CalibrationOrchestrationStatus.Failed
                or CalibrationOrchestrationStatus.Cancelled)
        {
            return CalibrationApiResult<CalibrationOrchestrationStatusDto>.Success(Project(orchestration));
        }

        if (!await TryAcquireLeaseAsync(orchestration, cancellationToken))
        {
            return Failure(StatusCodes.Status409Conflict, "orchestration_lease_held");
        }

        try
        {
            return await RunStepsAsync(orchestration, reconcileOnly, cancellationToken);
        }
        catch (Exception exception) when (exception is DbException or IOException or InvalidOperationException)
        {
            _logger.LogWarning(
                exception,
                "Calibration generation step {Step} failed for orchestration {OrchestrationId}",
                orchestration.CurrentStep,
                orchestration.Id);
            await ScheduleRetryAsync(
                orchestration,
                "generation_step_unavailable",
                [
                    new(
                        "generation_step_unavailable",
                        orchestration.CurrentStep,
                        "The generation step could not complete and will be retried."),
                ],
                cancellationToken);
            return CalibrationApiResult<CalibrationOrchestrationStatusDto>.Success(Project(orchestration));
        }
        finally
        {
            await ReleaseLeaseAsync(orchestration, cancellationToken);
        }
    }

    private async Task<CalibrationApiResult<CalibrationOrchestrationStatusDto>> RunStepsAsync(
        CalibrationOrchestration orchestration,
        bool reconcileOnly,
        CancellationToken cancellationToken)
    {
        CalibrationProject? project = await _dbContext.CalibrationProjects
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == orchestration.ProjectId, cancellationToken);
        CalibrationAttempt? attempt = await _dbContext.CalibrationAttempts
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == orchestration.AttemptId, cancellationToken);
        if (project is null || attempt is null)
        {
            await FailTerminallyAsync(
                project,
                orchestration,
                CalibrationGenerationProblemCodes.ContextIdentityMissing,
                [
                    new(
                        CalibrationGenerationProblemCodes.ContextIdentityMissing,
                        "attempt",
                        "The immutable attempt aggregate is no longer available."),
                ],
                cancellationToken);
            return CalibrationApiResult<CalibrationOrchestrationStatusDto>.Success(Project(orchestration));
        }

        if (_sliceJobs is null || _artifacts is null || _modelStorage is null || _models is null)
        {
            await ScheduleRetryAsync(
                orchestration,
                CalibrationGenerationProblemCodes.SliceSubmissionUnavailable,
                [
                    new(
                        CalibrationGenerationProblemCodes.SliceSubmissionUnavailable,
                        "deployment",
                        "The canonical slicing path is not routable from this process."),
                ],
                cancellationToken);
            return CalibrationApiResult<CalibrationOrchestrationStatusDto>.Success(Project(orchestration));
        }

        CalibrationPinnedSlicerIdentity? pinned =
            await _capabilityProbe.FindPinnedWorkerAsync(cancellationToken);
        if (pinned is null)
        {
            await ScheduleRetryAsync(
                orchestration,
                CalibrationGenerationProblemCodes.PinnedWorkerUnavailable,
                [
                    new(
                        CalibrationGenerationProblemCodes.PinnedWorkerUnavailable,
                        "worker",
                        "No registered worker attests the pinned upstream slicer build identity."),
                ],
                cancellationToken);
            return CalibrationApiResult<CalibrationOrchestrationStatusDto>.Success(Project(orchestration));
        }

        CalibrationGenerationResult<CalibrationRunContext> prepared =
            await PrepareAsync(project, attempt, orchestration, pinned, cancellationToken);
        if (!prepared.IsValid)
        {
            await FailTerminallyAsync(
                project,
                orchestration,
                prepared.Problems[0].Code,
                prepared.Problems,
                cancellationToken);
            return CalibrationApiResult<CalibrationOrchestrationStatusDto>.Success(Project(orchestration));
        }

        CalibrationRunContext run = prepared.Value!;
        if (orchestration.CurrentStep is CalibrationGenerationSteps.Created or CalibrationGenerationSteps.ValidatingContext)
        {
            orchestration.SpecificationSha256 = run.Specification.Sha256;
            orchestration.GeneratorVersion = CalibrationGeneratorIdentity.Current.Version;
            orchestration.SlicerContainerDigest = pinned.ContainerDigest;
            orchestration.SlicerBinarySha256 = pinned.BinarySha256;
            await CheckpointAsync(orchestration, CalibrationGenerationSteps.ResolvingModel, cancellationToken);
        }

        // Generation is deterministic, so the model, plan and annotated program are recomputed on
        // every pass and must reproduce the digests already checkpointed. That is what makes a resume
        // after a restart identical to the original run instead of a second, different run.
        CalibrationGenerationResult<CalibrationValidatedModel> resolved =
            await ResolveModelAsync(project, orchestration, run, cancellationToken);
        if (!resolved.IsValid)
        {
            await FailTerminallyAsync(project, orchestration, resolved.Problems[0].Code, resolved.Problems, cancellationToken);
            return CalibrationApiResult<CalibrationOrchestrationStatusDto>.Success(Project(orchestration));
        }

        if (orchestration.CurrentStep == CalibrationGenerationSteps.ResolvingModel)
        {
            await CheckpointAsync(orchestration, CalibrationGenerationSteps.CompilingPlan, cancellationToken);
        }

        CalibrationGenerationResult<OrcaCalibrationPlan> planned =
            _planCompiler.Compile(run.Specification, resolved.Value!);
        if (!planned.IsValid)
        {
            await FailTerminallyAsync(project, orchestration, planned.Problems[0].Code, planned.Problems, cancellationToken);
            return CalibrationApiResult<CalibrationOrchestrationStatusDto>.Success(Project(orchestration));
        }

        OrcaCalibrationPlan plan = planned.Value!;
        if (orchestration.PlanManifestSha256 is { Length: > 0 } pinnedPlan &&
            !CalibrationCanonicalJson.DigestsMatch(pinnedPlan, plan.ManifestSha256))
        {
            // A trusted server upgrade that changed only how a plan manifest is written down is not
            // drift: the plan body recompiles identically and only its serialized layout moved. Such
            // a checkpoint is recognized by reproducing it from a superseded schema, and the run then
            // keeps completing under the schema it was accepted with, so its already-submitted job,
            // its composed program and its promotion stay byte-identical and nothing durable is
            // rewritten. Anything no superseded schema explains is still a terminal mismatch.
            OrcaCalibrationPlan? accepted =
                OrcaCalibrationPlanManifestSchema.BindToCheckpoint(plan, pinnedPlan);
            if (accepted is null)
            {
                await FailTerminallyAsync(
                    project,
                    orchestration,
                    CalibrationGenerationProblemCodes.PlanModelMismatch,
                    [
                        new(
                            CalibrationGenerationProblemCodes.PlanModelMismatch,
                            "orchestration.planManifestSha256",
                            "The recompiled plan no longer matches the plan this run was accepted with."),
                    ],
                    cancellationToken);
                return CalibrationApiResult<CalibrationOrchestrationStatusDto>.Success(Project(orchestration));
            }

            _logger.LogInformation(
                "Calibration orchestration {OrchestrationId} continues under superseded plan manifest schema {SchemaVersion}",
                orchestration.Id,
                accepted.Manifest.SchemaVersion);
            plan = accepted;
        }

        if (orchestration.CurrentStep == CalibrationGenerationSteps.CompilingPlan)
        {
            orchestration.PlanManifestSha256 = plan.ManifestSha256;
            await CheckpointAsync(orchestration, CalibrationGenerationSteps.SubmittingSliceJob, cancellationToken);
        }

        if (orchestration.CurrentStep == CalibrationGenerationSteps.SubmittingSliceJob)
        {
            await SubmitSliceJobAsync(
                project,
                orchestration,
                run,
                resolved.Value!,
                plan,
                pinned,
                cancellationToken);
        }

        if (orchestration.CurrentStep == CalibrationGenerationSteps.AwaitingWorker)
        {
            bool completed = await ObserveWorkerAsync(project, orchestration, cancellationToken);
            if (!completed)
            {
                return CalibrationApiResult<CalibrationOrchestrationStatusDto>.Success(Project(orchestration));
            }
        }

        if (orchestration.CurrentStep == CalibrationGenerationSteps.VerifyingArtifact)
        {
            bool verified = await VerifyWorkerArtifactAsync(project, orchestration, cancellationToken);
            if (!verified)
            {
                return CalibrationApiResult<CalibrationOrchestrationStatusDto>.Success(Project(orchestration));
            }
        }

        if (orchestration.CurrentStep is not (CalibrationGenerationSteps.ComposingGcode or CalibrationGenerationSteps.Promoting))
        {
            return CalibrationApiResult<CalibrationOrchestrationStatusDto>.Success(Project(orchestration));
        }

        CalibrationGenerationResult<AnnotatedCalibrationGcode> annotated =
            BuildAnnotatedGcode(run, plan, resolved.Value!);
        if (!annotated.IsValid)
        {
            await FailTerminallyAsync(project, orchestration, annotated.Problems[0].Code, annotated.Problems, cancellationToken);
            return CalibrationApiResult<CalibrationOrchestrationStatusDto>.Success(Project(orchestration));
        }

        if (orchestration.CurrentStep == CalibrationGenerationSteps.ComposingGcode)
        {
            bool composed = await ComposeFinalGcodeAsync(
                project,
                orchestration,
                run,
                plan,
                annotated.Value!,
                cancellationToken);
            if (!composed)
            {
                return CalibrationApiResult<CalibrationOrchestrationStatusDto>.Success(Project(orchestration));
            }
        }

        if (orchestration.CurrentStep == CalibrationGenerationSteps.Promoting && !reconcileOnly)
        {
            await PromoteAsync(project, orchestration, run, plan, annotated.Value!, cancellationToken);
        }

        return CalibrationApiResult<CalibrationOrchestrationStatusDto>.Success(Project(orchestration));
    }

    private CalibrationGenerationResult<AnnotatedCalibrationGcode> BuildAnnotatedGcode(
        CalibrationRunContext run,
        OrcaCalibrationPlan plan,
        CalibrationValidatedModel model)
    {
        CalibrationGenerationResult<KlipperCalibrationProgram> generated =
            _gcodeGenerator.Generate(run.Specification, plan);
        return generated.IsValid
            ? _annotator.Annotate(run.Specification, plan, model, generated.Value!)
            : CalibrationGenerationResults.Failure<AnnotatedCalibrationGcode>(generated.Problems);
    }

    private async Task<CalibrationGenerationResult<CalibrationRunContext>> PrepareAsync(
        CalibrationProject project,
        CalibrationAttempt attempt,
        CalibrationOrchestration orchestration,
        CalibrationPinnedSlicerIdentity pinned,
        CancellationToken cancellationToken)
    {
        PrinterConfigurationSnapshot? snapshot = await _dbContext.PrinterConfigurationSnapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.Id == attempt.PrinterConfigurationSnapshotId,
                cancellationToken);
        if (snapshot is null)
        {
            return CalibrationGenerationResults.Failure<CalibrationRunContext>(
                CalibrationGenerationProblemCodes.ContextIdentityMissing,
                "attempt.printerConfigurationSnapshotId",
                "The immutable printer configuration snapshot is missing.");
        }

        long currentRevision = await _dbContext.Printers
            .AsNoTracking()
            .Where(printer => printer.Id == snapshot.PrinterId)
            .Select(printer => printer.ConfigurationRevision)
            .FirstOrDefaultAsync(cancellationToken);
        if (currentRevision == 0)
        {
            currentRevision = snapshot.PrinterConfigurationRevision;
        }

        CalibrationGenerationResult<CalibrationMethodOptions> bound = CalibrationMethodOptionsBinder.Bind(
            attempt.Method,
            attempt.DefinitionVersion,
            ReadStoredOptions(attempt));
        if (!bound.IsValid)
        {
            return CalibrationGenerationResults.Failure<CalibrationRunContext>(bound.Problems);
        }

        CalibrationModelReference? importedAsset = null;
        if (bound.Value is FinalVerificationCalibrationOptions verification)
        {
            Model3D? linked = _modelStorage is null
                ? null
                : await _modelStorage.FindOwnedAsync(
                    verification.Model3DId,
                    project.OwnerUserId,
                    cancellationToken);
            if (linked is null)
            {
                return CalibrationGenerationResults.Failure<CalibrationRunContext>(
                    CalibrationGenerationProblemCodes.LinkedAssetMissing,
                    "options.model3DId",
                    "The linked stored model does not exist or is not accessible.");
            }

            importedAsset = new CalibrationModelReference(
                linked.Id,
                NormalizeDigest(linked.FileHash),
                ResolveFormat(linked),
                ToSafeFileName(linked),
                linked.FileSizeBytes,
                "imported");
        }

        CalibrationGenerationResult<CalibrationGenerationContext> context =
            CalibrationGenerationContextFactory.Build(
                project,
                attempt,
                orchestration,
                snapshot,
                currentRevision,
                pinned,
                importedAsset);
        if (!context.IsValid)
        {
            return CalibrationGenerationResults.Failure<CalibrationRunContext>(context.Problems);
        }

        CalibrationGenerationResult<CalibrationSpecification> compiled =
            _specificationCompiler.Compile(context.Value!, bound.Value!);
        if (!compiled.IsValid)
        {
            return CalibrationGenerationResults.Failure<CalibrationRunContext>(compiled.Problems);
        }

        CalibrationSpecification specification = compiled.Value!;
        IReadOnlyList<CalibrationGenerationProblem> current =
            _specificationCompiler.VerifyStillCurrent(context.Value!, specification);
        if (current.Count > 0)
        {
            return CalibrationGenerationResults.Failure<CalibrationRunContext>(current);
        }

        if (!MatchesStoredSpecification(attempt, specification))
        {
            return CalibrationGenerationResults.Failure<CalibrationRunContext>(
                CalibrationGenerationProblemCodes.SpecificationHashMismatch,
                "attempt.specification",
                "The recompiled specification does not match the immutable attempt specification.");
        }

        if (orchestration.SpecificationSha256 is { Length: > 0 } pinnedSpecification &&
            !CalibrationCanonicalJson.DigestsMatch(pinnedSpecification, specification.Sha256))
        {
            return CalibrationGenerationResults.Failure<CalibrationRunContext>(
                CalibrationGenerationProblemCodes.SpecificationHashMismatch,
                "orchestration.specificationSha256",
                "The immutable context changed after this run was accepted.");
        }

        return CalibrationGenerationResults.Success(
            new CalibrationRunContext(context.Value!, specification, bound.Value!, snapshot));
    }

    private CalibrationMethodOptionsRequest? ReadStoredOptions(CalibrationAttempt attempt)
    {
        try
        {
            return JsonSerializer.Deserialize<CalibrationMethodOptionsRequest>(
                attempt.InputJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "Calibration attempt {AttemptId} carries unreadable typed options",
                attempt.Id);
            return null;
        }
    }

    private static bool MatchesStoredSpecification(
        CalibrationAttempt attempt,
        CalibrationSpecification specification)
    {
        if (!CalibrationCanonicalJson.DigestsMatch(attempt.SpecificationSha256, specification.Sha256))
        {
            return false;
        }

        try
        {
            using JsonDocument stored = JsonDocument.Parse(attempt.SpecificationJson);
            string canonical = Encoding.UTF8.GetString(
                CalibrationSnapshotBuilder.CanonicalizeToUtf8Bytes(stored.RootElement));
            return string.Equals(canonical, specification.CanonicalJson, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<CalibrationGenerationResult<CalibrationValidatedModel>> ResolveModelAsync(
        CalibrationProject project,
        CalibrationOrchestration orchestration,
        CalibrationRunContext run,
        CancellationToken cancellationToken)
    {
        if (run.Options is FinalVerificationCalibrationOptions verification)
        {
            ModelResolutionResult resolution = await _modelStorage!.OpenAsync(
                verification.Model3DId,
                project.OwnerUserId,
                expectedSha256: null,
                cancellationToken);
            if (!resolution.Succeeded)
            {
                return CalibrationGenerationResults.Failure<CalibrationValidatedModel>(
                    resolution.Failure == ModelResolutionFailure.HashMismatch
                        ? CalibrationGenerationProblemCodes.ModelHashMismatch
                        : CalibrationGenerationProblemCodes.LinkedAssetMissing,
                    "options.model3DId",
                    "The linked stored model could not be streamed from authorized storage.");
            }

            CalibrationModelReference reference = run.Specification.Document.ImportedAsset!;

            // IModelStorageResolver.OpenAsync documents that the caller owns the returned stream, so
            // disposing it here is required rather than a disposal of an injected dependency.
#pragma warning disable IDISP007
            await using Stream content = resolution.Content!.Content;
#pragma warning restore IDISP007
            StoredModelContentSource source = new(reference, content);
            CalibrationGenerationResult<CalibrationValidatedModel> imported =
                await _modelValidator.ValidateImportedAssetAsync(source, run.Specification, cancellationToken);
            if (imported.IsValid)
            {
                orchestration.Model3DId = verification.Model3DId;
            }

            return imported;
        }

        CalibrationGeneratedGeometry geometry = CalibrationBodyGeometryFactory.Build(run.Specification);
        CalibrationGenerationResult<CalibrationValidatedModel> validated =
            await _modelValidator.ValidateGeneratedGeometryAsync(geometry, run.Specification, cancellationToken);
        if (!validated.IsValid)
        {
            return validated;
        }

        Guid storedId = await EnsureGeneratedModelStoredAsync(
            project,
            orchestration,
            geometry,
            validated.Value!.Sha256,
            cancellationToken);
        orchestration.Model3DId = storedId;
        return CalibrationGenerationResults.Success(validated.Value! with { Model3DId = storedId });
    }

    private async Task<Guid> EnsureGeneratedModelStoredAsync(
        CalibrationProject project,
        CalibrationOrchestration orchestration,
        CalibrationGeneratedGeometry geometry,
        string contentSha256,
        CancellationToken cancellationToken)
    {
        string upperDigest = contentSha256.ToUpperInvariant();
        if (orchestration.Model3DId is { } existingId)
        {
            // Fail closed: only reuse the orchestration's previously stored model if it still
            // belongs to this project's owner. An unattributed or foreign-owned row is never
            // adopted, matching Model3DFileService's ownership check.
            Model3D? existing = await _models!.GetByIdAsync(existingId, cancellationToken);
            if (existing is not null && existing.UploadedByUserId == project.OwnerUserId)
            {
                return existing.Id;
            }
        }

        // Content addressed reuse: an interrupted run that already wrote the body finds it again
        // instead of storing a second identical model. Fail closed: an unattributed or
        // foreign-owned hash match is never adopted, only a model already owned by this
        // project's owner is reused.
        Model3D? byHash = await _models!.GetByHashAsync(upperDigest, cancellationToken);
        if (byHash is not null && byHash.UploadedByUserId == project.OwnerUserId)
        {
            return byHash.Id;
        }

        string root = _storagePaths.GetModelUploadDirectory();
        _ = Directory.CreateDirectory(root);
        Guid modelId = Guid.NewGuid();
        string storedFileName = $"{modelId:N}.stl";
        string storedPath = Path.Combine(root, storedFileName);
        bool persisted = false;
        bool preserveStagedBytes = false;
        try
        {
            await File.WriteAllBytesAsync(
                storedPath,
                geometry.Content.ToArray(),
                cancellationToken);

            DateTime nowUtc = UtcNow();
            Model3D model = new()
            {
                Id = modelId,
                Name = CalibrationBodyGeometryFactory.BuildStoredModelName(orchestration.AttemptId),
                FileName = storedFileName,
                FilePath = string.Empty,
                FileSizeBytes = geometry.Content.Length,

                // FileHash carries a global unique index, so a hash already recorded against a
                // foreign or unattributed row (byHash is not null here) cannot be reused verbatim for
                // this new row even though the bytes match: doing so would violate the unique
                // constraint. A per-model synthetic value keeps the insert unique. Worker delivery
                // still verifies the bytes against SliceJob.ModelSha256, which records the real
                // content digest and takes precedence over this legacy database key.
                FileHash = byHash is null ? upperDigest : modelId.ToString("N"),
                FileFormat = ModelFileFormat.STL,
                UploadedByUserId = project.OwnerUserId,
                UploadedAt = nowUtc,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc,
                IsValid = true,
            };
            await _models.AddAsync(model, cancellationToken);
            try
            {
                try
                {
                    await _models.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex) when (
                    byHash is null &&
                    IsModelFileHashUniqueConflict(ex))
                {
                    // A competing generator inserted the same real digest after our lookup. Keep this
                    // owner's staged bytes, but move the legacy unique key to the same synthetic
                    // per-model form used when the competing row was visible before the write.
                    model.FileHash = modelId.ToString("N");
                    await _models.SaveChangesAsync(cancellationToken);
                }
            }
            catch (DbUpdateException)
            {
                GeneratedModelSaveOutcome outcome =
                    await ReconcileGeneratedModelSaveAsync(model);
                if (outcome == GeneratedModelSaveOutcome.Committed)
                {
                    persisted = true;
                    return modelId;
                }

                preserveStagedBytes = outcome == GeneratedModelSaveOutcome.Uncertain;
                throw;
            }
            catch (DbException)
            {
                GeneratedModelSaveOutcome outcome =
                    await ReconcileGeneratedModelSaveAsync(model);
                if (outcome == GeneratedModelSaveOutcome.Committed)
                {
                    persisted = true;
                    return modelId;
                }

                preserveStagedBytes = outcome == GeneratedModelSaveOutcome.Uncertain;
                throw;
            }
            catch (OperationCanceledException)
            {
                GeneratedModelSaveOutcome outcome =
                    await ReconcileGeneratedModelSaveAsync(model);
                if (outcome == GeneratedModelSaveOutcome.Committed)
                {
                    persisted = true;
                    return modelId;
                }

                preserveStagedBytes = outcome == GeneratedModelSaveOutcome.Uncertain;
                throw;
            }

            persisted = true;
            return modelId;
        }
        finally
        {
            if (!persisted && !preserveStagedBytes)
            {
                DeleteUnpersistedGeneratedModel(storedPath, modelId);
            }
        }
    }

    private static bool IsModelFileHashUniqueConflict(DbUpdateException exception)
    {
        const string indexName = "IX_Models3D_FileHash";
        return exception.InnerException switch
        {
            SqliteException sqlite =>
                sqlite.SqliteErrorCode == 19
                && sqlite.SqliteExtendedErrorCode == 2067
                && sqlite.Message.Contains(
                    "UNIQUE constraint failed: Models3D.FileHash",
                    StringComparison.OrdinalIgnoreCase),
            PostgresException postgres =>
                postgres.SqlState == PostgresErrorCodes.UniqueViolation
                && string.Equals(postgres.ConstraintName, indexName, StringComparison.Ordinal),
            SqlException sqlServer =>
                sqlServer.Number is 2601 or 2627
                && NamesDelimitedIndex(sqlServer.Message, indexName),
            _ => false,
        };
    }

    private static bool NamesDelimitedIndex(string message, string indexName) =>
        message.Contains($"'{indexName}'", StringComparison.OrdinalIgnoreCase)
        || message.Contains($"\"{indexName}\"", StringComparison.OrdinalIgnoreCase)
        || message.Contains($"[{indexName}]", StringComparison.OrdinalIgnoreCase);

    private async Task<GeneratedModelSaveOutcome> ReconcileGeneratedModelSaveAsync(Model3D expected)
    {
        try
        {
            Model3D? stored = await _models!.GetByIdForReconciliationAsync(
                expected.Id,
                CancellationToken.None);
            if (stored is null)
            {
                return GeneratedModelSaveOutcome.Absent;
            }

            bool matches =
                stored.UploadedByUserId == expected.UploadedByUserId &&
                string.Equals(stored.FileName, expected.FileName, StringComparison.Ordinal) &&
                string.Equals(stored.FilePath, expected.FilePath, StringComparison.Ordinal) &&
                string.Equals(stored.FileHash, expected.FileHash, StringComparison.Ordinal) &&
                stored.FileSizeBytes == expected.FileSizeBytes &&
                stored.FileFormat == expected.FileFormat &&
                stored.IsValid == expected.IsValid;
            if (!matches)
            {
                _logger.LogWarning(
                    "Generated model {ModelId} was found after an unknown save outcome but did not match the staged owner, hash, or path",
                    expected.Id);
            }

            return matches
                ? GeneratedModelSaveOutcome.Committed
                : GeneratedModelSaveOutcome.Uncertain;
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(
                ex,
                "Could not reconcile generated model {ModelId} after an unknown save outcome",
                expected.Id);
            return GeneratedModelSaveOutcome.Uncertain;
        }
        catch (DbException ex)
        {
            _logger.LogWarning(
                ex,
                "Could not reconcile generated model {ModelId} after an unknown save outcome",
                expected.Id);
            return GeneratedModelSaveOutcome.Uncertain;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "Could not reconcile generated model {ModelId} after an unknown save outcome",
                expected.Id);
            return GeneratedModelSaveOutcome.Uncertain;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(
                ex,
                "Could not reconcile generated model {ModelId} after an unknown save outcome",
                expected.Id);
            return GeneratedModelSaveOutcome.Uncertain;
        }
    }

    private void DeleteUnpersistedGeneratedModel(string storedPath, Guid modelId)
    {
        try
        {
            File.Delete(storedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(
                ex,
                "Failed to remove unpersisted generated model bytes for {ModelId}",
                modelId);
        }
    }

    private enum GeneratedModelSaveOutcome
    {
        Absent,
        Committed,
        Uncertain,
    }

    private async Task SubmitSliceJobAsync(
        CalibrationProject project,
        CalibrationOrchestration orchestration,
        CalibrationRunContext run,
        CalibrationValidatedModel validatedModel,
        OrcaCalibrationPlan plan,
        CalibrationPinnedSlicerIdentity pinned,
        CancellationToken cancellationToken)
    {
        Guid correlationId = DeterministicGuid($"calibration-generation:correlation:{orchestration.Id:N}");
        string checksum = run.Specification.Sha256;

        // Unknown submit outcome is resolved from the correlated row before anything is submitted again.
        SliceJob? existing = await _sliceJobs!.FindExistingJobAsync(correlationId, checksum, cancellationToken);
        if (existing is null)
        {
            Model3D? model = await _models!.GetByIdAsync(orchestration.Model3DId!.Value, cancellationToken);
            if (model is null)
            {
                await ScheduleRetryAsync(
                    orchestration,
                    CalibrationGenerationProblemCodes.ModelStorageUnavailable,
                    [
                        new(
                            CalibrationGenerationProblemCodes.ModelStorageUnavailable,
                            "model",
                            "The resolved calibration model is no longer available."),
                    ],
                    cancellationToken);
                return;
            }

            Guid jobId = Guid.NewGuid();
            SliceJob job = new()
            {
                Id = jobId,
                UserId = project.OwnerUserId,
                PrinterId = run.Specification.Document.PrinterId,

                // The worker resolves bytes through the authenticated model route; no local absolute
                // path and no worker URL is ever persisted on the job.
                ModelFileUrl = $"/api/slice/{jobId}/model",
                ModelFileName = string.IsNullOrWhiteSpace(model.Name) ? model.FileName : model.Name,
                Model3DId = model.Id,
                ModelSha256 = validatedModel.Sha256.ToUpperInvariant(),
                SlicerEngine = (int)SlicerEngineType.OrcaSlicer,
                SlicerEngineName = SlicerEngineType.OrcaSlicer.ToString(),
                MachineProfileId = plan.MachineProfile.Id,
                ProcessProfileId = plan.ProcessProfile.Id,
                FilamentProfileId = plan.FilamentProfile.Id,

                // The worker receives the effective documents, never the baselines: a forbidden
                // command or notes value from an upstream profile is neutralized before it can
                // reach the slicer, and the delivered digest is the digest of what it receives.
                MachineProfileJson = plan.MachineProfile.EffectiveJson,
                ProcessProfileJson = plan.ProcessProfile.EffectiveJson,
                FilamentProfileJson = plan.FilamentProfile.EffectiveJson,
                MachineProfileSha256 = plan.MachineProfile.EffectiveSha256,
                ProcessProfileSha256 = plan.ProcessProfile.EffectiveSha256,
                FilamentProfileSha256 = plan.FilamentProfile.EffectiveSha256,
                SlicerDistribution = plan.Manifest.SlicerDistribution,
                SlicerVersion = plan.Manifest.SlicerVersion,
                SlicerContainerDigest = pinned.ContainerDigest,
                PinnedWorkerId = pinned.WorkerId,
                SlicerBinarySha256 = pinned.BinarySha256,
                RequiredCapabilitiesJson = JsonSerializer.Serialize(
                    new[] { CalibrationContractConstants.UpstreamSlicerCapability }),
                CalibrationProjectId = project.Id,
                IdempotencyScopeId = project.Id,
                CalibrationAttemptId = orchestration.AttemptId,
                CalibrationOrchestrationId = orchestration.Id,
                OperationId = DeterministicGuid($"calibration-generation:operation:{orchestration.OperationId}"),
                CorrelationId = correlationId,
                Checksum = checksum,
                Status = SliceJobStatus.Queued,
                Priority = 2,
                QueuedAt = UtcNow(),
                CreatedAt = UtcNow(),
                UpdatedAt = UtcNow(),
            };

            try
            {
                await _sliceJobs.AddAsync(job, cancellationToken);
                existing = job;
            }
            catch (DbUpdateException)
            {
                existing = await _sliceJobs.FindExistingJobAsync(correlationId, checksum, cancellationToken);
                if (existing is null)
                {
                    await ScheduleRetryAsync(
                        orchestration,
                        CalibrationGenerationProblemCodes.SliceSubmissionUnavailable,
                        [
                            new(
                                CalibrationGenerationProblemCodes.SliceSubmissionUnavailable,
                                "sliceJob",
                                "The canonical slice submission did not produce a durable job."),
                        ],
                        cancellationToken);
                    return;
                }
            }
        }

        orchestration.SliceJobId = existing.Id;
        await CheckpointAsync(orchestration, CalibrationGenerationSteps.AwaitingWorker, cancellationToken);
        await AppendEventAsync(project, orchestration, "slice-job-submitted", null, [], cancellationToken);
    }

    private async Task<bool> ObserveWorkerAsync(
        CalibrationProject project,
        CalibrationOrchestration orchestration,
        CancellationToken cancellationToken)
    {
        SliceJob? job = await _sliceJobs!.GetByIdAsync(orchestration.SliceJobId!.Value, cancellationToken);
        if (job is null)
        {
            await ScheduleRetryAsync(
                orchestration,
                CalibrationGenerationProblemCodes.SliceSubmissionUnavailable,
                [
                    new(
                        CalibrationGenerationProblemCodes.SliceSubmissionUnavailable,
                        "sliceJob",
                        "The submitted slice job is not readable."),
                ],
                cancellationToken);
            return false;
        }

        if (string.Equals(job.Status, SliceJobStatus.Completed, StringComparison.Ordinal))
        {
            orchestration.WorkerId = job.WorkerId;
            await CheckpointAsync(orchestration, CalibrationGenerationSteps.VerifyingArtifact, cancellationToken);
            return true;
        }

        if (string.Equals(job.Status, SliceJobStatus.Failed, StringComparison.Ordinal) ||
            string.Equals(job.Status, SliceJobStatus.Cancelled, StringComparison.Ordinal))
        {
            // The worker failure detail stays inside the slicer context; only the stable code crosses.
            await FailTerminallyAsync(
                project,
                orchestration,
                CalibrationGenerationProblemCodes.SliceJobFailed,
                [
                    new(
                        CalibrationGenerationProblemCodes.SliceJobFailed,
                        "sliceJob.status",
                        "The pinned worker did not complete the calibration slice job."),
                ],
                cancellationToken);
            return false;
        }

        DateTime nowUtc = UtcNow();
        orchestration.NextRetryAtUtc = nowUtc + WorkerPollDelay;
        orchestration.UpdatedAtUtc = nowUtc;
        orchestration.Revision++;
        _ = await _dbContext.SaveChangesAsync(cancellationToken);
        return false;
    }

    private async Task<bool> VerifyWorkerArtifactAsync(
        CalibrationProject project,
        CalibrationOrchestration orchestration,
        CancellationToken cancellationToken)
    {
        SliceJob? completedJob =
            await _sliceJobs!.GetByIdAsync(orchestration.SliceJobId!.Value, cancellationToken);
        IReadOnlyList<Guid> acceptedArtifactIds = ParseArtifactIds(completedJob?.ArtifactIdsCsv);
        IReadOnlyList<Artifact> produced =
            await _artifacts!.ListByJobAsync(orchestration.SliceJobId!.Value, cancellationToken);
        Dictionary<Guid, Artifact> producedById = produced.ToDictionary(artifact => artifact.Id);
        Artifact? sliced = completedJob?.WorkerId is { } workerId &&
            completedJob.ClaimToken is { } claimToken
                ? acceptedArtifactIds
                    .Select(id => producedById.GetValueOrDefault(id))
                    .FirstOrDefault(artifact =>
                        artifact is not null &&
                        string.Equals(
                            artifact.Kind,
                            SlicerArtifactKinds.Gcode,
                            StringComparison.OrdinalIgnoreCase) &&
                        artifact.WorkerId == workerId &&
                        artifact.ClaimToken == claimToken)
                : null;
        if (sliced is null)
        {
            await FailTerminallyAsync(
                project,
                orchestration,
                CalibrationGenerationProblemCodes.SliceArtifactMissing,
                [
                    new(
                        CalibrationGenerationProblemCodes.SliceArtifactMissing,
                        "artifact",
                        "The completed slice job produced no worker G-code artifact."),
                ],
                cancellationToken);
            return false;
        }

        await using ArtifactContentStream? content =
            await _artifacts.OpenReadStreamAsync(sliced.Id, cancellationToken);
        if (content is null)
        {
            await ScheduleRetryAsync(
                orchestration,
                CalibrationGenerationProblemCodes.SliceArtifactUnverifiable,
                [
                    new(
                        CalibrationGenerationProblemCodes.SliceArtifactUnverifiable,
                        "artifact.content",
                        "The worker artifact bytes are not currently readable."),
                ],
                cancellationToken);
            return false;
        }

        byte[] hash = await SHA256.HashDataAsync(content.Content, cancellationToken);
        string actual = Convert.ToHexString(hash);
        if (!CalibrationCanonicalJson.DigestsMatch(actual, sliced.Sha256) ||
            content.Content.Length != sliced.SizeBytes)
        {
            await FailTerminallyAsync(
                project,
                orchestration,
                CalibrationGenerationProblemCodes.SliceArtifactUnverifiable,
                [
                    new(
                        CalibrationGenerationProblemCodes.SliceArtifactUnverifiable,
                        "artifact.sha256",
                        "The worker artifact bytes do not match their recorded digest or size."),
                ],
                cancellationToken);
            return false;
        }

        orchestration.SourceArtifactId = sliced.Id;
        orchestration.WorkerId ??= sliced.WorkerId;
        await CheckpointAsync(orchestration, CalibrationGenerationSteps.ComposingGcode, cancellationToken);
        await AppendEventAsync(project, orchestration, "slice-artifact-verified", null, [], cancellationToken);
        return true;
    }

    private static List<Guid> ParseArtifactIds(string? artifactIdsCsv)
    {
        if (string.IsNullOrWhiteSpace(artifactIdsCsv))
        {
            return [];
        }

        string[] values = artifactIdsCsv.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var artifactIds = new List<Guid>(values.Length);
        foreach (string value in values)
        {
            if (!Guid.TryParse(value, out Guid artifactId))
            {
                return [];
            }

            artifactIds.Add(artifactId);
        }

        return artifactIds;
    }

    private async Task<bool> ComposeFinalGcodeAsync(
        CalibrationProject project,
        CalibrationOrchestration orchestration,
        CalibrationRunContext run,
        OrcaCalibrationPlan plan,
        AnnotatedCalibrationGcode final,
        CancellationToken cancellationToken)
    {
        CalibrationGenerationResult<CalibrationGcodeSafetyReport> safety = _safetyValidator.Validate(
            new CalibrationGcodeSafetyRequest(
                run.Specification,
                plan,
                final.Manifest,
                final.Gcode,
                CalibrationSafetyCheckpoint.BeforeArtifactCompletion,
                run.Context.CurrentPrinterConfigurationRevision,
                UtcNow()));
        if (!safety.IsValid)
        {
            await FailTerminallyAsync(project, orchestration, safety.Problems[0].Code, safety.Problems, cancellationToken);
            return false;
        }

        string expectedDigest = final.GcodeSha256.ToUpperInvariant();
        IReadOnlyList<Artifact> existing =
            await _artifacts!.ListByJobAsync(orchestration.SliceJobId!.Value, cancellationToken);

        // Unknown upload outcome is resolved by digest before a second artifact is ever written.
        Artifact? finalArtifact = existing.FirstOrDefault(artifact =>
            artifact.WorkerId is null &&
            string.Equals(artifact.Kind, SlicerArtifactKinds.Gcode, StringComparison.OrdinalIgnoreCase) &&
            CalibrationCanonicalJson.DigestsMatch(artifact.Sha256, expectedDigest));
        finalArtifact ??= await _artifacts.UploadTextAsync(
            final.Gcode,
            $"calibration-{orchestration.AttemptId:N}.gcode",
            orchestration.SliceJobId.Value,
            workerId: null,
            SlicerArtifactKinds.Gcode,
            cancellationToken);

        if (!existing.Any(artifact =>
                string.Equals(artifact.Kind, SlicerArtifactKinds.CalibrationManifest, StringComparison.OrdinalIgnoreCase) &&
                CalibrationCanonicalJson.DigestsMatch(artifact.Sha256, final.ManifestSha256)))
        {
            _ = await _artifacts.UploadTextAsync(
                final.ManifestJson,
                $"calibration-{orchestration.AttemptId:N}.manifest.json",
                orchestration.SliceJobId.Value,
                workerId: null,
                SlicerArtifactKinds.CalibrationManifest,
                cancellationToken);
        }

        orchestration.FinalArtifactId = finalArtifact.Id;
        orchestration.GcodeSha256 = final.GcodeSha256;
        orchestration.ManifestSha256 = final.ManifestSha256;
        await CheckpointAsync(orchestration, CalibrationGenerationSteps.Promoting, cancellationToken);
        await AppendEventAsync(project, orchestration, "gcode-annotated", null, [], cancellationToken);
        return true;
    }

    private async Task PromoteAsync(
        CalibrationProject project,
        CalibrationOrchestration orchestration,
        CalibrationRunContext run,
        OrcaCalibrationPlan plan,
        AnnotatedCalibrationGcode final,
        CancellationToken cancellationToken)
    {
        Artifact? finalArtifact = await _artifacts!.GetAsync(orchestration.FinalArtifactId!.Value, cancellationToken);
        if (finalArtifact is null)
        {
            await ScheduleRetryAsync(
                orchestration,
                CalibrationGenerationProblemCodes.PromotionUnavailable,
                [
                    new(
                        CalibrationGenerationProblemCodes.PromotionUnavailable,
                        "artifact",
                        "The verified calibration artifact is not currently readable."),
                ],
                cancellationToken);
            return;
        }

        string promotionOperationId = orchestration.PromotionOperationId ??
            $"calibration-generation:{orchestration.Id:N}";
        if (orchestration.PromotionOperationId is null)
        {
            orchestration.PromotionOperationId = promotionOperationId;
            await CheckpointAsync(orchestration, CalibrationGenerationSteps.Promoting, cancellationToken);
        }

        CalibrationActor owner = new(project.OwnerUserId, SystemSubject(project), false);

        // Reserve the bytes before re-reading them: cleanup must not reclaim the artifact between the
        // final safety validation and the promotion that consumes it.
        bool reserved = await _promoter.TryReserveSourceArtifactAsync(
            finalArtifact.Id,
            promotionOperationId,
            owner,
            cancellationToken);
        if (!reserved)
        {
            await ScheduleRetryAsync(
                orchestration,
                CalibrationGenerationProblemCodes.PromotionUnavailable,
                [
                    new(
                        CalibrationGenerationProblemCodes.PromotionUnavailable,
                        "artifact",
                        "The verified calibration artifact could not be reserved for promotion."),
                ],
                cancellationToken);
            return;
        }

        // The promoted bytes are re-read and statically validated again immediately before promotion,
        // so nothing that drifted between artifact completion and promotion can reach the library.
        ArtifactTextRead stored = await ReadArtifactTextAsync(finalArtifact.Id, cancellationToken);
        if (!stored.Available)
        {
            // Unreadable storage is a dependency outage, not malformed G-code: an empty string here
            // would be indistinguishable from a program with no instructions and would fail forever.
            await ScheduleRetryAsync(
                orchestration,
                CalibrationGenerationProblemCodes.PromotionUnavailable,
                [
                    new(
                        CalibrationGenerationProblemCodes.PromotionUnavailable,
                        "artifact.content",
                        "The verified calibration artifact bytes are not currently readable."),
                ],
                cancellationToken);
            return;
        }

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> promotionSafety = _safetyValidator.Validate(
            new CalibrationGcodeSafetyRequest(
                run.Specification,
                plan,
                final.Manifest,
                stored.Text,
                CalibrationSafetyCheckpoint.BeforePromotion,
                run.Context.CurrentPrinterConfigurationRevision,
                UtcNow()));
        if (!promotionSafety.IsValid)
        {
            // The artifact will never be promoted, so its reservation must not outlive the failure.
            await _promoter.ReleaseSourceArtifactReservationAsync(
                finalArtifact.Id,
                promotionOperationId,
                owner,
                cancellationToken);
            await FailTerminallyAsync(
                project,
                orchestration,
                promotionSafety.Problems[0].Code,
                promotionSafety.Problems,
                cancellationToken);
            return;
        }

        CalibrationApiResult<GcodePromotionDto> promotion = await _promoter.PromoteAsync(
            new GcodeArtifactPromotionRequest
            {
                OperationId = promotionOperationId,
                SourceArtifactId = finalArtifact.Id,
                SourceSliceJobId = orchestration.SliceJobId!.Value,
                ExpectedSha256 = finalArtifact.Sha256,
                ExpectedSizeBytes = finalArtifact.SizeBytes,
                ArtifactKind = SlicerArtifactKinds.Gcode,
                CalibrationProjectId = project.Id,
                CalibrationAttemptId = orchestration.AttemptId,
                CalibrationOrchestrationId = orchestration.Id,
            },
            owner,
            cancellationToken);

        if (!promotion.IsSuccess || promotion.Value is null)
        {
            if (promotion.StatusCode == StatusCodes.Status503ServiceUnavailable)
            {
                await ScheduleRetryAsync(
                    orchestration,
                    CalibrationGenerationProblemCodes.PromotionUnavailable,
                    [
                        new(
                            CalibrationGenerationProblemCodes.PromotionUnavailable,
                            "promotion",
                            "The artifact promotion hop is not currently available."),
                    ],
                    cancellationToken);
                return;
            }

            await FailTerminallyAsync(
                project,
                orchestration,
                CalibrationGenerationProblemCodes.PromotionRejected,
                [
                    new(
                        CalibrationGenerationProblemCodes.PromotionRejected,
                        "promotion",
                        "The artifact promotion boundary refused the verified calibration artifact."),
                ],
                cancellationToken);
            return;
        }

        DateTime nowUtc = UtcNow();
        orchestration.GcodeFileId = promotion.Value.GcodeFileId;
        orchestration.Status = CalibrationOrchestrationStatus.Completed;
        orchestration.CurrentStep = CalibrationGenerationSteps.Completed;
        orchestration.CompletedAtUtc = nowUtc;
        orchestration.NextRetryAtUtc = null;
        orchestration.LastErrorCode = null;
        orchestration.LastErrorJson = null;
        orchestration.StepStartedAtUtc = nowUtc;
        orchestration.UpdatedAtUtc = nowUtc;
        orchestration.Revision++;
        _ = await _dbContext.SaveChangesAsync(cancellationToken);
        await AppendEventAsync(project, orchestration, "generation-completed", null, [], cancellationToken);
    }

    /// <summary>
    /// Result of reading stored artifact text, distinguishing "storage did not answer" from "the
    /// artifact really is empty".
    /// </summary>
    /// <param name="Available">Whether the bytes were readable at all.</param>
    /// <param name="Text">The decoded content when it was readable.</param>
    private readonly record struct ArtifactTextRead(bool Available, string Text)
    {
        /// <summary>The stored bytes could not be opened.</summary>
        public static ArtifactTextRead Unavailable { get; } = new(false, string.Empty);
    }

    private async Task<ArtifactTextRead> ReadArtifactTextAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        ArtifactContentStream? content;
        try
        {
            content = await _artifacts!.OpenReadStreamAsync(artifactId, cancellationToken);
        }
        catch (IOException exception)
        {
            _logger.LogWarning(
                exception,
                "Calibration artifact {ArtifactId} bytes could not be opened and will be retried",
                artifactId);
            return ArtifactTextRead.Unavailable;
        }

        if (content is null)
        {
            return ArtifactTextRead.Unavailable;
        }

        await using ArtifactContentStream owned = content;
        try
        {
            using StreamReader reader = new(owned.Content, Encoding.UTF8, leaveOpen: true);
            return new ArtifactTextRead(true, await reader.ReadToEndAsync(cancellationToken));
        }
        catch (IOException exception)
        {
            _logger.LogWarning(
                exception,
                "Calibration artifact {ArtifactId} bytes could not be read and will be retried",
                artifactId);
            return ArtifactTextRead.Unavailable;
        }
    }

    private async Task<bool> TryAcquireLeaseAsync(
        CalibrationOrchestration orchestration,
        CancellationToken cancellationToken)
    {
        DateTime nowUtc = UtcNow();
        if (orchestration.LeaseExpiresAtUtc is { } expiry && expiry > nowUtc)
        {
            return false;
        }

        orchestration.LeaseOwner = _leaseOwner;
        orchestration.LeaseExpiresAtUtc = nowUtc + LeaseDuration;
        orchestration.UpdatedAtUtc = nowUtc;
        orchestration.Revision++;
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            _dbContext.ChangeTracker.Clear();
            return false;
        }
    }

    private async Task ReleaseLeaseAsync(
        CalibrationOrchestration orchestration,
        CancellationToken cancellationToken)
    {
        if (_dbContext.Entry(orchestration).State == EntityState.Detached)
        {
            return;
        }

        orchestration.LeaseOwner = null;
        orchestration.LeaseExpiresAtUtc = null;
        orchestration.UpdatedAtUtc = UtcNow();
        orchestration.Revision++;
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _dbContext.ChangeTracker.Clear();
        }
    }

    private async Task CheckpointAsync(
        CalibrationOrchestration orchestration,
        string nextStep,
        CancellationToken cancellationToken)
    {
        DateTime nowUtc = UtcNow();
        orchestration.CurrentStep = nextStep;
        orchestration.Status = CalibrationOrchestrationStatus.Running;
        orchestration.StepStartedAtUtc = nowUtc;
        orchestration.UpdatedAtUtc = nowUtc;
        orchestration.NextRetryAtUtc = null;
        orchestration.Revision++;
        _ = await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ScheduleRetryAsync(
        CalibrationOrchestration orchestration,
        string errorCode,
        IReadOnlyList<CalibrationGenerationProblem> problems,
        CancellationToken cancellationToken)
    {
        DateTime nowUtc = UtcNow();
        if (orchestration.RetryCount >= MaximumRetries)
        {
            await FailTerminallyAsync(null, orchestration, errorCode, problems, cancellationToken);
            return;
        }

        orchestration.RetryCount++;
        orchestration.Status = CalibrationOrchestrationStatus.WaitingToRetry;
        orchestration.LastErrorCode = errorCode;
        orchestration.LastErrorJson = SerializeProblems(problems);
        orchestration.NextRetryAtUtc = nowUtc + BackoffFor(orchestration.RetryCount);
        orchestration.UpdatedAtUtc = nowUtc;
        orchestration.Revision++;
        _ = await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task FailTerminallyAsync(
        CalibrationProject? project,
        CalibrationOrchestration orchestration,
        string errorCode,
        IReadOnlyList<CalibrationGenerationProblem> problems,
        CancellationToken cancellationToken)
    {
        DateTime nowUtc = UtcNow();
        orchestration.Status = CalibrationOrchestrationStatus.Failed;
        orchestration.CurrentStep = CalibrationGenerationSteps.Failed;
        orchestration.LastErrorCode = errorCode;
        orchestration.LastErrorJson = SerializeProblems(problems);
        orchestration.NextRetryAtUtc = null;
        orchestration.CompletedAtUtc = nowUtc;
        orchestration.UpdatedAtUtc = nowUtc;
        orchestration.Revision++;
        _ = await _dbContext.SaveChangesAsync(cancellationToken);

        project ??= await _dbContext.CalibrationProjects
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == orchestration.ProjectId, cancellationToken);
        if (project is not null)
        {
            await AppendEventAsync(project, orchestration, "generation-failed", errorCode, problems, cancellationToken);
        }
    }

    private async Task AppendEventAsync(
        CalibrationProject project,
        CalibrationOrchestration orchestration,
        string eventType,
        string? errorCode,
        IReadOnlyList<CalibrationGenerationProblem> problems,
        CancellationToken cancellationToken)
    {
        CalibrationActor actor = new(
            project.OwnerUserId,
            SystemSubject(project),
            false);
        JsonElement? error = problems.Count == 0
            ? null
            : JsonSerializer.SerializeToElement(
                new { problems = problems.Select(problem => new { problem.Code, problem.Field, problem.Message }) },
                ProblemJsonOptions);

        // The authoritative project service owns the attempt event and the change journal in one
        // transaction, so the durable status and the synchronization feed can never disagree.
        CalibrationApiResult<CalibrationAttemptEventDto> appended =
            await _projectService.AppendAttemptEventAsync(
                orchestration.AttemptId,
                new CalibrationAttemptEventCreateRequest
                {
                    ClientId = IdempotencyScope,
                    OperationId = $"{orchestration.OperationId}:{eventType}:{orchestration.RetryCount}",
                    EventType = eventType,
                    Model3DId = orchestration.Model3DId,
                    SliceJobId = orchestration.SliceJobId,
                    ArtifactId = orchestration.FinalArtifactId ?? orchestration.SourceArtifactId,
                    GcodeFileId = orchestration.GcodeFileId,
                    CalibrationOrchestrationId = orchestration.Id,
                    ErrorCode = errorCode,
                    Error = error,
                    RetryNumber = orchestration.RetryCount,
                },
                actor,
                cancellationToken);
        if (!appended.IsSuccess && !appended.Replayed)
        {
            _logger.LogWarning(
                "Calibration attempt event {EventType} for orchestration {OrchestrationId} was refused ({Code})",
                eventType,
                orchestration.Id,
                appended.Code);
        }
    }

    private async Task RecordIdempotencyAsync(
        CalibrationActor actor,
        CalibrationProject project,
        string operationId,
        string requestSha256,
        CalibrationOrchestration orchestration,
        int statusCode,
        CancellationToken cancellationToken)
    {
        string clientId = OwnerScope(actor);
        bool exists = await _dbContext.CalibrationIdempotencyRecords.AnyAsync(
            record => record.Scope == IdempotencyScope &&
                record.ClientId == clientId &&
                record.OperationId == operationId,
            cancellationToken);
        if (exists)
        {
            return;
        }

        DateTime nowUtc = UtcNow();
        _ = _dbContext.CalibrationIdempotencyRecords.Add(new CalibrationIdempotencyRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = actor.UserId,
            ProjectId = project.Id,
            Scope = IdempotencyScope,
            ClientId = clientId,
            OperationId = operationId,
            OperationType = IdempotencyScope,
            CanonicalRequestSha256 = requestSha256,
            ResourceType = "orchestration",
            ResourceId = orchestration.Id,
            StoredStatusCode = statusCode,
            StoredResultJson = null,
            State = CalibrationIdempotencyState.Completed,
            CreatedAtUtc = nowUtc,
            CompletedAtUtc = nowUtc,
        });
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent identical request won the unique index; its record is equivalent.
            _dbContext.ChangeTracker.Clear();
        }
    }

    private async Task<CalibrationApiResult<CalibrationOrchestrationStatusDto>?> FindReplayAsync(
        CalibrationActor actor,
        string operationId,
        string requestSha256,
        CalibrationOrchestration orchestration,
        CancellationToken cancellationToken)
    {
        string clientId = OwnerScope(actor);
        CalibrationIdempotencyRecord? record = await _dbContext.CalibrationIdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.Scope == IdempotencyScope &&
                    candidate.ClientId == clientId &&
                    candidate.OperationId == operationId,
                cancellationToken);
        if (record is null)
        {
            return null;
        }

        if (!string.Equals(record.CanonicalRequestSha256, requestSha256, StringComparison.Ordinal))
        {
            return Failure(StatusCodes.Status409Conflict, "idempotency_payload_mismatch");
        }

        return record.ResourceId != orchestration.Id
            ? Failure(StatusCodes.Status409Conflict, "incompatible_existing_operation")
            : CalibrationApiResult<CalibrationOrchestrationStatusDto>.Success(
                Project(orchestration),
                StatusCodes.Status200OK,
                replayed: true);
    }

    private static string OwnerScope(CalibrationActor actor) =>
        actor.IsFarmAdmin ? $"farm-admin:{actor.UserId:N}" : $"owner:{actor.UserId:N}";

    private static string SystemSubject(CalibrationProject project) =>
        string.Create(CultureInfo.InvariantCulture, $"system:calibration-generation:{project.Id:N}");

    private static string SerializeProblems(IReadOnlyList<CalibrationGenerationProblem> problems) =>
        JsonSerializer.Serialize(
            problems.Select(problem => new { problem.Code, problem.Field, problem.Message }),
            ProblemJsonOptions);

    private static TimeSpan BackoffFor(int retryCount)
    {
        double seconds = BaseRetryDelay.TotalSeconds * Math.Pow(2, Math.Max(0, retryCount - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaximumRetryDelay.TotalSeconds));
    }

    private static string ComputeRequestSha256(
        Guid projectId,
        Guid attemptId,
        CalibrationGenerateJobRequest request) =>
        CalibrationCanonicalJson.ComputeSha256(new
        {
            projectId,
            attemptId,
            method = request.Method?.Trim(),
            definitionVersion = request.DefinitionVersion?.Trim(),
            options = request.Options,
        });

    private static Guid DeterministicGuid(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string? NormalizeDigest(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string ResolveFormat(Model3D model)
    {
        string extension = Path.GetExtension(
            string.IsNullOrWhiteSpace(model.Name) ? model.FileName : model.Name);
        return extension.ToLowerInvariant() switch
        {
            ".3mf" => CalibrationModelFormats.ThreeMf,
            _ => CalibrationModelFormats.Stl,
        };
    }

    private static string ToSafeFileName(Model3D model)
    {
        string source = string.IsNullOrWhiteSpace(model.Name) ? model.FileName : model.Name;
        string baseName = Path.GetFileName(source.Replace('\\', '/'));
        string sanitized = string.Concat(baseName.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_'));
        return string.IsNullOrWhiteSpace(sanitized) ? "model.stl" : sanitized;
    }

    private static CalibrationApiResult<CalibrationOrchestrationStatusDto> Failure(int statusCode, string code) =>
        CalibrationApiResult<CalibrationOrchestrationStatusDto>.Failure(statusCode, code);

    private static CalibrationApiResult<CalibrationOrchestrationStatusDto> NotFound() =>
        Failure(StatusCodes.Status404NotFound, "calibration_resource_not_found");

    private static CalibrationApiResult<CalibrationOrchestrationStatusDto> Unprocessable(
        IReadOnlyList<CalibrationGenerationProblem> problems) =>
        new(
            StatusCodes.Status422UnprocessableEntity,
            "unsupported_or_unsafe_calibration_specification",
            new CalibrationOrchestrationStatusDto
            {
                Id = Guid.Empty,
                ProjectId = Guid.Empty,
                AttemptId = Guid.Empty,
                OperationId = string.Empty,
                Status = nameof(CalibrationOrchestrationStatus.Failed),
                CurrentStep = CalibrationGenerationSteps.ValidatingContext,
                Revision = 0,
                RetryCount = 0,
                Problems = MapProblems(problems),
                StatusRoute = string.Empty,
                CreatedAtUtc = default,
                UpdatedAtUtc = default,
            });

    private static IReadOnlyList<CalibrationGenerationProblemDto> MapProblems(
        IReadOnlyList<CalibrationGenerationProblem> problems) =>
        [.. problems.Select(problem => new CalibrationGenerationProblemDto(
            problem.Code,
            problem.Field,
            problem.Message))];

    /// <summary>Projects the durable row into the redacted public status document.</summary>
    /// <param name="orchestration">The durable orchestration row.</param>
    /// <returns>A status document carrying no path, host, key or raw log text.</returns>
    internal static CalibrationOrchestrationStatusDto Project(CalibrationOrchestration orchestration)
    {
        ArgumentNullException.ThrowIfNull(orchestration);
        return new CalibrationOrchestrationStatusDto
        {
            Id = orchestration.Id,
            ProjectId = orchestration.ProjectId,
            AttemptId = orchestration.AttemptId,
            OperationId = orchestration.OperationId,
            Status = orchestration.Status.ToString(),
            CurrentStep = orchestration.CurrentStep,
            Revision = orchestration.Revision,
            RetryCount = orchestration.RetryCount,
            NextRetryAtUtc = orchestration.NextRetryAtUtc,
            StepStartedAtUtc = orchestration.StepStartedAtUtc,
            LastErrorCode = orchestration.LastErrorCode,
            Problems = ReadProblems(orchestration.LastErrorJson),
            Model3DId = orchestration.Model3DId,
            SliceJobId = orchestration.SliceJobId,
            WorkerId = orchestration.WorkerId,
            SourceArtifactId = orchestration.SourceArtifactId,
            FinalArtifactId = orchestration.FinalArtifactId,
            GcodeFileId = orchestration.GcodeFileId,
            SpecificationSha256 = orchestration.SpecificationSha256,
            PlanManifestSha256 = orchestration.PlanManifestSha256,
            GcodeSha256 = orchestration.GcodeSha256,
            ManifestSha256 = orchestration.ManifestSha256,
            GeneratorVersion = orchestration.GeneratorVersion,
            SlicerContainerDigest = orchestration.SlicerContainerDigest,
            SlicerBinarySha256 = orchestration.SlicerBinarySha256,
            StatusRoute = BuildStatusRoute(orchestration.Id),
            CreatedAtUtc = orchestration.CreatedAtUtc,
            UpdatedAtUtc = orchestration.UpdatedAtUtc,
            CompletedAtUtc = orchestration.CompletedAtUtc,
        };
    }

    /// <summary>Builds the authenticated durable status route of an orchestration.</summary>
    /// <param name="orchestrationId">The orchestration identity.</param>
    /// <returns>The API-relative status route.</returns>
    public static string BuildStatusRoute(Guid orchestrationId) =>
        string.Create(CultureInfo.InvariantCulture, $"/api/calibration-orchestrations/{orchestrationId}");

    private static List<CalibrationGenerationProblemDto> ReadProblems(string? errorJson)
    {
        if (string.IsNullOrWhiteSpace(errorJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<CalibrationGenerationProblemDto>>(
                errorJson,
                ProblemJsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private sealed record CalibrationRunContext(
        CalibrationGenerationContext Context,
        CalibrationSpecification Specification,
        CalibrationMethodOptions Options,
        PrinterConfigurationSnapshot Snapshot);

    /// <summary>Adapts an authorized model stream to the validator's content source contract.</summary>
    /// <remarks>
    /// The stream stays owned by the caller that opened it, so the adapter never disposes bytes it did
    /// not acquire.
    /// </remarks>
    private sealed class StoredModelContentSource(
        CalibrationModelReference reference,
        Stream content) : ICalibrationModelContentSource
    {
        public Guid Model3DId { get; } = reference.Model3DId;

        public string? Sha256 { get; } = reference.Sha256;

        public string? Format { get; } = reference.Format;

        public string? SafeFileName { get; } = reference.SafeFileName;

        public string? Provenance { get; } = reference.Provenance;

        public Task<Stream> OpenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (content.CanSeek)
            {
                content.Position = 0;
            }

            return Task.FromResult(content);
        }
    }
}
