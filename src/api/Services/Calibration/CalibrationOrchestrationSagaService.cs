using System.Text.Json;
using System.Text.Json.Nodes;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services.Calibration;

/// <summary>
/// Canonical wire names for every step of the filament-calibration saga, in the order the saga
/// drives them.
/// </summary>
public static class CalibrationSagaSteps
{
    public const string Created = "created";
    public const string CloningProfile = "cloning-profile";
    public const string Slicing = "slicing";
    public const string AwaitingSlice = "awaiting-slice";
    public const string SendingToPrinter = "sending-to-printer";
    public const string AwaitingPrint = "awaiting-print";
    public const string AwaitingMeasurement = "awaiting-measurement";
    public const string ApplyingMeasurement = "applying-measurement";
    public const string Advancing = "advancing";
    public const string Completed = "completed";

    /// <summary>Every step, in the fixed order the saga always advances through.</summary>
    public static IReadOnlyList<string> Ordered { get; } =
    [
        Created,
        CloningProfile,
        Slicing,
        AwaitingSlice,
        SendingToPrinter,
        AwaitingPrint,
        AwaitingMeasurement,
        ApplyingMeasurement,
        Advancing,
        Completed,
    ];
}

/// <summary>Drives one <see cref="CalibrationOrchestration"/> checkpoint through the fixed saga.</summary>
public interface ICalibrationOrchestrationSagaService
{
    Task<CalibrationApiResult<CalibrationOrchestrationDto>> GetAsync(
        Guid orchestrationId,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    Task<CalibrationApiResult<CalibrationOrchestrationDto>> GetByAttemptAsync(
        Guid attemptId,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    Task<CalibrationApiResult<CalibrationOrchestrationDto>> AdvanceAsync(
        Guid orchestrationId,
        CalibrationOrchestrationAdvanceRequest request,
        CalibrationActor actor,
        CancellationToken cancellationToken);
}

/// <summary>
/// Fresh saga/step-machine service driving the ten-step filament-calibration flow against the
/// existing <see cref="CalibrationOrchestration"/> row created per-attempt by
/// <see cref="ICalibrationProjectService.CreateAttemptAsync"/>. Every transition is recorded as an
/// append-only <see cref="CalibrationAttemptEvent"/> (via <see cref="ICalibrationProjectService"/>,
/// never re-implemented here) - the orchestration row itself is a byproduct checkpoint, never a
/// gate an operator must satisfy before starting a calibration.
/// </summary>
/// <remarks>
/// Slicing and printer dispatch are integrated with the real <c>SliceJobController</c> and
/// <c>SlicePrintBridgeController</c> HTTP contracts through <see cref="ISliceSubmissionGateway"/>
/// and <see cref="IPrintDispatchGateway"/>, so this service never duplicates their submission,
/// rate-limiting, safety-validation, or dispatch logic. Slice jobs submitted here deliberately
/// omit <c>CalibrationProjectId</c>/<c>CalibrationAttemptId</c>/<c>CalibrationOrchestrationId</c>
/// on the <c>SliceJob</c> row itself (that combination is rejected by <c>SliceJobController</c>
/// when <c>Calibration.Method</c> is also set, and is reserved for the unrelated
/// promote-to-primary-queue calibration flow) - this saga's own linkage lives exclusively on
/// <see cref="CalibrationAttemptEvent.SliceJobId"/> and <see cref="CalibrationOrchestration.SliceJobId"/>.
/// </remarks>
public sealed class CalibrationOrchestrationSagaService(
    AppDbContext dbContext,
    ICalibrationProjectService calibrationService,
    ISliceSubmissionGateway sliceSubmissionGateway,
    IPrintDispatchGateway printDispatchGateway,
    TimeProvider timeProvider,
    ILogger<CalibrationOrchestrationSagaService> logger) : ICalibrationOrchestrationSagaService
{
    /// <summary>Bounded retry budget before a step's failure becomes terminal.</summary>
    public const int MaximumStepRetries = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly ICalibrationProjectService _calibrationService =
        calibrationService ?? throw new ArgumentNullException(nameof(calibrationService));

    private readonly ISliceSubmissionGateway _sliceSubmissionGateway =
        sliceSubmissionGateway ?? throw new ArgumentNullException(nameof(sliceSubmissionGateway));

    private readonly IPrintDispatchGateway _printDispatchGateway =
        printDispatchGateway ?? throw new ArgumentNullException(nameof(printDispatchGateway));

    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<CalibrationOrchestrationSagaService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<CalibrationApiResult<CalibrationOrchestrationDto>> GetAsync(
        Guid orchestrationId,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        CalibrationOrchestration? orchestration = await FindVisibleOrchestrationAsync(
            orchestrationId,
            actor,
            cancellationToken);
        return orchestration is null
            ? CalibrationApiResult<CalibrationOrchestrationDto>.Failure(
                StatusCodes.Status404NotFound,
                "calibration_orchestration_not_found")
            : CalibrationApiResult<CalibrationOrchestrationDto>.Success(MapOrchestration(orchestration));
    }

    /// <inheritdoc />
    public async Task<CalibrationApiResult<CalibrationOrchestrationDto>> GetByAttemptAsync(
        Guid attemptId,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        IQueryable<CalibrationOrchestration> query = _dbContext.CalibrationOrchestrations
            .Join(
                VisibleProjects(actor),
                orchestration => orchestration.ProjectId,
                project => project.Id,
                (orchestration, _) => orchestration);
        CalibrationOrchestration? orchestration = await query.SingleOrDefaultAsync(
            orchestration => orchestration.AttemptId == attemptId,
            cancellationToken);
        return orchestration is null
            ? CalibrationApiResult<CalibrationOrchestrationDto>.Failure(
                StatusCodes.Status404NotFound,
                "calibration_orchestration_not_found")
            : CalibrationApiResult<CalibrationOrchestrationDto>.Success(MapOrchestration(orchestration));
    }

    /// <inheritdoc />
    public async Task<CalibrationApiResult<CalibrationOrchestrationDto>> AdvanceAsync(
        Guid orchestrationId,
        CalibrationOrchestrationAdvanceRequest request,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.OperationId))
        {
            return CalibrationApiResult<CalibrationOrchestrationDto>.Failure(
                StatusCodes.Status400BadRequest,
                "calibration_orchestration_advance_invalid");
        }

        // A quick, unlocked existence/visibility check up front lets a caller for an unknown or
        // invisible orchestration fail fast (404) without ever contending on the per-orchestration
        // lock below. This MUST be AsNoTracking: AppDbContext is scoped per HTTP request, so this
        // check and the locked reload below run on the SAME DbContext instance. A tracking query
        // here would populate the change tracker, and EF Core's identity resolution would then
        // make the locked reload return that same in-memory instance instead of re-querying the
        // database - silently reintroducing the stale-snapshot race the lock exists to close.
        if (await FindVisibleOrchestrationAsync(orchestrationId, actor, cancellationToken, asNoTracking: true) is null)
        {
            return CalibrationApiResult<CalibrationOrchestrationDto>.Failure(
                StatusCodes.Status404NotFound,
                "calibration_orchestration_not_found");
        }

        // Serialize every advance attempt for this one orchestration within this process: two
        // concurrent Advance calls (e.g. a double-clicked retry, or overlapping polls) must never
        // both load the same "what step runs next" state and then both perform an external,
        // non-idempotent side effect (submitting a slice job, dispatching a print) before either
        // save can detect the conflict - by the time SaveChangesAsync notices, the side effect has
        // already happened. Everything that reads or acts on orchestration state - including the
        // reload immediately below - happens only after this lock is held, so a racing caller
        // that was waiting always observes the *other* caller's already-applied change, never a
        // stale snapshot taken before it. This lock closes that window for the common
        // single-process deployment; the DbUpdateConcurrencyException handling below is defense
        // in depth for the residual multi-instance case.
        SemaphoreSlim advanceLock = GetAdvanceLock(orchestrationId);
        await advanceLock.WaitAsync(cancellationToken);
        try
        {
            return await AdvanceLockedAsync(orchestrationId, request, actor, cancellationToken);
        }
        finally
        {
            _ = advanceLock.Release();
        }
    }

