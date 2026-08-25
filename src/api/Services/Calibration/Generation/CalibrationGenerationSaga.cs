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
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>Orchestration statuses eligible for recovery scanning.</summary>
    private static readonly CalibrationOrchestrationStatus[] RecoverableStatuses =
    [
        CalibrationOrchestrationStatus.Running,
        CalibrationOrchestrationStatus.WaitingToRetry,
        CalibrationOrchestrationStatus.Pending,
    ];

    private static readonly JsonSerializerOptions ProblemJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly ICalibrationProjectService _projectService =
        projectService ?? throw new ArgumentNullException(nameof(projectService));

    private readonly ICalibrationGenerationCapabilityProbe _capabilityProbe =
        capabilityProbe ?? throw new ArgumentNullException(nameof(capabilityProbe));

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
        await AdvanceAsync(orchestrationId, cancellationToken);

    /// <inheritdoc/>
    public async Task<CalibrationApiResult<CalibrationOrchestrationStatusDto>> ReconcileAsync(
        Guid orchestrationId,
        CancellationToken cancellationToken) =>
        await AdvanceAsync(orchestrationId, cancellationToken);

    /// <inheritdoc/>
    public async Task<int> RecoverDueAsync(int maxOrchestrations, CancellationToken cancellationToken)
    {
        int limit = Math.Clamp(maxOrchestrations, 1, MaximumRecoveryBatch);
        DateTime nowUtc = UtcNow();
        List<Guid> due = await _dbContext.CalibrationOrchestrations
            .AsNoTracking()
            .Where(orchestration =>
                orchestration.GenerationRequestSha256 != null &&
                RecoverableStatuses.Contains(orchestration.Status) &&
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
                await AdvanceAsync(orchestrationId, cancellationToken);
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
            return await RunStepsAsync(orchestration, cancellationToken);
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

        // Path D (#1980): PrinterConfigurationSnapshot and the CalibrationOrchestration
        // generator-attestation columns were deleted. Every attempt created after Path D
        // (#1981) already has a null PrinterConfigurationSnapshotId, so this step already always
        // failed terminally here for all new work; the failure is now immediate instead of after
        // a lookup that could never succeed. The rest of the generation pipeline (model
        // resolution, plan compilation, slicing, promotion) is unreachable until the
        // filament-calibration saga (D7) replaces this snapshot-based context.
        await FailTerminallyAsync(
            project,
            orchestration,
            CalibrationGenerationProblemCodes.ContextIdentityMissing,
            [
                new(
                    CalibrationGenerationProblemCodes.ContextIdentityMissing,
                    "attempt.printerConfigurationSnapshotId",
                    "The printer configuration snapshot mechanism was removed; calibration generation is unavailable."),
            ],
            cancellationToken);
        return CalibrationApiResult<CalibrationOrchestrationStatusDto>.Success(Project(orchestration));
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
                    ArtifactId = null,
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
            GcodeFileId = orchestration.GcodeFileId,
            SpecificationSha256 = orchestration.SpecificationSha256,
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
}