    private async Task<CalibrationApiResult<CalibrationOrchestrationDto>> AdvanceLockedAsync(
        Guid orchestrationId,
        CalibrationOrchestrationAdvanceRequest request,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        // Reload fresh now that the lock is held - a racer that was waiting must never act on a
        // snapshot taken before the previous holder's change was saved.
        CalibrationOrchestration? orchestration = await FindVisibleOrchestrationAsync(
            orchestrationId,
            actor,
            cancellationToken);
        if (orchestration is null)
        {
            return CalibrationApiResult<CalibrationOrchestrationDto>.Failure(
                StatusCodes.Status404NotFound,
                "calibration_orchestration_not_found");
        }

        if (orchestration.Status == CalibrationOrchestrationStatus.Failed)
        {
            // A terminal failure never blocks starting a new calibration attempt - it only means
            // this particular orchestration checkpoint stops advancing on its own. Also drop the
            // lock entry: GetAdvanceLock() re-adds a fresh semaphore on every call, so without
            // this an already-terminal orchestration that keeps getting polled would leak one
            // dictionary entry per poll instead of staying cleaned up.
            _ = AdvanceLocks.TryRemove(orchestrationId, out _);
            return CalibrationApiResult<CalibrationOrchestrationDto>.Failure(
                StatusCodes.Status409Conflict,
                "calibration_orchestration_terminally_failed");
        }

        if (orchestration.Status == CalibrationOrchestrationStatus.Completed ||
            orchestration.CurrentStep == CalibrationSagaSteps.Completed)
        {
            _ = AdvanceLocks.TryRemove(orchestrationId, out _);
            return CalibrationApiResult<CalibrationOrchestrationDto>.Success(MapOrchestration(orchestration));
        }

        CalibrationAttempt attempt = await _dbContext.CalibrationAttempts.SingleAsync(
            candidate => candidate.Id == orchestration.AttemptId,
            cancellationToken);
        CalibrationProject project = await _dbContext.CalibrationProjects.SingleAsync(
            candidate => candidate.Id == orchestration.ProjectId,
            cancellationToken);

        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        StepOutcome outcome = orchestration.CurrentStep switch
        {
            CalibrationSagaSteps.Created =>
                StepOutcome.Advance(CalibrationSagaSteps.CloningProfile, "step:created"),
            CalibrationSagaSteps.CloningProfile =>
                RunCloningProfileStep(attempt),
            CalibrationSagaSteps.Slicing =>
                await RunSlicingStepAsync(orchestration, attempt, cancellationToken),
            CalibrationSagaSteps.AwaitingSlice =>
                await RunAwaitingSliceStepAsync(orchestration, cancellationToken),
            CalibrationSagaSteps.SendingToPrinter =>
                await RunSendingToPrinterStepAsync(orchestration, project, cancellationToken),
            CalibrationSagaSteps.AwaitingPrint =>
                RunAwaitingPrintStep(orchestration, request),
            CalibrationSagaSteps.AwaitingMeasurement =>
                await RunAwaitingMeasurementStepAsync(orchestration, cancellationToken),
            CalibrationSagaSteps.ApplyingMeasurement =>
                RunApplyingMeasurementStep(),
            CalibrationSagaSteps.Advancing =>
                RunAdvancingStep(),
            _ => StepOutcome.NoChange(),
        };

        if (!outcome.Changed)
        {
            return CalibrationApiResult<CalibrationOrchestrationDto>.Success(MapOrchestration(orchestration));
        }

        // Append the timeline event through the *first*, dedicated save. AppendAttemptEventAsync
        // may clear the change tracker on a transient DbUpdateException while retrying its own
        // idempotency bookkeeping, so the orchestration row is mutated and saved only afterward,
        // against a freshly re-attached instance - never in the same tracked graph as the event
        // append, so a retry inside AppendAttemptEventAsync can never silently drop this
        // orchestration's own state change.
        if (outcome.EventType is not null)
        {
            CalibrationApiResult<CalibrationAttemptEventDto> appendResult = await _calibrationService.AppendAttemptEventAsync(
                attempt.Id,
                new CalibrationAttemptEventCreateRequest
                {
                    ClientId = request.ClientId,
                    OperationId = request.OperationId,
                    EventType = outcome.EventType,
                    SliceJobId = outcome.SliceJobId ?? orchestration.SliceJobId,
                    GcodeFileId = orchestration.GcodeFileId,
                    PrintJobId = orchestration.PrintJobId,
                    CalibrationOrchestrationId = orchestration.Id,
                    ErrorCode = outcome.ErrorCode,
                    Error = outcome.ErrorDetail is null
                        ? null
                        : JsonSerializer.SerializeToElement(new { detail = outcome.ErrorDetail }, JsonOptions),
                    RetryNumber = outcome.ErrorCode is null ? null : outcome.RetryCount ?? orchestration.RetryCount,
                    OccurredAtUtc = nowUtc,
                },
                actor,
                cancellationToken);

            // A non-throwing append failure (e.g. a validation or idempotency-payload mismatch)
            // must never let the orchestration advance without the promised timeline event
            // actually being recorded - the event *is* the audit trail this saga exists to keep.
            if (!appendResult.IsSuccess)
            {
                return CalibrationApiResult<CalibrationOrchestrationDto>.Failure(
                    appendResult.StatusCode,
                    appendResult.Code ?? "calibration_orchestration_event_append_failed");
            }

            orchestration = await _dbContext.CalibrationOrchestrations.SingleAsync(
                candidate => candidate.Id == orchestrationId,
                cancellationToken);
        }

        ApplyOutcome(orchestration, outcome, nowUtc);
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another Advance call for this same orchestration committed first. Report an
            // explicit, retryable conflict rather than letting the caller's write silently lose
            // (last-write-wins) or surfacing an unhandled 500.
            return CalibrationApiResult<CalibrationOrchestrationDto>.Failure(
                StatusCodes.Status409Conflict,
                "calibration_orchestration_advance_conflict");
        }

        if (outcome.Terminal)
        {
            _logger.LogWarning(
                "Calibration orchestration {OrchestrationId} failed terminally at step {Step}: {ErrorCode}",
                orchestration.Id,
                orchestration.CurrentStep,
                outcome.ErrorCode);
        }
        else if (outcome.WaitingToRetry)
        {
            _logger.LogInformation(
                "Calibration orchestration {OrchestrationId} retrying step {Step} (attempt {RetryCount}): {ErrorCode}",
                orchestration.Id,
                orchestration.CurrentStep,
                orchestration.RetryCount,
                outcome.ErrorCode);
        }

        if (outcome.Terminal || outcome.Completed)
        {
            // No further Advance call can legitimately progress this orchestration, so its
            // per-orchestration lock (and the bookkeeping entry backing it) can be released for
            // good - otherwise AdvanceLocks would grow for as long as the process lives.
            _ = AdvanceLocks.TryRemove(orchestration.Id, out _);
        }

        return CalibrationApiResult<CalibrationOrchestrationDto>.Success(MapOrchestration(orchestration));
    }

    /// <summary>Per-orchestration in-process serialization lock for <see cref="AdvanceAsync"/>.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> AdvanceLocks = new();

    private static SemaphoreSlim GetAdvanceLock(Guid orchestrationId) =>
        AdvanceLocks.GetOrAdd(orchestrationId, static _ => new SemaphoreSlim(1, 1));

    /// <summary>Validates the attempt's method against the saga's calibration-method catalogue.</summary>
    /// <remarks>
    /// The attempt is immutable, so an unparsable method is a terminal failure rather than a
    /// transient one - retrying the same parse can never succeed.
    /// </remarks>
    private static StepOutcome RunCloningProfileStep(CalibrationAttempt attempt) =>
        CalibrationMethodNames.TryParse(attempt.Method, out _)
            ? StepOutcome.Advance(CalibrationSagaSteps.Slicing, "step:cloning-profile")
            : StepOutcome.TerminalFailure("unknown_calibration_method", $"Method '{attempt.Method}' is not a recognized calibration method.");

    private async Task<StepOutcome> RunSlicingStepAsync(
        CalibrationOrchestration orchestration,
        CalibrationAttempt attempt,
        CancellationToken cancellationToken)
    {
        if (!CalibrationMethodNames.TryParse(attempt.Method, out CalibrationMethod method))
        {
            return StepOutcome.TerminalFailure(
                "unknown_calibration_method",
                $"Method '{attempt.Method}' is not a recognized calibration method.");
        }

        JsonNode requestBody = BuildSliceSubmissionBody(attempt, method);
        SliceSubmissionResult result = await _sliceSubmissionGateway.SubmitAsync(
            new CalibrationSliceSubmission(requestBody),
            cancellationToken);

        if (result.Success)
        {
            // Preserve (do not reset) the retry count here: a resubmission triggered by a prior
            // "awaiting-slice" failure is not genuine forward progress past the failure-prone
            // slicing/awaiting-slice pair - only reaching "sending-to-printer" is. Resetting to 0
            // on every successful resubmission would let a submit-succeeds/status-fails cycle
            // retry forever without ever exhausting the retry budget.
            return StepOutcome.Advance(
                CalibrationSagaSteps.AwaitingSlice,
                "step:slicing-submitted",
                sliceJobId: result.SliceJobId,
                retryCount: orchestration.RetryCount);
        }

        return StepOutcome.Retryable(
            orchestration.RetryCount,
            CalibrationSagaSteps.Slicing,
            result.ErrorCode ?? "slice_submission_failed",
            result.ErrorDetail);
    }

    private async Task<StepOutcome> RunAwaitingSliceStepAsync(
        CalibrationOrchestration orchestration,
        CancellationToken cancellationToken)
    {
        if (orchestration.SliceJobId is not Guid sliceJobId)
        {
            return StepOutcome.TerminalFailure("slice_job_missing", "No slice job was recorded for this orchestration.");
        }

        SliceStatusResult result = await _sliceSubmissionGateway.GetStatusAsync(sliceJobId, cancellationToken);
        if (!result.Success)
        {
            return StepOutcome.Retryable(
                orchestration.RetryCount,
                CalibrationSagaSteps.Slicing,
                result.ErrorCode ?? "slice_status_query_failed",
                result.ErrorDetail);
        }

        if (string.Equals(result.SliceStatus, "Completed", StringComparison.OrdinalIgnoreCase))
        {
            return StepOutcome.Advance(CalibrationSagaSteps.SendingToPrinter, "step:slicing-completed");
        }

        if (string.Equals(result.SliceStatus, "Failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.SliceStatus, "Cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return StepOutcome.Retryable(
                orchestration.RetryCount,
                CalibrationSagaSteps.Slicing,
                "slice_job_failed",
                $"Slice job reported status '{result.SliceStatus}'.");
        }

        // Still queued or processing: nothing changed yet, keep waiting.
        return StepOutcome.NoChange();
    }

    private async Task<StepOutcome> RunSendingToPrinterStepAsync(
        CalibrationOrchestration orchestration,
        CalibrationProject project,
        CancellationToken cancellationToken)
    {
        if (orchestration.SliceJobId is not Guid sliceJobId)
        {
            return StepOutcome.TerminalFailure("slice_job_missing", "No slice job was recorded for this orchestration.");
        }

        PrintDispatchResult result = await _printDispatchGateway.SendToPrinterAsync(
            sliceJobId,
            project.PrinterId,
            cancellationToken);
        if (result.Success)
        {
            return StepOutcome.Advance(CalibrationSagaSteps.AwaitingPrint, "step:sent-to-printer");
        }

        return StepOutcome.Retryable(
            orchestration.RetryCount,
            CalibrationSagaSteps.SendingToPrinter,
            result.ErrorCode ?? "send_to_printer_failed",
            result.ErrorDetail);
    }

    /// <summary>
    /// Advances past <c>awaiting-print</c> only from an explicit caller-supplied signal.
    /// </summary>
    /// <remarks>
    /// There is no automated print-progress feed this saga can consult yet, so the operator (or a
    /// future dedicated integration) reports completion the same way they already report a
    /// measurement: an explicit call, never a precondition gating the calibration itself.
    /// </remarks>
    private static StepOutcome RunAwaitingPrintStep(
        CalibrationOrchestration orchestration,
        CalibrationOrchestrationAdvanceRequest request)
    {
        if (request.PrintFailed == true)
        {
            return StepOutcome.Retryable(
                orchestration.RetryCount,
                CalibrationSagaSteps.AwaitingPrint,
                "print_failed",
                "The print was reported as failed.");
        }

        return request.PrintCompleted == true
            ? StepOutcome.Advance(CalibrationSagaSteps.AwaitingMeasurement, "step:print-completed")
            : StepOutcome.NoChange();
    }

    private async Task<StepOutcome> RunAwaitingMeasurementStepAsync(
        CalibrationOrchestration orchestration,
        CancellationToken cancellationToken)
    {
        bool hasObservation = await _dbContext.CalibrationObservations.AnyAsync(
            observation => observation.AttemptId == orchestration.AttemptId,
            cancellationToken);
        return hasObservation
            ? StepOutcome.Advance(CalibrationSagaSteps.ApplyingMeasurement, "step:measurement-received")
            : StepOutcome.NoChange();
    }

    private static StepOutcome RunApplyingMeasurementStep() =>
        StepOutcome.Advance(CalibrationSagaSteps.Advancing, "step:measurement-applied");

    private static StepOutcome RunAdvancingStep() =>
        StepOutcome.Advance(CalibrationSagaSteps.Completed, "step:advanced", completed: true);

    /// <summary>
    /// Builds the exact JSON body for <c>POST /api/slice</c> from the attempt's own recorded
    /// input, always overlaid with the resolved <c>calibration.method</c>/<c>calibration.params</c>
    /// fields so <c>SliceJobController</c> treats this submission as a genuine calibration slice
    /// (issue #1938/#1952) instead of an ordinary manual slice. Deliberately strips
    /// <c>calibrationProjectId</c>/<c>calibrationAttemptId</c>/<c>calibrationOrchestrationId</c>
    /// from whatever <see cref="CalibrationAttempt.InputJson"/> happens to carry: those three keys
    /// are reserved for the unrelated promote-to-primary-queue flow and
    /// <c>SliceJobController</c> rejects (<c>calibration_mode_conflicts_with_saga_ids</c>) any
    /// request that sets <c>calibration.method</c> alongside any of them, so this saga must never
    /// forward them even if a stored input happened to contain one.
    /// </summary>
    private static JsonObject BuildSliceSubmissionBody(CalibrationAttempt attempt, CalibrationMethod method)
    {
        JsonNode? parsedBody = JsonNode.Parse(attempt.InputJson);
        JsonObject bodyObject = parsedBody as JsonObject ?? [];
        bodyObject.Remove("calibrationProjectId");
        bodyObject.Remove("calibrationAttemptId");
        bodyObject.Remove("calibrationOrchestrationId");
        JsonNode? specification = JsonNode.Parse(attempt.SpecificationJson);
        bodyObject["calibration"] = new JsonObject
        {
            ["method"] = CalibrationMethodNames.ToName(method),
            ["params"] = specification?.DeepClone(),
        };
        return bodyObject;
    }

    private void ApplyOutcome(CalibrationOrchestration orchestration, StepOutcome outcome, DateTime nowUtc)
    {
        orchestration.CurrentStep = outcome.NextStep ?? orchestration.CurrentStep;

        // Overwrite, never merge-if-null: a re-slice after a failed/stale attempt must be able to
        // replace the previously recorded SliceJobId with the new one. `??=` would have left the
        // first (possibly failed) job's ID permanently stuck on the orchestration.
        orchestration.SliceJobId = outcome.SliceJobId ?? orchestration.SliceJobId;
        orchestration.LastErrorCode = outcome.ErrorCode;
        orchestration.LastErrorJson = outcome.ErrorDetail is null
            ? null
            : JsonSerializer.Serialize(new { detail = outcome.ErrorDetail }, JsonOptions);
        orchestration.RetryCount = outcome.RetryCount ?? orchestration.RetryCount;
        orchestration.NextRetryAtUtc = outcome.WaitingToRetry
            ? nowUtc + RetryBackoff(orchestration.RetryCount)
            : null;
        orchestration.Status = outcome.Terminal
            ? CalibrationOrchestrationStatus.Failed
            : outcome.WaitingToRetry
                ? CalibrationOrchestrationStatus.WaitingToRetry
                : outcome.Completed
                    ? CalibrationOrchestrationStatus.Completed
                    : CalibrationOrchestrationStatus.Running;
        orchestration.StepStartedAtUtc = nowUtc;
        orchestration.UpdatedAtUtc = nowUtc;
        orchestration.CompletedAtUtc = outcome.Completed ? nowUtc : orchestration.CompletedAtUtc;
        orchestration.Revision++;
    }

    private static TimeSpan RetryBackoff(int retryCount) =>
        TimeSpan.FromSeconds(Math.Min(30 * Math.Max(retryCount, 1), 300));

    private IQueryable<CalibrationProject> VisibleProjects(CalibrationActor actor)
    {
        IQueryable<CalibrationProject> query = _dbContext.CalibrationProjects
            .Where(project => project.DeletedAtUtc == null);
        return actor.IsFarmAdmin ? query : query.Where(project => project.OwnerUserId == actor.UserId);
    }

    /// <param name="asNoTracking">
    /// When true, the query is executed with <c>AsNoTracking</c> and never populates the
    /// DbContext's change tracker. This matters for the unlocked pre-lock existence/visibility
    /// check in <see cref="AdvanceAsync"/>: because <see cref="AppDbContext"/> is scoped per
    /// HTTP request, that check runs on the SAME DbContext instance the locked reload later uses.
    /// EF Core's identity resolution means a *tracking* query for an already-tracked key returns
    /// the in-memory instance without re-querying the database - so if the pre-lock check were
    /// allowed to track the entity, the "reload inside the lock" would silently return that same
    /// stale snapshot instead of actually re-reading the latest committed state, defeating the
    /// lock. Keeping the pre-lock check untracked ensures the locked reload is always a real,
    /// fresh database read.
    /// </param>
    private async Task<CalibrationOrchestration?> FindVisibleOrchestrationAsync(
        Guid orchestrationId,
        CalibrationActor actor,
        CancellationToken cancellationToken,
        bool asNoTracking = false)
    {
        IQueryable<CalibrationOrchestration> query = _dbContext.CalibrationOrchestrations
            .Join(
                VisibleProjects(actor),
                orchestration => orchestration.ProjectId,
                project => project.Id,
                (orchestration, _) => orchestration);
        if (asNoTracking)
        {
            query = query.AsNoTracking();
        }

        return await query.SingleOrDefaultAsync(
            orchestration => orchestration.Id == orchestrationId,
            cancellationToken);
    }

    private static CalibrationOrchestrationDto MapOrchestration(CalibrationOrchestration orchestration) => new(
        orchestration.Id,
        orchestration.ProjectId,
        orchestration.AttemptId,
        orchestration.CurrentStep,
        orchestration.Status.ToString(),
        orchestration.RetryCount,
        orchestration.NextRetryAtUtc,
        orchestration.LastErrorCode,
        orchestration.SliceJobId,
        orchestration.GcodeFileId,
        orchestration.PrintJobId,
        orchestration.Revision,
        orchestration.CreatedAtUtc,
        orchestration.UpdatedAtUtc,
        orchestration.CompletedAtUtc);

    /// <summary>Outcome of running one saga step, describing exactly how the checkpoint should change.</summary>
    private sealed record StepOutcome(
        bool Changed,
        string? NextStep,
        string? EventType,
        Guid? SliceJobId,
        string? ErrorCode,
        string? ErrorDetail,
        int? RetryCount,
        bool WaitingToRetry,
        bool Terminal,
        bool Completed)
    {
        public static StepOutcome NoChange() =>
            new(false, null, null, null, null, null, null, false, false, false);

        public static StepOutcome Advance(
            string nextStep,
            string eventType,
            Guid? sliceJobId = null,
            bool completed = false,
            int? retryCount = null) =>
            new(true, nextStep, eventType, sliceJobId, null, null, retryCount ?? 0, false, false, completed);

        /// <summary>A transient failure that stays on the same step and retries, or fails terminally past budget.</summary>
        public static StepOutcome Retryable(int currentRetryCount, string retryStep, string errorCode, string? detail)
        {
            int nextRetryCount = currentRetryCount + 1;
            bool exhausted = nextRetryCount > MaximumStepRetries;
            return new(
                true,
                retryStep,
                exhausted ? "step:failed" : "step:retrying",
                null,
                errorCode,
                detail,
                nextRetryCount,
                WaitingToRetry: !exhausted,
                Terminal: exhausted,
                Completed: false);
        }

        public static StepOutcome TerminalFailure(string errorCode, string? detail) =>
            new(true, null, "step:failed", null, errorCode, detail, null, false, true, false);
    }
}
