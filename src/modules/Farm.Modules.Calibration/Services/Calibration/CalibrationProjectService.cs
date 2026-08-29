using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Modules.Calibration.Contracts;
using Farm.Slicer.Module.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Farm.Modules.Calibration.Services.Calibration;

/// <summary>Authenticated caller context used for calibration resource authorization.</summary>
public sealed record CalibrationActor(Guid UserId, string Subject, bool IsFarmAdmin);

/// <summary>Stable application result used to map calibration outcomes to HTTP semantics.</summary>
public sealed record CalibrationApiResult<T>(
    int StatusCode,
    string? Code,
    T? Value,
    CalibrationRevisionConflictDto? Conflict = null,
    bool Replayed = false)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1000",
        Justification = "A generic success factory preserves strongly typed result construction.")]
    public static CalibrationApiResult<T> Success(
        T value,
        int statusCode = StatusCodes.Status200OK,
        bool replayed = false) =>
        new(statusCode, null, value, null, replayed);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1000",
        Justification = "A generic failure factory preserves strongly typed result construction.")]
    public static CalibrationApiResult<T> Failure(
        int statusCode,
        string code,
        CalibrationRevisionConflictDto? conflict = null) =>
        new(statusCode, code, default, conflict);
}

/// <summary>Coordinates calibration persistence and synchronization without any job generation or dispatch.</summary>
public interface ICalibrationProjectService
{
    Task<IReadOnlyList<CalibrationProjectDto>> GetProjectsAsync(
        CalibrationActor actor,
        bool includeDeleted,
        CancellationToken cancellationToken);

    Task<CalibrationApiResult<CalibrationProjectDto>> GetProjectAsync(
        Guid projectId,
        CalibrationActor actor,
        bool includeDeleted,
        CancellationToken cancellationToken);

    Task<CalibrationApiResult<CalibrationProjectDto>> CreateProjectAsync(
        CalibrationProjectCreateRequest request,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    Task<CalibrationApiResult<CalibrationProjectDto>> UpdateProjectAsync(
        Guid projectId,
        CalibrationProjectUpdateRequest request,
        string? ifMatch,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    Task<CalibrationApiResult<CalibrationProjectDto>> DeleteProjectAsync(
        Guid projectId,
        long? baseRevision,
        string? ifMatch,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    Task<CalibrationApiResult<CalibrationDraftDto>> UpsertDraftAsync(
        Guid projectId,
        string stepId,
        CalibrationDraftUpsertRequest request,
        string? ifMatch,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    Task<CalibrationApiResult<CalibrationDraftDto>> DeleteDraftAsync(
        Guid projectId,
        string stepId,
        string deviceLineageId,
        long? baseRevision,
        string? ifMatch,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Answers "is anything already underway on this project, and where was it started?" without
    /// requiring the caller to know any attempt or orchestration id up front. See
    /// <see cref="CalibrationInFlightStateDto"/> for how to interpret the result.
    /// </summary>
    Task<CalibrationApiResult<CalibrationInFlightStateDto>> GetInFlightAsync(
        Guid projectId,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CalibrationAttemptDto>> GetAttemptsAsync(
        Guid projectId,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    Task<CalibrationApiResult<CalibrationAttemptDto>> GetAttemptAsync(
        Guid attemptId,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    Task<CalibrationApiResult<CalibrationAttemptDto>> CreateAttemptAsync(
        Guid projectId,
        CalibrationAttemptCreateRequest request,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    Task<CalibrationApiResult<CalibrationAttemptEventDto>> AppendAttemptEventAsync(
        Guid attemptId,
        CalibrationAttemptEventCreateRequest request,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    Task<CalibrationApiResult<CalibrationObservationDto>> AppendObservationAsync(
        Guid attemptId,
        CalibrationObservationCreateRequest request,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CalibrationPhotoDto>> GetPhotosAsync(
        Guid attemptId,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    Task<CalibrationApiResult<CalibrationPhotoDto>> UploadPhotoAsync(
        Guid attemptId,
        string clientUploadId,
        string originalFileName,
        string declaredContentType,
        DateTime? capturedAtUtc,
        string? caption,
        int sortOrder,
        Stream content,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    Task<CalibrationApiResult<CalibrationPhotoDto>> GetPhotoAsync(
        Guid photoId,
        CalibrationActor actor,
        bool includeDeleted,
        CancellationToken cancellationToken);

    Task<CalibrationApiResult<CalibrationPhotoDto>> UpdatePhotoAsync(
        Guid photoId,
        CalibrationPhotoUpdateRequest request,
        string? ifMatch,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    Task<CalibrationApiResult<CalibrationPhotoDto>> DeletePhotoAsync(
        Guid photoId,
        long? baseRevision,
        string? ifMatch,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    Task<Stream> OpenPhotoAsync(
        Guid photoId,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    Task<CalibrationApiResult<CalibrationChangesResponse>> GetChangesAsync(
        string? after,
        int limit,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CalibrationSyncMutationResultDto>> ApplyChangesAsync(
        CalibrationSyncApplyRequest request,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    Task<CalibrationApiResult<LegacyCalibrationImportResultDto>> ImportLegacyV4Async(
        LegacyCalibrationImportRequest request,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    Task<int> ReconcilePendingPhotoDeletesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Application service for the calibration aggregate. It deliberately records
/// generation/dispatch identifiers only; producing G-code, queueing, and dispatch
/// remain owned by later bounded contexts.
/// </summary>
public sealed class CalibrationProjectService(
    AppDbContext dbContext,
    ICalibrationBlobStore blobStore,
    TimeProvider timeProvider,
    ILogger<CalibrationProjectService> logger)
    : ICalibrationProjectService
{
    private const int MaximumChangePageSize = 250;
    private const int MaximumAppendAttempts = 8;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Context-free stand-in for ValidateSelectedToolhead (#1981): its Toolheads list is always
    // empty, so any actual toolhead selection is rejected (no captured toolhead list to match
    // against), while an absent selection is still allowed - matching the method's own
    // unmodified short-circuit for that case.
    private static readonly CalibrationContextDto EmptyToolheadValidationContext = new(new CalibrationCandidateDto());

    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly ICalibrationBlobStore _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));

    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<CalibrationProjectService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly Dictionary<CalibrationChange, long> _pendingChangeOrders = [];
    private long _nextPendingChangeOrder;

    private sealed record MutationIdentity(
        string ClientId,
        string OperationId,
        string OperationType,
        string CanonicalRequestSha256,
        string ResourceType,
        Guid? ResourceId,
        string MismatchCode);

    /// <inheritdoc />
    public async Task<IReadOnlyList<CalibrationProjectDto>> GetProjectsAsync(
        CalibrationActor actor,
        bool includeDeleted,
        CancellationToken cancellationToken)
    {
        IQueryable<CalibrationProject> query = VisibleProjects(actor, includeDeleted)
            .AsNoTracking()
            .OrderByDescending(project => project.UpdatedAtUtc)
            .ThenBy(project => project.Id);
        return (await query.ToListAsync(cancellationToken)).Select(MapProject).ToArray();
    }

    /// <inheritdoc />
    public async Task<CalibrationApiResult<CalibrationProjectDto>> GetProjectAsync(
        Guid projectId,
        CalibrationActor actor,
        bool includeDeleted,
        CancellationToken cancellationToken)
    {
        CalibrationProject? project = await FindVisibleProjectAsync(
            projectId,
            actor,
            includeDeleted,
            cancellationToken);
        return project is null
            ? NotFound<CalibrationProjectDto>()
            : CalibrationApiResult<CalibrationProjectDto>.Success(MapProject(project));
    }

    /// <inheritdoc />
    public async Task<CalibrationApiResult<CalibrationInFlightStateDto>> GetInFlightAsync(
        Guid projectId,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        CalibrationProject? project = await FindVisibleProjectAsync(projectId, actor, false, cancellationToken);
        if (project is null)
        {
            return NotFound<CalibrationInFlightStateDto>();
        }

        // Attempts are append-only and retryable (ParentAttemptId), and each CreateAttemptAsync
        // call inserts its own orchestration row (CalibrationOrchestration.AttemptId is
        // unique-indexed per ATTEMPT, not per project) - so more than one non-terminal
        // orchestration CAN and does coexist for one project (e.g. a retry attempt started while
        // an earlier one is still mid-print). Picking "most recently touched" alone would let a
        // brand-new Pending retry mask an older Running orchestration that already has a
        // PrintJobId - exactly the wasted-filament case this endpoint exists to prevent. Load the
        // (small, per-project) candidate set and rank in memory: a physical print in progress
        // (PrintJobId set) always outranks everything else, then Running outranks
        // WaitingToRetry outranks Pending, then most recently touched, with a fully deterministic
        // tiebreak so results are stable across providers and ticks that share a timestamp.
        List<CalibrationOrchestration> nonTerminalOrchestrations = await _dbContext.CalibrationOrchestrations
            .AsNoTracking()
            .Where(candidate => candidate.ProjectId == projectId &&
                (candidate.Status == CalibrationOrchestrationStatus.Pending ||
                    candidate.Status == CalibrationOrchestrationStatus.Running ||
                    candidate.Status == CalibrationOrchestrationStatus.WaitingToRetry))
            .ToListAsync(cancellationToken);
        CalibrationOrchestration? orchestration = nonTerminalOrchestrations
            .OrderByDescending(InFlightPriority)
            .ThenByDescending(candidate => candidate.UpdatedAtUtc)
            .ThenByDescending(candidate => candidate.CreatedAtUtc)
            .ThenBy(candidate => candidate.Id)
            .FirstOrDefault();

        CalibrationDraftExistenceDto[] drafts = await _dbContext.CalibrationDrafts
            .AsNoTracking()
            .Where(candidate => candidate.ProjectId == projectId && candidate.DeletedAtUtc == null)
            .OrderByDescending(candidate => candidate.UpdatedAtUtc)
            .Select(candidate => new CalibrationDraftExistenceDto(
                candidate.StepId,
                candidate.DeviceLineageId,
                candidate.UpdatedAtUtc))
            .Take(MaxInFlightDrafts)
            .ToArrayAsync(cancellationToken);

        CalibrationInFlightStateDto dto = new(
            projectId,
            orchestration is null ? null : MapOrchestration(orchestration),
            drafts);
        return CalibrationApiResult<CalibrationInFlightStateDto>.Success(dto);
    }

    /// <summary>
    /// Ranking key for <see cref="GetInFlightAsync"/>: higher always wins. A physical print
    /// already underway must never be masked by a newer but less-advanced orchestration.
    /// </summary>
    private static int InFlightPriority(CalibrationOrchestration orchestration) =>
        (orchestration.PrintJobId.HasValue ? 100 : 0) +
        orchestration.Status switch
        {
            CalibrationOrchestrationStatus.Running => 3,
            CalibrationOrchestrationStatus.WaitingToRetry => 2,
            CalibrationOrchestrationStatus.Pending => 1,
            _ => 0,
        };

    /// <summary>
    /// Defensive cap on drafts returned by <see cref="GetInFlightAsync"/>: existence metadata is
    /// cheap, but an unbounded number of devices leaving stale drafts on one project should not
    /// let a single request load an unbounded result set into memory.
    /// </summary>
    private const int MaxInFlightDrafts = 200;

    /// <inheritdoc />
    public async Task<CalibrationApiResult<CalibrationProjectDto>> CreateProjectAsync(
        CalibrationProjectCreateRequest request,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? validationCode = ValidateProjectCreate(request);
        if (validationCode is not null)
        {
            return Validation<CalibrationProjectDto>(validationCode);
        }

        string requestHash = ComputeCanonicalHash(request);
        CalibrationApiResult<CalibrationProjectDto>? replay = await FindReplayAsync<CalibrationProjectDto>(
            actor,
            request.ClientId,
            request.RequestId,
            requestHash,
            cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        // Path D (#1981): filament calibration is context-free, so no printer configuration
        // context is resolved here. ValidateSelectedToolhead is unchanged and still short-circuits
        // to "allowed" when no toolhead selection is present (the filament-calibration case);
        // it is invoked against a fixed empty context so that any actual selection - which would
        // otherwise require a resolved toolhead list to validate against - is rejected.
        if (ValidateSelectedToolhead(request, EmptyToolheadValidationContext) is string toolheadCode)
        {
            return Validation<CalibrationProjectDto>(toolheadCode);
        }

        DateTime nowUtc = UtcNow();
        Guid projectId = Guid.NewGuid();
        CalibrationProject project = new()
        {
            Id = projectId,
            OwnerUserId = actor.UserId,
            Name = request.Name.Trim(),
            LifecycleStatus = CalibrationProjectLifecycleStatus.Active,
            ExperienceMode = ParseExperienceMode(request.ExperienceMode),
            PrinterId = request.PrinterId,
            SelectedToolheadId = request.SelectedToolheadId,
            SelectedToolheadIndex = request.SelectedToolheadIndex,
            FilamentProvider = request.FilamentProvider.Trim(),
            FilamentProductId = request.FilamentProductId.Trim(),
            FilamentSku = NormalizeOptional(request.FilamentSku),
            FilamentVendor = NormalizeOptional(request.FilamentVendor),
            FilamentProductName = request.FilamentProductName.Trim(),
            FilamentMaterial = request.FilamentMaterial.Trim(),
            FilamentDiameter = request.FilamentDiameter,
            FilamentColor = NormalizeOptional(request.FilamentColor),
            FilamentTypeId = request.FilamentTypeId,
            SpoolmanFilamentId = request.SpoolmanFilamentId,
            LocalSpoolId = request.LocalSpoolId,
            SpoolmanSpoolId = request.SpoolmanSpoolId,
            FilamentSnapshotJson = Json(request.FilamentSnapshot),
            OrderedStepsJson = Json(request.OrderedSteps),
            CurrentStep = NormalizeOptional(request.CurrentStep),
            CurrentSelectionsJson = Json(request.CurrentSelections),
            Revision = 1,
            CreateRequestId = request.RequestId,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            CreatedBySubject = actor.Subject,
            UpdatedBySubject = actor.Subject,
        };
        CalibrationProjectDto result = MapProject(project);
        _ = _dbContext.CalibrationProjects.Add(project);
        AddChange(project, "project", project.Id, project.Revision, CalibrationChangeType.Created, request.RequestId, actor);
        AddIdempotency(
            actor,
            project.Id,
            request.ClientId,
            request.RequestId,
            "project.create",
            requestHash,
            "project",
            project.Id,
            StatusCodes.Status201Created,
            result);

        try
        {
            _ = await SaveJournaledChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return await ResolveCreateRaceAsync<CalibrationProjectDto>(
                actor,
                request.ClientId,
                request.RequestId,
                requestHash,
                cancellationToken);
        }

        return CalibrationApiResult<CalibrationProjectDto>.Success(
            result,
            StatusCodes.Status201Created);
    }

    /// <inheritdoc />
    public Task<CalibrationApiResult<CalibrationProjectDto>> UpdateProjectAsync(
        Guid projectId,
        CalibrationProjectUpdateRequest request,
        string? ifMatch,
        CalibrationActor actor,
        CancellationToken cancellationToken) =>
        UpdateProjectInternalAsync(projectId, request, ifMatch, actor, null, cancellationToken);

    private async Task<CalibrationApiResult<CalibrationProjectDto>> UpdateProjectInternalAsync(
        Guid projectId,
        CalibrationProjectUpdateRequest request,
        string? ifMatch,
        CalibrationActor actor,
        MutationIdentity? mutationIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (mutationIdentity is not null)
        {
            CalibrationApiResult<CalibrationProjectDto>? replay =
                await FindReplayAsync<CalibrationProjectDto>(
                    actor,
                    mutationIdentity.ClientId,
                    mutationIdentity.OperationId,
                    mutationIdentity.CanonicalRequestSha256,
                    cancellationToken,
                    mutationIdentity.OperationType,
                    mutationIdentity.ResourceType,
                    mutationIdentity.ResourceId,
                    mutationIdentity.MismatchCode);
            if (replay is not null)
            {
                return replay;
            }
        }

        CalibrationProject? project = await FindVisibleProjectAsync(
            projectId,
            actor,
            false,
            cancellationToken);
        if (project is null)
        {
            return NotFound<CalibrationProjectDto>();
        }

        CalibrationApiResult<CalibrationProjectDto>? precondition =
            CheckPrecondition(project.Revision, project.Id, "project", request.BaseRevision, ifMatch, MapProject(project));
        if (precondition is not null)
        {
            return precondition;
        }

        string? normalizedName = null;
        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
            {
                return Validation<CalibrationProjectDto>("project_name_invalid");
            }

            normalizedName = request.Name.Trim();
        }

        CalibrationProjectLifecycleStatus? normalizedLifecycleStatus = null;
        if (request.LifecycleStatus is not null)
        {
            if (!Enum.TryParse(request.LifecycleStatus, true, out CalibrationProjectLifecycleStatus status))
            {
                return Validation<CalibrationProjectDto>("project_lifecycle_invalid");
            }

            normalizedLifecycleStatus = status;
        }

        string? orderedStepsJson = null;
        if (request.OrderedSteps.HasValue)
        {
            if (!IsJsonContainer(request.OrderedSteps.Value))
            {
                return Validation<CalibrationProjectDto>("ordered_steps_invalid");
            }

            if (ValidateSafeJson(request.OrderedSteps.Value) is string safetyCode)
            {
                return Validation<CalibrationProjectDto>(safetyCode);
            }

            orderedStepsJson = Json(request.OrderedSteps.Value);
        }

        string? currentSelectionsJson = null;
        if (request.CurrentSelections.HasValue)
        {
            if (!IsJsonContainer(request.CurrentSelections.Value))
            {
                return Validation<CalibrationProjectDto>("current_selections_invalid");
            }

            if (ValidateSafeJson(request.CurrentSelections.Value) is string safetyCode)
            {
                return Validation<CalibrationProjectDto>(safetyCode);
            }

            currentSelectionsJson = Json(request.CurrentSelections.Value);
        }

        if (normalizedName is not null)
        {
            project.Name = normalizedName;
        }

        if (normalizedLifecycleStatus.HasValue)
        {
            project.LifecycleStatus = normalizedLifecycleStatus.Value;
        }

        if (orderedStepsJson is not null)
        {
            project.OrderedStepsJson = orderedStepsJson;
        }

        if (currentSelectionsJson is not null)
        {
            project.CurrentSelectionsJson = currentSelectionsJson;
        }

        if (request.CurrentStep is not null)
        {
            project.CurrentStep = NormalizeOptional(request.CurrentStep);
        }

        project.CompletedAtUtc = request.CompletedAtUtc ?? project.CompletedAtUtc;
        project.Revision++;
        project.UpdatedAtUtc = UtcNow();
        project.UpdatedBySubject = actor.Subject;
        CalibrationProjectDto result = MapProject(project);
        AddChange(
            project,
            "project",
            project.Id,
            project.Revision,
            CalibrationChangeType.Updated,
            mutationIdentity?.OperationId ?? MutationId(),
            actor);
        if (mutationIdentity is not null)
        {
            AddIdempotency(
                actor,
                project.Id,
                mutationIdentity.ClientId,
                mutationIdentity.OperationId,
                mutationIdentity.OperationType,
                mutationIdentity.CanonicalRequestSha256,
                mutationIdentity.ResourceType,
                mutationIdentity.ResourceId,
                StatusCodes.Status200OK,
                result);
        }

        try
        {
            _ = await SaveJournaledChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (mutationIdentity is not null)
            {
                ClearTrackedState();
                CalibrationApiResult<CalibrationProjectDto>? replay =
                    await FindReplayAsync<CalibrationProjectDto>(
                        actor,
                        mutationIdentity.ClientId,
                        mutationIdentity.OperationId,
                        mutationIdentity.CanonicalRequestSha256,
                        cancellationToken,
                        mutationIdentity.OperationType,
                        mutationIdentity.ResourceType,
                        mutationIdentity.ResourceId,
                        mutationIdentity.MismatchCode);
                if (replay is not null)
                {
                    return replay;
                }
            }

            return await ProjectRevisionConflictAsync(
                projectId,
                request.BaseRevision,
                actor,
                cancellationToken);
        }
        catch (DbUpdateException) when (mutationIdentity is not null)
        {
            return await ResolveCreateRaceAsync<CalibrationProjectDto>(
                actor,
                mutationIdentity.ClientId,
                mutationIdentity.OperationId,
                mutationIdentity.CanonicalRequestSha256,
                cancellationToken,
                mutationIdentity.OperationType,
                mutationIdentity.ResourceType,
                mutationIdentity.ResourceId,
                mutationIdentity.MismatchCode);
        }

        return CalibrationApiResult<CalibrationProjectDto>.Success(result);
    }

    /// <inheritdoc />
    public Task<CalibrationApiResult<CalibrationProjectDto>> DeleteProjectAsync(
        Guid projectId,
        long? baseRevision,
        string? ifMatch,
        CalibrationActor actor,
        CancellationToken cancellationToken) =>
        DeleteProjectInternalAsync(projectId, baseRevision, ifMatch, actor, null, cancellationToken);

    private async Task<CalibrationApiResult<CalibrationProjectDto>> DeleteProjectInternalAsync(
        Guid projectId,
        long? baseRevision,
        string? ifMatch,
        CalibrationActor actor,
        MutationIdentity? mutationIdentity,
        CancellationToken cancellationToken)
    {
        if (mutationIdentity is not null)
        {
            CalibrationApiResult<CalibrationProjectDto>? replay =
                await FindReplayAsync<CalibrationProjectDto>(
                    actor,
                    mutationIdentity.ClientId,
                    mutationIdentity.OperationId,
                    mutationIdentity.CanonicalRequestSha256,
                    cancellationToken,
                    mutationIdentity.OperationType,
                    mutationIdentity.ResourceType,
                    mutationIdentity.ResourceId,
                    mutationIdentity.MismatchCode);
            if (replay is not null)
            {
                return replay;
            }
        }

        CalibrationProject? project = await FindVisibleProjectAsync(
            projectId,
            actor,
            false,
            cancellationToken);
        if (project is null)
        {
            return NotFound<CalibrationProjectDto>();
        }

        CalibrationApiResult<CalibrationProjectDto>? precondition =
            CheckPrecondition(project.Revision, project.Id, "project", baseRevision, ifMatch, MapProject(project));
        if (precondition is not null)
        {
            return precondition;
        }

        project.DeletedAtUtc = UtcNow();
        project.DeletedBySubject = actor.Subject;
        project.UpdatedAtUtc = project.DeletedAtUtc.Value;
        project.UpdatedBySubject = actor.Subject;
        project.Revision++;
        CalibrationProjectDto result = MapProject(project);
        AddChange(
            project,
            "project",
            project.Id,
            project.Revision,
            CalibrationChangeType.Deleted,
            mutationIdentity?.OperationId ?? MutationId(),
            actor,
            new { project.Id, project.DeletedAtUtc });
        if (mutationIdentity is not null)
        {
            AddIdempotency(
                actor,
                project.Id,
                mutationIdentity.ClientId,
                mutationIdentity.OperationId,
                mutationIdentity.OperationType,
                mutationIdentity.CanonicalRequestSha256,
                mutationIdentity.ResourceType,
                mutationIdentity.ResourceId,
                StatusCodes.Status200OK,
                result);
        }

        try
        {
            _ = await SaveJournaledChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (mutationIdentity is not null)
            {
                ClearTrackedState();
                CalibrationApiResult<CalibrationProjectDto>? replay =
                    await FindReplayAsync<CalibrationProjectDto>(
                        actor,
                        mutationIdentity.ClientId,
                        mutationIdentity.OperationId,
                        mutationIdentity.CanonicalRequestSha256,
                        cancellationToken,
                        mutationIdentity.OperationType,
                        mutationIdentity.ResourceType,
                        mutationIdentity.ResourceId,
                        mutationIdentity.MismatchCode);
                if (replay is not null)
                {
                    return replay;
                }
            }

            return await ProjectRevisionConflictAsync(
                projectId,
                baseRevision,
                actor,
                cancellationToken);
        }
        catch (DbUpdateException) when (mutationIdentity is not null)
        {
            return await ResolveCreateRaceAsync<CalibrationProjectDto>(
                actor,
                mutationIdentity.ClientId,
                mutationIdentity.OperationId,
                mutationIdentity.CanonicalRequestSha256,
                cancellationToken,
                mutationIdentity.OperationType,
                mutationIdentity.ResourceType,
                mutationIdentity.ResourceId,
                mutationIdentity.MismatchCode);
        }

        return CalibrationApiResult<CalibrationProjectDto>.Success(result);
    }

    /// <inheritdoc />
    public async Task<CalibrationApiResult<CalibrationDraftDto>> UpsertDraftAsync(
        Guid projectId,
        string stepId,
        CalibrationDraftUpsertRequest request,
        string? ifMatch,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(stepId) || stepId.Length > 128 ||
            string.IsNullOrWhiteSpace(request.DeviceLineageId) ||
            request.DeviceLineageId.Length > 128 ||
            string.IsNullOrWhiteSpace(request.Method) ||
            request.Method.Length > 128 ||
            !IsJsonContainer(request.Values) ||
            !IsJsonContainer(request.Prerequisites))
        {
            return Validation<CalibrationDraftDto>("draft_invalid");
        }

        if (ValidateSafeJson(request.Values) is string valuesSafetyCode)
        {
            return Validation<CalibrationDraftDto>(valuesSafetyCode);
        }

        if (ValidateSafeJson(request.Prerequisites) is string prerequisitesSafetyCode)
        {
            return Validation<CalibrationDraftDto>(prerequisitesSafetyCode);
        }

        CalibrationProject? project = await FindVisibleProjectAsync(
            projectId,
            actor,
            false,
            cancellationToken);
        if (project is null)
        {
            return NotFound<CalibrationDraftDto>();
        }

        string deviceLineageId = request.DeviceLineageId.Trim();
        string trimmedMethod = request.Method.Trim();
        CalibrationDraft? draft = await _dbContext.CalibrationDrafts.SingleOrDefaultAsync(
            candidate => candidate.ProjectId == projectId &&
                candidate.StepId == stepId &&
                candidate.DeviceLineageId == deviceLineageId &&
                candidate.DeletedAtUtc == null,
            cancellationToken);
        DateTime nowUtc = UtcNow();
        bool isNewDraft = draft is null;

        if (draft is null)
        {
            if (request.BaseRevision.HasValue || !string.IsNullOrWhiteSpace(ifMatch))
            {
                return Validation<CalibrationDraftDto>("draft_not_found_for_precondition");
            }
        }
        else
        {
            CalibrationApiResult<CalibrationDraftDto>? precondition =
                CheckPrecondition(draft.Revision, draft.Id, "draft", request.BaseRevision, ifMatch, MapDraft(draft));
            if (precondition is not null)
            {
                return precondition;
            }
        }

        // D8: enforce the canonical per-method step sequence whenever a step is first
        // recorded under a recognized method (canonical catalogue, D2/D8) — either as a
        // brand-new draft, or when an existing draft's method is being changed TO a
        // recognized method (which is equally an "advance": it is the first time this row
        // is asserting a reached step for that method). Unknown/legacy method strings are
        // left unchecked so this never gates on values this catalogue does not know about.
        // Re-editing a draft's values/prerequisites without changing its method is not an
        // "advance" and is never checked.
        bool methodChangingToRecognized = draft is not null &&
            !string.Equals(draft.Method, trimmedMethod, StringComparison.Ordinal);
        if ((isNewDraft || methodChangingToRecognized) &&
            CalibrationMethods.TryParse(trimmedMethod, out CalibrationMethod parsedMethod))
        {
            int requestedIndex = CalibrationMethodSteps.IndexOf(parsedMethod, stepId);
            List<string> existingStepIds = await _dbContext.CalibrationDrafts
                .Where(candidate => candidate.ProjectId == projectId &&
                    candidate.DeviceLineageId == deviceLineageId &&
                    candidate.Method == trimmedMethod &&
                    candidate.DeletedAtUtc == null)
                .Select(candidate => candidate.StepId)
                .ToListAsync(cancellationToken);
            int highestReachedIndex = existingStepIds
                .Select(existingStepId => CalibrationMethodSteps.IndexOf(parsedMethod, existingStepId))
                .DefaultIfEmpty(-1)
                .Max();

            if (requestedIndex < 0 || requestedIndex != highestReachedIndex + 1)
            {
                return Validation<CalibrationDraftDto>("step_out_of_sequence");
            }
        }

        if (draft is null)
        {
            draft = new CalibrationDraft
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                StepId = stepId,
                DeviceLineageId = deviceLineageId,
                Method = trimmedMethod,
                ValuesJson = Json(request.Values),
                PrerequisitesJson = Json(request.Prerequisites),
                Revision = 1,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
                CreatedBySubject = actor.Subject,
                UpdatedBySubject = actor.Subject,
            };
            _ = _dbContext.CalibrationDrafts.Add(draft);
            AddChange(project, "draft", draft.Id, draft.Revision, CalibrationChangeType.Created, MutationId(), actor);
        }
        else
        {
            draft.Method = trimmedMethod;
            draft.ValuesJson = Json(request.Values);
            draft.PrerequisitesJson = Json(request.Prerequisites);
            draft.Revision++;
            draft.UpdatedAtUtc = nowUtc;
            draft.UpdatedBySubject = actor.Subject;
            AddChange(project, "draft", draft.Id, draft.Revision, CalibrationChangeType.Updated, MutationId(), actor);
        }

        CalibrationDraftDto result = MapDraft(draft);
        try
        {
            _ = await SaveJournaledChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException) when (!isNewDraft)
        {
            return await DraftRevisionConflictAsync(
                draft.Id,
                request.BaseRevision,
                actor,
                cancellationToken);
        }
        catch (DbUpdateException) when (isNewDraft)
        {
            return await ResolveDraftCreateRaceAsync(
                projectId,
                stepId,
                deviceLineageId,
                request,
                actor,
                cancellationToken);
        }

        return CalibrationApiResult<CalibrationDraftDto>.Success(result);
    }

    /// <inheritdoc />
    public async Task<CalibrationApiResult<CalibrationDraftDto>> DeleteDraftAsync(
        Guid projectId,
        string stepId,
        string deviceLineageId,
        long? baseRevision,
        string? ifMatch,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        CalibrationProject? project = await FindVisibleProjectAsync(
            projectId,
            actor,
            false,
            cancellationToken);
        if (project is null)
        {
            return NotFound<CalibrationDraftDto>();
        }

        CalibrationDraft? draft = await _dbContext.CalibrationDrafts.SingleOrDefaultAsync(
            candidate => candidate.ProjectId == projectId &&
                candidate.StepId == stepId &&
                candidate.DeviceLineageId == deviceLineageId &&
                candidate.DeletedAtUtc == null,
            cancellationToken);
        if (draft is null)
        {
            return NotFound<CalibrationDraftDto>();
        }

        CalibrationApiResult<CalibrationDraftDto>? precondition =
            CheckPrecondition(draft.Revision, draft.Id, "draft", baseRevision, ifMatch, MapDraft(draft));
        if (precondition is not null)
        {
            return precondition;
        }

        draft.DeletedAtUtc = UtcNow();
        draft.UpdatedAtUtc = draft.DeletedAtUtc.Value;
        draft.Revision++;
        CalibrationDraftDto result = MapDraft(draft);
        AddChange(
            project,
            "draft",
            draft.Id,
            draft.Revision,
            CalibrationChangeType.Deleted,
            MutationId(),
            actor,
            new { draft.Id, draft.ProjectId, draft.StepId, draft.DeviceLineageId, draft.DeletedAtUtc });
        try
        {
            _ = await SaveJournaledChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await DraftRevisionConflictAsync(draft.Id, baseRevision, actor, cancellationToken);
        }

        return CalibrationApiResult<CalibrationDraftDto>.Success(result);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CalibrationAttemptDto>> GetAttemptsAsync(
        Guid projectId,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        CalibrationProject? project = await FindVisibleProjectAsync(projectId, actor, false, cancellationToken);
        if (project is null)
        {
            return [];
        }

        CalibrationAttempt[] attempts = await _dbContext.CalibrationAttempts
            .AsNoTracking()
            .Where(attempt => attempt.ProjectId == projectId)
            .OrderBy(attempt => attempt.Sequence)
            .ToArrayAsync(cancellationToken);
        Dictionary<Guid, string> statuses = await GetAttemptStatusesAsync(
            attempts.Select(attempt => attempt.Id).ToArray(),
            cancellationToken);
        return attempts.Select(attempt => MapAttempt(attempt, statuses.GetValueOrDefault(attempt.Id, "planned"))).ToArray();
    }

    /// <inheritdoc />
    public async Task<CalibrationApiResult<CalibrationAttemptDto>> GetAttemptAsync(
        Guid attemptId,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        CalibrationAttempt? attempt = await FindVisibleAttemptAsync(attemptId, actor, cancellationToken);
        if (attempt is null)
        {
            return NotFound<CalibrationAttemptDto>();
        }

        string status = await GetAttemptStatusAsync(attemptId, cancellationToken);
        return CalibrationApiResult<CalibrationAttemptDto>.Success(MapAttempt(attempt, status));
    }

    /// <inheritdoc />
    public async Task<CalibrationApiResult<CalibrationAttemptDto>> CreateAttemptAsync(
        Guid projectId,
        CalibrationAttemptCreateRequest request,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.RequestId) ||
            string.IsNullOrWhiteSpace(request.CalibrationKind) || string.IsNullOrWhiteSpace(request.Method) ||
            string.IsNullOrWhiteSpace(request.DefinitionVersion) || !IsJsonContainer(request.Input) ||
            !IsJsonContainer(request.Specification) || !IsJsonContainer(request.ProfileSnapshotIds))
        {
            return Validation<CalibrationAttemptDto>("attempt_invalid");
        }

        string? payloadSafetyCode = new[]
        {
            request.Input,
            request.Specification,
            request.ProfileSnapshotIds,
        }
            .Select(ValidateSafeJson)
            .FirstOrDefault(code => code is not null);
        if (payloadSafetyCode is not null)
        {
            return Validation<CalibrationAttemptDto>(payloadSafetyCode);
        }

        if (request.ActualSpoolSnapshot.HasValue &&
            ValidateSafeJson(request.ActualSpoolSnapshot.Value) is string spoolSafetyCode)
        {
            return Validation<CalibrationAttemptDto>(spoolSafetyCode);
        }

        CalibrationProject? project = await FindVisibleProjectAsync(projectId, actor, false, cancellationToken);
        if (project is null)
        {
            return NotFound<CalibrationAttemptDto>();
        }

        // Bind the route resource identity (parent project) and operation type into the canonical
        // request hash so replaying the same operation ID against a different project route target
        // produces a deterministic idempotency_payload_mismatch instead of returning a stored
        // response belonging to the first project. Mirrors CreateMutationIdentity for the sync lane.
        string requestHash = ComputeDirectOperationHash("attempt.create", "project", projectId, request);
        CalibrationApiResult<CalibrationAttemptDto>? replay = await FindReplayAsync<CalibrationAttemptDto>(
            actor,
            request.ClientId,
            request.RequestId,
            requestHash,
            cancellationToken,
            operationType: "attempt.create");
        if (replay is not null)
        {
            return replay;
        }

        if (request.ParentAttemptId.HasValue &&
            !await _dbContext.CalibrationAttempts.AnyAsync(
                attempt => attempt.Id == request.ParentAttemptId && attempt.ProjectId == projectId,
                cancellationToken))
        {
            return Validation<CalibrationAttemptDto>("parent_attempt_not_found");
        }

        // Path D (#1981): filament calibration is context-free, so no printer configuration
        // context is resolved here. The PrinterConfigurationSnapshot entity that used to carry
        // that optional/best-effort linkage was deleted entirely in #1989 (D3b); any pre-D4
        // attempt that still carried a snapshot FK had that historical linkage discarded along
        // with the table.
        for (int appendAttempt = 0; appendAttempt < MaximumAppendAttempts; appendAttempt++)
        {
            long nextSequence = (await _dbContext.CalibrationAttempts
                .Where(attempt => attempt.ProjectId == projectId)
                .Select(attempt => attempt.Sequence)
                .ToListAsync(cancellationToken))
                .DefaultIfEmpty()
                .Max() + 1;
            Guid attemptId = Guid.NewGuid();
            DateTime nowUtc = UtcNow();
            CalibrationAttempt attempt = new()
            {
                Id = attemptId,
                ProjectId = projectId,
                Sequence = nextSequence,
                ParentAttemptId = request.ParentAttemptId,
                CalibrationKind = request.CalibrationKind.Trim(),
                Method = request.Method.Trim(),
                DefinitionVersion = request.DefinitionVersion.Trim(),
                InputJson = Json(request.Input),
                SpecificationJson = Json(request.Specification),
                SpecificationSha256 = ComputeCanonicalHash(request.Specification),
                ProfileSnapshotIdsJson = Json(request.ProfileSnapshotIds),
                ActualSpoolSnapshotJson = request.ActualSpoolSnapshot.HasValue
                    ? Json(request.ActualSpoolSnapshot.Value)
                    : null,
                AttemptRequestId = request.RequestId.Trim(),
                CreatedAtUtc = nowUtc,
                CreatedBySubject = actor.Subject,
            };
            CalibrationOrchestration orchestration = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                AttemptId = attemptId,
                CurrentStep = "created",
                Status = CalibrationOrchestrationStatus.Pending,
                OperationId = request.RequestId.Trim(),
                Revision = 1,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
            };
            CalibrationAttemptDto result = MapAttempt(attempt, "planned");
            _ = _dbContext.CalibrationAttempts.Add(attempt);
            _ = _dbContext.CalibrationOrchestrations.Add(orchestration);
            AddChange(
                project,
                "attempt",
                attempt.Id,
                attempt.Sequence,
                CalibrationChangeType.Created,
                request.RequestId,
                actor);
            AddChange(
                project,
                "orchestration",
                orchestration.Id,
                orchestration.Revision,
                CalibrationChangeType.Created,
                MutationId(),
                actor);
            AddIdempotency(
                actor,
                projectId,
                request.ClientId,
                request.RequestId,
                "attempt.create",
                requestHash,
                "attempt",
                attemptId,
                StatusCodes.Status201Created,
                result);

            try
            {
                _ = await SaveJournaledChangesAsync(cancellationToken);
                return CalibrationApiResult<CalibrationAttemptDto>.Success(
                    result,
                    StatusCodes.Status201Created);
            }
            catch (Exception exception) when (exception is DbUpdateException or DbException)
            {
                ClearTrackedState();
                replay = await FindReplayAsync<CalibrationAttemptDto>(
                    actor,
                    request.ClientId,
                    request.RequestId,
                    requestHash,
                    cancellationToken,
                    operationType: "attempt.create");
                if (replay is not null)
                {
                    return replay;
                }

                if (appendAttempt == MaximumAppendAttempts - 1)
                {
                    throw;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10 * (appendAttempt + 1)), cancellationToken);
            }
        }

        throw new InvalidOperationException("The calibration attempt append retry budget was exhausted.");
    }

    /// <inheritdoc />
    public async Task<CalibrationApiResult<CalibrationAttemptEventDto>> AppendAttemptEventAsync(
        Guid attemptId,
        CalibrationAttemptEventCreateRequest request,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.OperationId) ||
            string.IsNullOrWhiteSpace(request.EventType) ||
            (request.Error.HasValue && !IsJsonContainer(request.Error.Value)))
        {
            return Validation<CalibrationAttemptEventDto>("attempt_event_invalid");
        }

        if (request.Error.HasValue && ValidateSafeJson(request.Error.Value) is string errorSafetyCode)
        {
            return Validation<CalibrationAttemptEventDto>(errorSafetyCode);
        }

        CalibrationAttempt? attempt = await FindVisibleAttemptAsync(attemptId, actor, cancellationToken);
        if (attempt is null)
        {
            return NotFound<CalibrationAttemptEventDto>();
        }

        // Bind the route resource identity (parent attempt) and operation type into the canonical
        // request hash so replaying the same operation ID against a different attempt route target
        // produces a deterministic idempotency_payload_mismatch instead of returning a stored
        // response belonging to the first attempt.
        string requestHash = ComputeDirectOperationHash("attempt.event.append", "attempt", attemptId, request);
        CalibrationApiResult<CalibrationAttemptEventDto>? replay =
            await FindReplayAsync<CalibrationAttemptEventDto>(
                actor,
                request.ClientId,
                request.OperationId,
                requestHash,
                cancellationToken,
                operationType: "attempt.event.append");
        if (replay is not null)
        {
            return replay;
        }

        for (int appendAttempt = 0; appendAttempt < MaximumAppendAttempts; appendAttempt++)
        {
            long sequence = await NextEventSequenceAsync(attemptId, cancellationToken);
            CalibrationAttemptEvent @event = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = attempt.ProjectId,
                AttemptId = attempt.Id,
                Sequence = sequence,
                EventType = request.EventType.Trim(),
                DerivedStatus = DeriveAttemptStatus(request.EventType),
                Model3DId = request.Model3DId,
                SliceJobId = request.SliceJobId,
                ArtifactId = request.ArtifactId,
                GcodeFileId = request.GcodeFileId,
                PrintJobId = request.PrintJobId,
                CalibrationOrchestrationId = request.CalibrationOrchestrationId,
                ErrorCode = NormalizeOptional(request.ErrorCode),
                ErrorJson = request.Error.HasValue ? Json(request.Error.Value) : null,
                RetryNumber = request.RetryNumber,
                OperationId = request.OperationId.Trim(),
                OccurredAtUtc = request.OccurredAtUtc?.ToUniversalTime() ?? UtcNow(),
                ActorSubject = actor.Subject,
            };
            CalibrationAttemptEventDto result = MapEvent(@event);
            _ = _dbContext.CalibrationAttemptEvents.Add(@event);
            CalibrationProject project = await _dbContext.CalibrationProjects.SingleAsync(
                candidate => candidate.Id == attempt.ProjectId,
                cancellationToken);
            AddChange(
                project,
                "attemptEvent",
                @event.Id,
                @event.Sequence,
                CalibrationChangeType.Created,
                request.OperationId,
                actor);
            AddIdempotency(
                actor,
                attempt.ProjectId,
                request.ClientId,
                request.OperationId,
                "attempt.event.append",
                requestHash,
                "attemptEvent",
                @event.Id,
                StatusCodes.Status201Created,
                result);

            try
            {
                _ = await SaveJournaledChangesAsync(cancellationToken);
                return CalibrationApiResult<CalibrationAttemptEventDto>.Success(
                    result,
                    StatusCodes.Status201Created);
            }
            catch (Exception exception) when (exception is DbUpdateException or DbException)
            {
                ClearTrackedState();
                replay = await FindReplayAsync<CalibrationAttemptEventDto>(
                    actor,
                    request.ClientId,
                    request.OperationId,
                    requestHash,
                    cancellationToken,
                    operationType: "attempt.event.append");
                if (replay is not null)
                {
                    return replay;
                }

                if (appendAttempt == MaximumAppendAttempts - 1)
                {
                    throw;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10 * (appendAttempt + 1)), cancellationToken);
            }
        }

        throw new InvalidOperationException("The calibration event append retry budget was exhausted.");
    }

    /// <inheritdoc />
    public async Task<CalibrationApiResult<CalibrationObservationDto>> AppendObservationAsync(
        Guid attemptId,
        CalibrationObservationCreateRequest request,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.OperationId) ||
            string.IsNullOrWhiteSpace(request.ObservationType) ||
            !IsJsonContainer(request.Measurements) || !IsJsonContainer(request.Result) ||
            !IsJsonContainer(request.Units) || request.Confidence is < 0 or > 1 ||
            request.Notes?.Length > 4_096)
        {
            return Validation<CalibrationObservationDto>("observation_invalid");
        }

        string? payloadSafetyCode = new[]
        {
            request.Measurements,
            request.Result,
            request.Units,
        }
            .Select(ValidateSafeJson)
            .FirstOrDefault(code => code is not null);
        if (payloadSafetyCode is not null)
        {
            return Validation<CalibrationObservationDto>(payloadSafetyCode);
        }

        CalibrationAttempt? attempt = await FindVisibleAttemptAsync(attemptId, actor, cancellationToken);
        if (attempt is null)
        {
            return NotFound<CalibrationObservationDto>();
        }

        // D8: reject physically implausible flow-ratio/temperature/PA values. Only enforced
        // when the attempt's method resolves to a known kind with a defined range and the
        // corresponding measurement key is present; this validates values submitted during a
        // run and is never a precondition to creating the attempt/starting calibration.
        if (ValidateMeasurementRange(attempt.Method, request.Measurements) is string measurementRangeCode)
        {
            return Validation<CalibrationObservationDto>(measurementRangeCode);
        }

        if (request.SelectionParentObservationId.HasValue &&
            !await _dbContext.CalibrationObservations.AnyAsync(
                observation => observation.Id == request.SelectionParentObservationId &&
                    observation.AttemptId == attemptId,
                cancellationToken))
        {
            return Validation<CalibrationObservationDto>("selection_parent_not_found");
        }

        string requestHash = ComputeDirectOperationHash("observation.append", "attempt", attemptId, request);
        CalibrationApiResult<CalibrationObservationDto>? replay =
            await FindReplayAsync<CalibrationObservationDto>(
                actor,
                request.ClientId,
                request.OperationId,
                requestHash,
                cancellationToken,
                operationType: "observation.append");
        if (replay is not null)
        {
            return replay;
        }

        for (int appendAttempt = 0; appendAttempt < MaximumAppendAttempts; appendAttempt++)
        {
            long sequence = await NextObservationSequenceAsync(attemptId, cancellationToken);
            CalibrationObservation observation = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = attempt.ProjectId,
                AttemptId = attempt.Id,
                Sequence = sequence,
                ObservationType = request.ObservationType.Trim(),
                MeasurementsJson = Json(request.Measurements),
                ResultJson = Json(request.Result),
                UnitsJson = Json(request.Units),
                Confidence = request.Confidence,
                RetestRecommended = request.RetestRecommended,
                Notes = NormalizeOptional(request.Notes),
                SelectionParentObservationId = request.SelectionParentObservationId,
                SelectionReason = NormalizeOptional(request.SelectionReason),
                OperationId = request.OperationId.Trim(),
                ObservedAtUtc = request.ObservedAtUtc?.ToUniversalTime() ?? UtcNow(),
                ActorSubject = actor.Subject,
            };
            CalibrationObservationDto result = MapObservation(observation);
            _ = _dbContext.CalibrationObservations.Add(observation);
            CalibrationProject project = await _dbContext.CalibrationProjects.SingleAsync(
                candidate => candidate.Id == attempt.ProjectId,
                cancellationToken);
            AddChange(
                project,
                "observation",
                observation.Id,
                observation.Sequence,
                CalibrationChangeType.Created,
                request.OperationId,
                actor);
            AddIdempotency(
                actor,
                attempt.ProjectId,
                request.ClientId,
                request.OperationId,
                "observation.append",
                requestHash,
                "observation",
                observation.Id,
                StatusCodes.Status201Created,
                result);

            try
            {
                _ = await SaveJournaledChangesAsync(cancellationToken);
                return CalibrationApiResult<CalibrationObservationDto>.Success(
                    result,
                    StatusCodes.Status201Created);
            }
            catch (Exception exception) when (exception is DbUpdateException or DbException)
            {
                ClearTrackedState();
                replay = await FindReplayAsync<CalibrationObservationDto>(
                    actor,
                    request.ClientId,
                    request.OperationId,
                    requestHash,
                    cancellationToken,
                    operationType: "observation.append");
                if (replay is not null)
                {
                    return replay;
                }

                if (appendAttempt == MaximumAppendAttempts - 1)
                {
                    throw;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10 * (appendAttempt + 1)), cancellationToken);
            }
        }

        throw new InvalidOperationException("The calibration observation append retry budget was exhausted.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CalibrationPhotoDto>> GetPhotosAsync(
        Guid attemptId,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        CalibrationAttempt? attempt = await FindVisibleAttemptAsync(attemptId, actor, cancellationToken);
        if (attempt is null)
        {
            return [];
        }

        return (await _dbContext.CalibrationPhotos
            .AsNoTracking()
            .Where(photo => photo.AttemptId == attemptId && photo.DeletedAtUtc == null)
            .OrderBy(photo => photo.SortOrder)
            .ThenBy(photo => photo.CreatedAtUtc)
            .ToListAsync(cancellationToken))
            .Select(MapPhoto)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<CalibrationApiResult<CalibrationPhotoDto>> UploadPhotoAsync(
        Guid attemptId,
        string clientUploadId,
        string originalFileName,
        string declaredContentType,
        DateTime? capturedAtUtc,
        string? caption,
        int sortOrder,
        Stream content,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(clientUploadId) || clientUploadId.Length > 128 ||
            string.IsNullOrWhiteSpace(originalFileName) || originalFileName.Length > 255 ||
            caption?.Length > 1_024)
        {
            return Validation<CalibrationPhotoDto>("photo_metadata_invalid");
        }

        CalibrationAttempt? attempt = await FindVisibleAttemptAsync(attemptId, actor, cancellationToken);
        if (attempt is null)
        {
            return NotFound<CalibrationPhotoDto>();
        }

        string normalizedClientUploadId = clientUploadId.Trim();
        string normalizedOriginalFileName = Path.GetFileName(originalFileName);
        string? normalizedCaption = NormalizeOptional(caption);
        Guid photoId = Guid.NewGuid();
        CalibrationBlobMetadata blob;
        try
        {
            blob = await _blobStore.PutAsync(
                new CalibrationBlobWriteRequest(
                    actor.UserId,
                    attempt.ProjectId,
                    attemptId,
                    photoId,
                    normalizedOriginalFileName,
                    declaredContentType),
                content,
                cancellationToken);
        }
        catch (CalibrationBlobValidationException exception)
        {
            return Validation<CalibrationPhotoDto>(exception.Code);
        }
        catch (IOException)
        {
            return CalibrationApiResult<CalibrationPhotoDto>.Failure(
                StatusCodes.Status503ServiceUnavailable,
                "storage_or_dependency_unavailable");
        }

        string requestHash = ComputeCanonicalHash(new
        {
            ClientId = normalizedClientUploadId,
            AttemptId = attemptId,
            OriginalFileName = normalizedOriginalFileName,
            DeclaredContentType = declaredContentType.Trim(),
            BlobContentType = blob.ContentType,
            SourceSha256 = blob.SourceSha256 ?? blob.Sha256,
            CapturedAtUtc = capturedAtUtc?.ToUniversalTime(),
            Caption = normalizedCaption,
            SortOrder = sortOrder,
        });
        CalibrationApiResult<CalibrationPhotoDto>? replay = await FindReplayAsync<CalibrationPhotoDto>(
            actor,
            normalizedClientUploadId,
            normalizedClientUploadId,
            requestHash,
            cancellationToken,
            "photo.upload",
            "photo");
        if (replay is not null)
        {
            await DeleteUploadedBlobAsync(blob.StorageKey, photoId, cancellationToken);
            return replay;
        }

        CalibrationPhoto photo = new()
        {
            Id = photoId,
            ProjectId = attempt.ProjectId,
            AttemptId = attemptId,
            ClientUploadId = normalizedClientUploadId,
            OpaqueStorageKey = blob.StorageKey,
            OriginalFileName = normalizedOriginalFileName,
            ContentType = blob.ContentType,
            SizeBytes = blob.SizeBytes,
            Sha256 = blob.Sha256,
            Width = blob.Width,
            Height = blob.Height,
            CapturedAtUtc = capturedAtUtc?.ToUniversalTime(),
            Caption = normalizedCaption,
            SortOrder = sortOrder,
            Revision = 1,
            CreatedAtUtc = UtcNow(),
            CreatedBySubject = actor.Subject,
        };
        CalibrationPhotoDto result = MapPhoto(photo);
        _ = _dbContext.CalibrationPhotos.Add(photo);
        CalibrationProject project = await _dbContext.CalibrationProjects.SingleAsync(
            candidate => candidate.Id == attempt.ProjectId,
            cancellationToken);
        AddChange(
            project,
            "photo",
            photo.Id,
            photo.Revision,
            CalibrationChangeType.Created,
            normalizedClientUploadId,
            actor);
        AddIdempotency(
            actor,
            attempt.ProjectId,
            normalizedClientUploadId,
            normalizedClientUploadId,
            "photo.upload",
            requestHash,
            "photo",
            photo.Id,
            StatusCodes.Status201Created,
            result);

        try
        {
            _ = await SaveJournaledChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await DeleteUploadedBlobAsync(blob.StorageKey, photoId, CancellationToken.None);
            return await ResolveCreateRaceAsync<CalibrationPhotoDto>(
                actor,
                normalizedClientUploadId,
                normalizedClientUploadId,
                requestHash,
                cancellationToken,
                "photo.upload",
                "photo");
        }
        catch
        {
            await DeleteUploadedBlobAsync(blob.StorageKey, photoId, CancellationToken.None);
            throw;
        }

        return CalibrationApiResult<CalibrationPhotoDto>.Success(result, StatusCodes.Status201Created);
    }

    /// <inheritdoc />
    public async Task<CalibrationApiResult<CalibrationPhotoDto>> GetPhotoAsync(
        Guid photoId,
        CalibrationActor actor,
        bool includeDeleted,
        CancellationToken cancellationToken)
    {
        CalibrationPhoto? photo = await FindVisiblePhotoAsync(photoId, actor, includeDeleted, cancellationToken);
        return photo is null
            ? NotFound<CalibrationPhotoDto>()
            : CalibrationApiResult<CalibrationPhotoDto>.Success(MapPhoto(photo));
    }

    /// <inheritdoc />
    public async Task<CalibrationApiResult<CalibrationPhotoDto>> UpdatePhotoAsync(
        Guid photoId,
        CalibrationPhotoUpdateRequest request,
        string? ifMatch,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Caption?.Length > 1_024)
        {
            return Validation<CalibrationPhotoDto>("photo_caption_invalid");
        }

        CalibrationPhoto? photo = await FindVisiblePhotoAsync(photoId, actor, false, cancellationToken);
        if (photo is null)
        {
            return NotFound<CalibrationPhotoDto>();
        }

        CalibrationApiResult<CalibrationPhotoDto>? precondition =
            CheckPrecondition(photo.Revision, photo.Id, "photo", request.BaseRevision, ifMatch, MapPhoto(photo));
        if (precondition is not null)
        {
            return precondition;
        }

        photo.Caption = NormalizeOptional(request.Caption);
        if (request.SortOrder.HasValue)
        {
            photo.SortOrder = request.SortOrder.Value;
        }

        photo.Revision++;
        CalibrationProject project = await _dbContext.CalibrationProjects.SingleAsync(
            candidate => candidate.Id == photo.ProjectId,
            cancellationToken);
        CalibrationPhotoDto result = MapPhoto(photo);
        AddChange(project, "photo", photo.Id, photo.Revision, CalibrationChangeType.Updated, MutationId(), actor);
        try
        {
            _ = await SaveJournaledChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await PhotoRevisionConflictAsync(
                photoId,
                request.BaseRevision,
                actor,
                cancellationToken);
        }

        return CalibrationApiResult<CalibrationPhotoDto>.Success(result);
    }

    /// <inheritdoc />
    public async Task<CalibrationApiResult<CalibrationPhotoDto>> DeletePhotoAsync(
        Guid photoId,
        long? baseRevision,
        string? ifMatch,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        CalibrationPhoto? photo = await FindVisiblePhotoAsync(photoId, actor, false, cancellationToken);
        if (photo is null)
        {
            return NotFound<CalibrationPhotoDto>();
        }

        CalibrationApiResult<CalibrationPhotoDto>? precondition =
            CheckPrecondition(photo.Revision, photo.Id, "photo", baseRevision, ifMatch, MapPhoto(photo));
        if (precondition is not null)
        {
            return precondition;
        }

        DateTime nowUtc = UtcNow();
        photo.DeletedAtUtc = nowUtc;
        photo.DeletedBySubject = actor.Subject;
        photo.DeleteRequestedAtUtc = nowUtc;
        photo.Revision++;
        CalibrationProject project = await _dbContext.CalibrationProjects.SingleAsync(
            candidate => candidate.Id == photo.ProjectId,
            cancellationToken);
        CalibrationPhotoDto result = MapPhoto(photo);
        AddChange(
            project,
            "photo",
            photo.Id,
            photo.Revision,
            CalibrationChangeType.Deleted,
            MutationId(),
            actor,
            new { photo.Id, photo.ProjectId, photo.AttemptId, photo.DeletedAtUtc });
        try
        {
            _ = await SaveJournaledChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await PhotoRevisionConflictAsync(photoId, baseRevision, actor, cancellationToken);
        }

        try
        {
            await _blobStore.DeleteAsync(photo.OpaqueStorageKey, cancellationToken);
            photo.PurgedAtUtc = UtcNow();
            try
            {
                _ = await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return await PhotoRevisionConflictAsync(photoId, baseRevision, actor, cancellationToken);
            }

            return CalibrationApiResult<CalibrationPhotoDto>.Success(MapPhoto(photo));
        }
        catch (IOException)
        {
            _logger.LogWarning(
                "Private calibration photo deletion is pending reconciliation. PhotoId={PhotoId}",
                photoId);
            return CalibrationApiResult<CalibrationPhotoDto>.Success(
                result,
                StatusCodes.Status202Accepted);
        }
    }

    /// <inheritdoc />
    public async Task<Stream> OpenPhotoAsync(
        Guid photoId,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        CalibrationPhoto? photo = await FindVisiblePhotoAsync(photoId, actor, false, cancellationToken);
        if (photo is null || photo.PurgedAtUtc.HasValue)
        {
            throw new FileNotFoundException("The requested calibration photo does not exist.");
        }

        return await _blobStore.OpenReadAsync(photo.OpaqueStorageKey, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CalibrationApiResult<CalibrationChangesResponse>> GetChangesAsync(
        string? after,
        int limit,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > MaximumChangePageSize)
        {
            return Validation<CalibrationChangesResponse>("change_limit_invalid");
        }

        string scope = GetScope(actor);
        long afterSequence = 0;
        if (!string.IsNullOrWhiteSpace(after))
        {
            if (!Guid.TryParse(after, out Guid cursorId))
            {
                return Validation<CalibrationChangesResponse>("change_cursor_invalid");
            }

            CalibrationSyncCursor? cursor = await _dbContext.CalibrationSyncCursors
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == cursorId && candidate.Scope == scope,
                    cancellationToken);
            if (cursor is null)
            {
                return Validation<CalibrationChangesResponse>("change_cursor_invalid");
            }

            afterSequence = cursor.Sequence;
        }

        IQueryable<CalibrationChange> query = _dbContext.CalibrationChanges
            .AsNoTracking()
            .Where(change => change.Sequence > afterSequence);
        if (!actor.IsFarmAdmin)
        {
            query = query.Where(change => change.OwnerUserId == actor.UserId);
        }

        CalibrationChange[] page = await query
            .OrderBy(change => change.Sequence)
            .Take(limit + 1)
            .ToArrayAsync(cancellationToken);
        bool hasMore = page.Length > limit;
        CalibrationChange[] visible = hasMore ? page[..limit] : page;
        long nextSequence = visible.Length == 0 ? afterSequence : visible[^1].Sequence;
        CalibrationSyncCursor nextCursor = new()
        {
            Id = Guid.NewGuid(),
            Scope = scope,
            Sequence = nextSequence,
            CreatedAtUtc = UtcNow(),
        };
        _ = _dbContext.CalibrationSyncCursors.Add(nextCursor);
        _ = await _dbContext.SaveChangesAsync(cancellationToken);

        return CalibrationApiResult<CalibrationChangesResponse>.Success(
            new CalibrationChangesResponse(
                visible.Select(MapChange).ToArray(),
                nextCursor.Id.ToString("N"),
                hasMore,
                UtcNow()));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CalibrationSyncMutationResultDto>> ApplyChangesAsync(
        CalibrationSyncApplyRequest request,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<CalibrationSyncMutationResultDto> results = [];
        HashSet<string> succeeded = new(StringComparer.Ordinal);
        foreach (CalibrationSyncMutationRequest mutation in request.Mutations)
        {
            string operationId = mutation.OperationId?.Trim() ?? string.Empty;
            string operationType = mutation.OperationType?.Trim() ?? string.Empty;
            string clientId = mutation.ClientId?.Trim() ?? string.Empty;
            if (operationId.Length == 0 || operationType.Length == 0 || clientId.Length == 0)
            {
                results.Add(new(
                    operationId,
                    "invalid",
                    StatusCodes.Status422UnprocessableEntity,
                    "sync_mutation_invalid",
                    null,
                    null));
                continue;
            }

            string[] dependencies = mutation.Dependencies
                .Select(dependency => dependency.Trim())
                .ToArray();
            if (dependencies.Any(dependency => !succeeded.Contains(dependency)))
            {
                results.Add(new(
                    operationId,
                    "conflict",
                    StatusCodes.Status409Conflict,
                    "sync_dependency_unsatisfied",
                    null,
                    null));
                continue;
            }

            CalibrationSyncMutationRequest normalizedMutation = new()
            {
                ClientId = clientId,
                OperationId = operationId,
                OperationType = operationType,
                ProjectId = mutation.ProjectId,
                BaseRevision = mutation.BaseRevision,
                Payload = mutation.Payload,
                Dependencies = dependencies,
            };
            CalibrationSyncMutationResultDto result = await ApplyMutationAsync(
                normalizedMutation,
                actor,
                cancellationToken);
            results.Add(result);
            if (result.Status is "applied" or "replayed")
            {
                _ = succeeded.Add(operationId);
            }
            else
            {
                ClearTrackedState();
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<CalibrationApiResult<LegacyCalibrationImportResultDto>> ImportLegacyV4Async(
        LegacyCalibrationImportRequest request,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ClientId) ||
            string.IsNullOrWhiteSpace(request.OperationId) ||
            request.Projects.Count == 0 ||
            request.Projects.Count > 100)
        {
            return Validation<LegacyCalibrationImportResultDto>("legacy_import_invalid");
        }

        string sourceHash = ComputeCanonicalHash(request.Projects);
        CalibrationApiResult<LegacyCalibrationImportResultDto>? replay =
            await FindReplayAsync<LegacyCalibrationImportResultDto>(
                actor,
                request.ClientId,
                request.OperationId,
                sourceHash,
                cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        List<string> rejected = request.Projects
            .Select((project, index) => (Code: ValidateProjectCreate(project), Index: index))
            .Where(result => result.Code is not null)
            .Select(result => $"projects[{result.Index}]:{result.Code}")
            .ToList();
        if (rejected.Count > 0)
        {
            return CalibrationApiResult<LegacyCalibrationImportResultDto>.Success(
                new(
                    true,
                    sourceHash,
                    [],
                    [],
                    rejected,
                    []));
        }

        List<string> mappings = request.Projects
            .Select((project, index) => $"projects[{index}]=>calibration-project")
            .ToList();
        if (request.DryRun)
        {
            return CalibrationApiResult<LegacyCalibrationImportResultDto>.Success(
                new(true, sourceHash, mappings, [], [], []));
        }

        await using IDbContextTransaction transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        List<Guid> projectIds = [];
        foreach (CalibrationProjectCreateRequest projectRequest in request.Projects)
        {
            CalibrationApiResult<CalibrationProjectDto> created = await CreateProjectAsync(
                projectRequest,
                actor,
                cancellationToken);
            if (!created.IsSuccess || created.Value is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                ClearTrackedState();
                return CalibrationApiResult<LegacyCalibrationImportResultDto>.Failure(
                    created.StatusCode,
                    created.Code ?? "legacy_import_failed",
                    created.Conflict);
            }

            projectIds.Add(created.Value.Id);
        }

        LegacyCalibrationImportResultDto result = new(
            false,
            sourceHash,
            mappings,
            [],
            [],
            projectIds);
        AddIdempotency(
            actor,
            null,
            request.ClientId,
            request.OperationId,
            "legacy-v4.import",
            sourceHash,
            "legacyImport",
            null,
            StatusCodes.Status201Created,
            result);
        _ = await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CalibrationApiResult<LegacyCalibrationImportResultDto>.Success(
            result,
            StatusCodes.Status201Created);
    }

    /// <inheritdoc />
    public async Task<int> ReconcilePendingPhotoDeletesAsync(CancellationToken cancellationToken)
    {
        CalibrationPhoto[] pending = await _dbContext.CalibrationPhotos
            .Where(photo => photo.DeleteRequestedAtUtc != null && photo.PurgedAtUtc == null)
            .OrderBy(photo => photo.DeleteRequestedAtUtc)
            .Take(100)
            .ToArrayAsync(cancellationToken);
        CalibrationBlobCleanup[] pendingBlobCleanups = await _dbContext.CalibrationBlobCleanups
            .OrderBy(cleanup => cleanup.CreatedAtUtc)
            .Take(100)
            .ToArrayAsync(cancellationToken);
        int reconciled = 0;
        foreach (CalibrationPhoto photo in pending)
        {
            try
            {
                await _blobStore.DeleteAsync(photo.OpaqueStorageKey, cancellationToken);
                photo.PurgedAtUtc = UtcNow();
                _ = await _dbContext.SaveChangesAsync(cancellationToken);
                reconciled++;
            }
            catch (DbUpdateConcurrencyException exception)
            {
                _logger.LogInformation(
                    exception,
                    "Calibration photo delete reconciliation was superseded. PhotoId={PhotoId}",
                    photo.Id);
                ClearTrackedState();
                return reconciled;
            }
            catch (IOException exception)
            {
                _logger.LogWarning(
                    exception,
                    "Calibration photo remains pending blob deletion. PhotoId={PhotoId}",
                    photo.Id);
            }
        }

        foreach (CalibrationBlobCleanup cleanup in pendingBlobCleanups)
        {
            try
            {
                await _blobStore.DeleteAsync(cleanup.OpaqueStorageKey, cancellationToken);
                _ = _dbContext.CalibrationBlobCleanups.Remove(cleanup);
                _ = await _dbContext.SaveChangesAsync(cancellationToken);
                reconciled++;
            }
            catch (IOException exception)
            {
                _logger.LogWarning(
                    exception,
                    "Orphaned calibration blob remains pending deletion. CleanupId={CleanupId}",
                    cleanup.Id);
            }
        }

        return reconciled;
    }

    private async Task<CalibrationSyncMutationResultDto> ApplyMutationAsync(
        CalibrationSyncMutationRequest mutation,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        try
        {
            return mutation.OperationType switch
            {
                "project.update" when mutation.ProjectId.HasValue =>
                    await ApplyProjectUpdateMutationAsync(mutation, actor, cancellationToken),
                "project.delete" when mutation.ProjectId.HasValue =>
                    await ApplyProjectDeleteMutationAsync(mutation, actor, cancellationToken),
                _ => new(
                    mutation.OperationId,
                    "invalid",
                    StatusCodes.Status422UnprocessableEntity,
                    "sync_operation_unsupported",
                    null,
                    null),
            };
        }
        catch (JsonException)
        {
            return new(
                mutation.OperationId,
                "invalid",
                StatusCodes.Status422UnprocessableEntity,
                "sync_payload_invalid",
                null,
                null);
        }
    }

    private async Task<CalibrationSyncMutationResultDto> ApplyProjectUpdateMutationAsync(
        CalibrationSyncMutationRequest mutation,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        CalibrationProjectUpdateRequest? payload = mutation.Payload.Deserialize<CalibrationProjectUpdateRequest>(JsonOptions);
        if (payload is null)
        {
            return new(mutation.OperationId, "invalid", StatusCodes.Status422UnprocessableEntity, "sync_payload_invalid", null, null);
        }

        CalibrationProjectUpdateRequest request = new()
        {
            BaseRevision = mutation.BaseRevision ?? payload.BaseRevision,
            Name = payload.Name,
            LifecycleStatus = payload.LifecycleStatus,
            CurrentStep = payload.CurrentStep,
            OrderedSteps = payload.OrderedSteps,
            CurrentSelections = payload.CurrentSelections,
            CompletedAtUtc = payload.CompletedAtUtc,
        };
        Guid projectId = mutation.ProjectId.GetValueOrDefault();
        MutationIdentity identity = CreateMutationIdentity(
            mutation,
            "project.update",
            "project",
            projectId,
            request.BaseRevision,
            request);
        string ifMatch = request.BaseRevision.HasValue
            ? ETag("project", projectId, request.BaseRevision.Value)
            : string.Empty;
        CalibrationApiResult<CalibrationProjectDto> result = await UpdateProjectInternalAsync(
            projectId,
            request,
            ifMatch,
            actor,
            identity,
            cancellationToken);
        return ToSyncResult(mutation.OperationId, result);
    }

    private async Task<CalibrationSyncMutationResultDto> ApplyProjectDeleteMutationAsync(
        CalibrationSyncMutationRequest mutation,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        Guid projectId = mutation.ProjectId.GetValueOrDefault();
        MutationIdentity identity = CreateMutationIdentity(
            mutation,
            "project.delete",
            "project",
            projectId,
            mutation.BaseRevision,
            new { });
        string ifMatch = mutation.BaseRevision.HasValue
            ? ETag("project", projectId, mutation.BaseRevision.Value)
            : string.Empty;
        CalibrationApiResult<CalibrationProjectDto> result = await DeleteProjectInternalAsync(
            projectId,
            mutation.BaseRevision,
            ifMatch,
            actor,
            identity,
            cancellationToken);
        return ToSyncResult(mutation.OperationId, result);
    }

    private static MutationIdentity CreateMutationIdentity(
        CalibrationSyncMutationRequest mutation,
        string operationType,
        string resourceType,
        Guid resourceId,
        long? effectiveBaseRevision,
        object canonicalPayload) =>
        new(
            mutation.ClientId,
            mutation.OperationId,
            operationType,
            ComputeCanonicalHash(new
            {
                OperationType = operationType,
                ResourceType = resourceType,
                ResourceId = resourceId,
                BaseRevision = effectiveBaseRevision,
                Payload = canonicalPayload,
            }),
            resourceType,
            resourceId,
            "semantic_conflict");

    private static CalibrationSyncMutationResultDto ToSyncResult<T>(
        string operationId,
        CalibrationApiResult<T> result)
    {
        JsonElement? value = result.Value is null
            ? null
            : JsonSerializer.SerializeToElement(result.Value, JsonOptions);
        string mutationStatus = result.IsSuccess
            ? result.Replayed ? "replayed" : "applied"
            : result.StatusCode is StatusCodes.Status409Conflict or StatusCodes.Status412PreconditionFailed
                ? "conflict"
                : "invalid";
        return new(
            operationId,
            mutationStatus,
            result.StatusCode,
            result.Code,
            value,
            result.Conflict);
    }

    private IQueryable<CalibrationProject> VisibleProjects(CalibrationActor actor, bool includeDeleted)
    {
        IQueryable<CalibrationProject> query = _dbContext.CalibrationProjects;
        if (!actor.IsFarmAdmin)
        {
            query = query.Where(project => project.OwnerUserId == actor.UserId);
        }

        return includeDeleted ? query : query.Where(project => project.DeletedAtUtc == null);
    }

    private Task<CalibrationProject?> FindVisibleProjectAsync(
        Guid projectId,
        CalibrationActor actor,
        bool includeDeleted,
        CancellationToken cancellationToken) =>
        VisibleProjects(actor, includeDeleted).SingleOrDefaultAsync(
            project => project.Id == projectId,
            cancellationToken);

    private async Task<CalibrationAttempt?> FindVisibleAttemptAsync(
        Guid attemptId,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        IQueryable<CalibrationAttempt> query = _dbContext.CalibrationAttempts
            .Join(
                VisibleProjects(actor, false),
                attempt => attempt.ProjectId,
                project => project.Id,
                (attempt, _) => attempt);
        return await query.SingleOrDefaultAsync(attempt => attempt.Id == attemptId, cancellationToken);
    }

    private async Task<CalibrationPhoto?> FindVisiblePhotoAsync(
        Guid photoId,
        CalibrationActor actor,
        bool includeDeleted,
        CancellationToken cancellationToken)
    {
        IQueryable<CalibrationPhoto> query = _dbContext.CalibrationPhotos
            .Join(
                VisibleProjects(actor, true),
                photo => photo.ProjectId,
                project => project.Id,
                (photo, project) => new { photo, project })
            .Where(item => item.project.DeletedAtUtc == null)
            .Select(item => item.photo);
        if (!includeDeleted)
        {
            query = query.Where(photo => photo.DeletedAtUtc == null);
        }

        return await query.SingleOrDefaultAsync(photo => photo.Id == photoId, cancellationToken);
    }

    private async Task<Dictionary<Guid, string>> GetAttemptStatusesAsync(
        Guid[] attemptIds,
        CancellationToken cancellationToken)
    {
        if (attemptIds.Length == 0)
        {
            return [];
        }

        return await _dbContext.CalibrationAttemptEvents
            .AsNoTracking()
            .Where(@event => attemptIds.Contains(@event.AttemptId))
            .GroupBy(@event => @event.AttemptId)
            .Select(group => new
            {
                AttemptId = group.Key,
                Status = group.OrderByDescending(@event => @event.Sequence)
                    .Select(@event => @event.DerivedStatus)
                    .First(),
            })
            .ToDictionaryAsync(item => item.AttemptId, item => item.Status, cancellationToken);
    }

    private async Task<string> GetAttemptStatusAsync(Guid attemptId, CancellationToken cancellationToken) =>
        await _dbContext.CalibrationAttemptEvents
            .AsNoTracking()
            .Where(@event => @event.AttemptId == attemptId)
            .OrderByDescending(@event => @event.Sequence)
            .Select(@event => @event.DerivedStatus)
            .FirstOrDefaultAsync(cancellationToken) ?? "planned";

    private async Task<long> NextEventSequenceAsync(Guid attemptId, CancellationToken cancellationToken) =>
        (await _dbContext.CalibrationAttemptEvents
            .Where(@event => @event.AttemptId == attemptId)
            .Select(@event => @event.Sequence)
            .ToListAsync(cancellationToken))
            .DefaultIfEmpty()
            .Max() + 1;

    private async Task<long> NextObservationSequenceAsync(Guid attemptId, CancellationToken cancellationToken) =>
        (await _dbContext.CalibrationObservations
            .Where(observation => observation.AttemptId == attemptId)
            .Select(observation => observation.Sequence)
            .ToListAsync(cancellationToken))
            .DefaultIfEmpty()
            .Max() + 1;

    private async Task<CalibrationApiResult<T>?> FindReplayAsync<T>(
        CalibrationActor actor,
        string clientId,
        string operationId,
        string canonicalRequestSha256,
        CancellationToken cancellationToken,
        string? operationType = null,
        string? resourceType = null,
        Guid? resourceId = null,
        string mismatchCode = "idempotency_payload_mismatch")
    {
        string normalizedClientId = clientId.Trim();
        string normalizedOperationId = operationId.Trim();
        CalibrationIdempotencyRecord? record = await _dbContext.CalibrationIdempotencyRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Scope == GetScope(actor) &&
                    candidate.ClientId == normalizedClientId &&
                    candidate.OperationId == normalizedOperationId,
                cancellationToken);
        if (record is null)
        {
            return null;
        }

        if ((operationType is not null &&
                !string.Equals(record.OperationType, operationType, StringComparison.Ordinal)) ||
            (resourceType is not null &&
                !string.Equals(record.ResourceType, resourceType, StringComparison.Ordinal)) ||
            (resourceId.HasValue && record.ResourceId != resourceId) ||
            !string.Equals(
                record.CanonicalRequestSha256,
                canonicalRequestSha256,
                StringComparison.Ordinal))
        {
            return CalibrationApiResult<T>.Failure(
                StatusCodes.Status409Conflict,
                mismatchCode);
        }

        if (record.State != CalibrationIdempotencyState.Completed ||
            string.IsNullOrWhiteSpace(record.StoredResultJson))
        {
            return CalibrationApiResult<T>.Failure(
                StatusCodes.Status503ServiceUnavailable,
                "idempotency_operation_incomplete");
        }

        T? value = JsonSerializer.Deserialize<T>(record.StoredResultJson, JsonOptions);
        return value is null
            ? CalibrationApiResult<T>.Failure(
                StatusCodes.Status503ServiceUnavailable,
                "idempotency_result_unavailable")
            : CalibrationApiResult<T>.Success(value, record.StoredStatusCode, replayed: true);
    }

    private async Task<CalibrationApiResult<T>> ResolveCreateRaceAsync<T>(
        CalibrationActor actor,
        string clientId,
        string operationId,
        string canonicalRequestSha256,
        CancellationToken cancellationToken,
        string? operationType = null,
        string? resourceType = null,
        Guid? resourceId = null,
        string mismatchCode = "idempotency_payload_mismatch")
    {
        ClearTrackedState();
        CalibrationApiResult<T>? replay = await FindReplayAsync<T>(
            actor,
            clientId,
            operationId,
            canonicalRequestSha256,
            cancellationToken,
            operationType,
            resourceType,
            resourceId,
            mismatchCode);
        return replay ?? CalibrationApiResult<T>.Failure(
            StatusCodes.Status409Conflict,
            "semantic_conflict");
    }

    private async Task PersistBlobCleanupAsync(
        string opaqueStorageKey,
        CancellationToken cancellationToken)
    {
        ClearTrackedState();
        if (await _dbContext.CalibrationBlobCleanups.AnyAsync(
                cleanup => cleanup.OpaqueStorageKey == opaqueStorageKey,
                cancellationToken))
        {
            return;
        }

        _ = _dbContext.CalibrationBlobCleanups.Add(new CalibrationBlobCleanup
        {
            Id = Guid.NewGuid(),
            OpaqueStorageKey = opaqueStorageKey,
            CreatedAtUtc = UtcNow(),
        });
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            ClearTrackedState();
            bool alreadyPersisted = await _dbContext.CalibrationBlobCleanups.AnyAsync(
                cleanup => cleanup.OpaqueStorageKey == opaqueStorageKey,
                cancellationToken);
            if (!alreadyPersisted)
            {
                throw;
            }
        }
    }

    private async Task DeleteUploadedBlobAsync(
        string opaqueStorageKey,
        Guid photoId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _blobStore.DeleteAsync(opaqueStorageKey, cancellationToken);
        }
        catch (IOException exception)
        {
            await PersistBlobCleanupAsync(opaqueStorageKey, CancellationToken.None);
            _logger.LogWarning(
                exception,
                "Calibration photo metadata write did not commit and private blob cleanup must be reconciled. PhotoId={PhotoId}",
                photoId);
        }
    }

    private async Task<CalibrationApiResult<CalibrationDraftDto>> ResolveDraftCreateRaceAsync(
        Guid projectId,
        string stepId,
        string deviceLineageId,
        CalibrationDraftUpsertRequest request,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        ClearTrackedState();
        CalibrationDraft? current = await _dbContext.CalibrationDrafts
            .Join(
                VisibleProjects(actor, false),
                draft => draft.ProjectId,
                project => project.Id,
                (draft, _) => draft)
            .SingleOrDefaultAsync(
                draft => draft.ProjectId == projectId &&
                    draft.StepId == stepId &&
                    draft.DeviceLineageId == deviceLineageId &&
                    draft.DeletedAtUtc == null,
                cancellationToken);
        if (current is null)
        {
            throw new InvalidOperationException(
                "The concurrent calibration draft insert did not produce an active draft.");
        }

        CalibrationDraftDto representation = MapDraft(current);
        if (string.Equals(current.Method, request.Method.Trim(), StringComparison.Ordinal) &&
            string.Equals(current.ValuesJson, Json(request.Values), StringComparison.Ordinal) &&
            string.Equals(current.PrerequisitesJson, Json(request.Prerequisites), StringComparison.Ordinal))
        {
            return CalibrationApiResult<CalibrationDraftDto>.Success(representation, replayed: true);
        }

        return CalibrationApiResult<CalibrationDraftDto>.Failure(
            StatusCodes.Status409Conflict,
            "draft_create_conflict",
            new(
                current.Revision,
                null,
                representation,
                ["draft"],
                ["refresh", "fork", "discard-local-change"]));
    }

    private async Task<CalibrationApiResult<CalibrationProjectDto>> ProjectRevisionConflictAsync(
        Guid projectId,
        long? submittedBaseRevision,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        ClearTrackedState();
        CalibrationProject? current = await FindVisibleProjectAsync(
            projectId,
            actor,
            true,
            cancellationToken);
        return current is null
            ? NotFound<CalibrationProjectDto>()
            : RevisionConflict(
                current.Revision,
                submittedBaseRevision,
                MapProject(current));
    }

    private async Task<CalibrationApiResult<CalibrationDraftDto>> DraftRevisionConflictAsync(
        Guid draftId,
        long? submittedBaseRevision,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        ClearTrackedState();
        CalibrationDraft? current = await _dbContext.CalibrationDrafts
            .Join(
                VisibleProjects(actor, true),
                draft => draft.ProjectId,
                project => project.Id,
                (draft, _) => draft)
            .SingleOrDefaultAsync(draft => draft.Id == draftId, cancellationToken);
        return current is null
            ? NotFound<CalibrationDraftDto>()
            : RevisionConflict(
                current.Revision,
                submittedBaseRevision,
                MapDraft(current));
    }

    private async Task<CalibrationApiResult<CalibrationPhotoDto>> PhotoRevisionConflictAsync(
        Guid photoId,
        long? submittedBaseRevision,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        ClearTrackedState();
        CalibrationPhoto? current = await _dbContext.CalibrationPhotos
            .Join(
                VisibleProjects(actor, true),
                photo => photo.ProjectId,
                project => project.Id,
                (photo, _) => photo)
            .SingleOrDefaultAsync(photo => photo.Id == photoId, cancellationToken);
        return current is null
            ? NotFound<CalibrationPhotoDto>()
            : RevisionConflict(
                current.Revision,
                submittedBaseRevision,
                MapPhoto(current));
    }

    private async Task<int> SaveJournaledChangesAsync(CancellationToken cancellationToken)
    {
        CalibrationChange[] pendingChanges = _dbContext.ChangeTracker
            .Entries<CalibrationChange>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity)
            .OrderBy(change => _pendingChangeOrders.TryGetValue(change, out long order) ? order : long.MaxValue)
            .ToArray();
        if (pendingChanges.Length == 0)
        {
            return await _dbContext.SaveChangesAsync(cancellationToken);
        }

        if (string.Equals(
                _dbContext.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.InMemory",
                StringComparison.Ordinal))
        {
            CalibrationChangeFeedState? inMemoryState = await _dbContext.CalibrationChangeFeedStates
                .SingleOrDefaultAsync(state => state.Id == 1, cancellationToken);
            if (inMemoryState is null)
            {
                inMemoryState = new CalibrationChangeFeedState { Id = 1 };
                _ = _dbContext.CalibrationChangeFeedStates.Add(inMemoryState);
            }

            long firstSequence = inMemoryState.LastSequence + 1;
            inMemoryState.LastSequence += pendingChanges.Length;
            for (int index = 0; index < pendingChanges.Length; index++)
            {
                pendingChanges[index].Sequence = firstSequence + index;
            }

            int saved = await _dbContext.SaveChangesAsync(cancellationToken);
            foreach (CalibrationChange change in pendingChanges)
            {
                _ = _pendingChangeOrders.Remove(change);
            }

            return saved;
        }

        bool ownsTransaction = _dbContext.Database.CurrentTransaction is null;
        IDbContextTransaction? transaction = ownsTransaction
            ? await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        try
        {
            int updatedStates = await _dbContext.CalibrationChangeFeedStates
                .Where(state => state.Id == 1)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        state => state.LastSequence,
                        state => state.LastSequence + pendingChanges.Length),
                    cancellationToken);
            if (updatedStates != 1)
            {
                throw new InvalidOperationException(
                    "The calibration journal allocator is not initialized.");
            }

            long lastSequence = await _dbContext.CalibrationChangeFeedStates
                .AsNoTracking()
                .Where(state => state.Id == 1)
                .Select(state => state.LastSequence)
                .SingleAsync(cancellationToken);
            long firstSequence = lastSequence - pendingChanges.Length + 1;
            for (int index = 0; index < pendingChanges.Length; index++)
            {
                pendingChanges[index].Sequence = firstSequence + index;
            }

            int saved = await _dbContext.SaveChangesAsync(cancellationToken);
            foreach (CalibrationChange change in pendingChanges)
            {
                _ = _pendingChangeOrders.Remove(change);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return saved;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private void AddIdempotency<T>(
        CalibrationActor actor,
        Guid? projectId,
        string clientId,
        string operationId,
        string operationType,
        string canonicalRequestSha256,
        string resourceType,
        Guid? resourceId,
        int statusCode,
        T result)
    {
        _ = _dbContext.CalibrationIdempotencyRecords.Add(new CalibrationIdempotencyRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = actor.UserId,
            ProjectId = projectId,
            Scope = GetScope(actor),
            ClientId = clientId.Trim(),
            OperationId = operationId.Trim(),
            OperationType = operationType,
            CanonicalRequestSha256 = canonicalRequestSha256,
            ResourceType = resourceType,
            ResourceId = resourceId,
            StoredStatusCode = statusCode,
            StoredResultJson = JsonSerializer.Serialize(result, JsonOptions),
            State = CalibrationIdempotencyState.Completed,
            CreatedAtUtc = UtcNow(),
            CompletedAtUtc = UtcNow(),
        });
    }

    private void ClearTrackedState()
    {
        _dbContext.ChangeTracker.Clear();
        _pendingChangeOrders.Clear();
    }

    private void AddChange(
        CalibrationProject project,
        string entityType,
        Guid entityId,
        long entityRevision,
        CalibrationChangeType changeType,
        string mutationId,
        CalibrationActor actor,
        object? tombstone = null)
    {
        CalibrationChange change = new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = project.OwnerUserId,
            ProjectId = project.Id,
            EntityType = entityType,
            EntityId = entityId,
            EntityRevision = entityRevision,
            ChangeType = changeType,
            TombstoneJson = tombstone is null ? null : JsonSerializer.Serialize(tombstone, JsonOptions),
            MutationId = ComputeCanonicalHash(new
            {
                Scope = GetScope(actor),
                EntityType = entityType,
                EntityId = entityId,
                MutationId = mutationId,
            }),
            ActorSubject = actor.Subject,
            OccurredAtUtc = UtcNow(),
        };
        long pendingOrder = _nextPendingChangeOrder++;

        // Sequence is the primary key and is application-assigned. Give pending
        // rows distinct temporary keys until the transactional allocator replaces
        // them with their committed positive cursor values.
        change.Sequence = -(pendingOrder + 1);
        _pendingChangeOrders.Add(change, pendingOrder);
        _ = _dbContext.CalibrationChanges.Add(change);
    }

    private static CalibrationApiResult<T> RevisionConflict<T>(
        long currentRevision,
        long? submittedBaseRevision,
        T currentRepresentation) =>
        CalibrationApiResult<T>.Failure(
            StatusCodes.Status412PreconditionFailed,
            "revision_conflict",
            new(
                currentRevision,
                submittedBaseRevision,
                currentRepresentation,
                ["revision"],
                ["refresh", "fork", "discard-local-change"]));

    private static CalibrationApiResult<T>? CheckPrecondition<T>(
        long currentRevision,
        Guid resourceId,
        string resourceType,
        long? baseRevision,
        string? ifMatch,
        T current)
    {
        if (!baseRevision.HasValue || string.IsNullOrWhiteSpace(ifMatch))
        {
            return CalibrationApiResult<T>.Failure(
                StatusCodes.Status428PreconditionRequired,
                "precondition_required");
        }

        string expectedEtag = ETag(resourceType, resourceId, baseRevision.Value);
        if (baseRevision.Value != currentRevision ||
            !string.Equals(ifMatch, expectedEtag, StringComparison.Ordinal))
        {
            return CalibrationApiResult<T>.Failure(
                StatusCodes.Status412PreconditionFailed,
                "revision_conflict",
                new(
                    currentRevision,
                    baseRevision,
                    current,
                    ["revision"],
                    ["refresh", "fork", "discard-local-change"]));
        }

        return null;
    }

    private static CalibrationApiResult<T> NotFound<T>() =>
        CalibrationApiResult<T>.Failure(StatusCodes.Status404NotFound, "calibration_resource_not_found");

    private static CalibrationApiResult<T> Validation<T>(string code) =>
        CalibrationApiResult<T>.Failure(StatusCodes.Status422UnprocessableEntity, code);

    private static string? ValidateProjectCreate(CalibrationProjectCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId) || request.ClientId.Length > 128 ||
            string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128 ||
            string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200 ||
            request.PrinterId == Guid.Empty || request.PrinterConfigurationRevision < 1 ||
            string.IsNullOrWhiteSpace(request.FilamentProvider) || request.FilamentProvider.Length > 64 ||
            string.IsNullOrWhiteSpace(request.FilamentProductId) || request.FilamentProductId.Length > 256 ||
            string.IsNullOrWhiteSpace(request.FilamentProductName) || request.FilamentProductName.Length > 256 ||
            string.IsNullOrWhiteSpace(request.FilamentMaterial) || request.FilamentMaterial.Length > 64 ||
            !IsJsonContainer(request.FilamentSnapshot) || !IsJsonContainer(request.OrderedSteps) ||
            !IsJsonContainer(request.CurrentSelections) ||
            !Enum.TryParse<CalibrationExperienceMode>(request.ExperienceMode, true, out _))
        {
            return "project_invalid";
        }

        string? payloadSafetyCode = new[]
        {
            request.FilamentSnapshot,
            request.OrderedSteps,
            request.CurrentSelections,
        }
            .Select(ValidateSafeJson)
            .FirstOrDefault(code => code is not null);
        if (payloadSafetyCode is not null)
        {
            return payloadSafetyCode;
        }

        return null;
    }

    // Strict complete-or-missing selection: both absent is allowed by the existing create contract
    // (SelectedToolheadId/SelectedToolheadIndex are nullable), but any partial, mismatched, or
    // unknown pair is rejected against the server-captured immutable snapshot toolhead list.
    private static string? ValidateSelectedToolhead(
        CalibrationProjectCreateRequest request,
        CalibrationContextDto context)
    {
        bool hasId = request.SelectedToolheadId.HasValue;
        bool hasIndex = request.SelectedToolheadIndex.HasValue;
        if (!hasId && !hasIndex)
        {
            return null;
        }

        if (hasId != hasIndex)
        {
            return "toolhead_selection_invalid";
        }

        Guid selectedId = request.SelectedToolheadId!.Value;
        int selectedIndex = request.SelectedToolheadIndex!.Value;
        if (selectedId == Guid.Empty)
        {
            return "toolhead_selection_invalid";
        }

        IReadOnlyList<CalibrationToolheadDto> capturedToolheads = context.Snapshot.Toolheads;
        if (capturedToolheads.Any(toolhead => toolhead.Id == selectedId && toolhead.Index == selectedIndex))
        {
            return null;
        }

        return "toolhead_selection_invalid";
    }

    private static CalibrationExperienceMode ParseExperienceMode(string value) =>
        Enum.Parse<CalibrationExperienceMode>(value, ignoreCase: true);

    private static string DeriveAttemptStatus(string eventType) =>
        eventType.Trim().ToLowerInvariant() switch
        {
            "created" or "planned" or "queued" => "planned",
            "started" or "running" => "running",
            "completed" or "succeeded" => "succeeded",
            "failed" => "failed",
            "cancelled" or "canceled" => "cancelled",
            _ => "recorded",
        };

    private static CalibrationProjectDto MapProject(CalibrationProject project) =>
        new(
            project.Id,
            project.Name,
            project.LifecycleStatus.ToString(),
            project.ExperienceMode.ToString(),
            project.PrinterId,
            project.SelectedToolheadId,
            project.SelectedToolheadIndex,
            new(
                project.FilamentProvider,
                project.FilamentProductId,
                project.FilamentSku,
                project.FilamentVendor,
                project.FilamentProductName,
                project.FilamentMaterial,
                project.FilamentDiameter,
                project.FilamentColor,
                project.FilamentTypeId,
                project.SpoolmanFilamentId,
                project.LocalSpoolId,
                project.SpoolmanSpoolId,
                Parse(project.FilamentSnapshotJson)),
            Parse(project.OrderedStepsJson),
            project.CurrentStep,
            Parse(project.CurrentSelectionsJson),
            project.Revision,
            project.CreatedAtUtc,
            project.UpdatedAtUtc,
            project.CompletedAtUtc,
            project.DeletedAtUtc);

    private static CalibrationDraftDto MapDraft(CalibrationDraft draft) =>
        new(
            draft.Id,
            draft.ProjectId,
            draft.StepId,
            draft.DeviceLineageId,
            draft.Method,
            Parse(draft.ValuesJson),
            Parse(draft.PrerequisitesJson),
            draft.Revision,
            draft.CreatedAtUtc,
            draft.UpdatedAtUtc,
            draft.DeletedAtUtc);

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

    private static CalibrationAttemptDto MapAttempt(CalibrationAttempt attempt, string status) =>
        new(
            attempt.Id,
            attempt.ProjectId,
            attempt.Sequence,
            attempt.ParentAttemptId,
            attempt.CalibrationKind,
            attempt.Method,
            attempt.DefinitionVersion,
            Parse(attempt.InputJson),
            Parse(attempt.SpecificationJson),
            attempt.SpecificationSha256,
            Parse(attempt.ProfileSnapshotIdsJson),
            attempt.ActualSpoolSnapshotJson is null ? null : Parse(attempt.ActualSpoolSnapshotJson),
            status,
            attempt.CreatedAtUtc);

    private static CalibrationAttemptEventDto MapEvent(CalibrationAttemptEvent @event) =>
        new(
            @event.Id,
            @event.AttemptId,
            @event.Sequence,
            @event.EventType,
            @event.DerivedStatus,
            @event.Model3DId,
            @event.SliceJobId,
            @event.ArtifactId,
            @event.GcodeFileId,
            @event.PrintJobId,
            @event.CalibrationOrchestrationId,
            @event.ErrorCode,
            @event.ErrorJson is null ? null : Parse(@event.ErrorJson),
            @event.RetryNumber,
            @event.OperationId,
            @event.OccurredAtUtc);

    private static CalibrationObservationDto MapObservation(CalibrationObservation observation) =>
        new(
            observation.Id,
            observation.AttemptId,
            observation.Sequence,
            observation.ObservationType,
            Parse(observation.MeasurementsJson),
            Parse(observation.ResultJson),
            Parse(observation.UnitsJson),
            observation.Confidence,
            observation.RetestRecommended,
            observation.Notes,
            observation.SelectionParentObservationId,
            observation.SelectionReason,
            observation.OperationId,
            observation.ObservedAtUtc);

    private static CalibrationPhotoDto MapPhoto(CalibrationPhoto photo) =>
        new(
            photo.Id,
            photo.ProjectId,
            photo.AttemptId,
            photo.ClientUploadId,
            photo.OriginalFileName,
            photo.ContentType,
            photo.SizeBytes,
            photo.Sha256,
            photo.Width,
            photo.Height,
            photo.CapturedAtUtc,
            photo.Caption,
            photo.SortOrder,
            photo.Revision,
            photo.CreatedAtUtc,
            photo.DeletedAtUtc,
            photo.PurgedAtUtc);

    private static CalibrationChangeDto MapChange(CalibrationChange change) =>
        new(
            change.Sequence,
            change.ProjectId,
            change.EntityType,
            change.EntityId,
            change.EntityRevision,
            change.ChangeType.ToString(),
            change.TombstoneJson is null ? null : Parse(change.TombstoneJson),
            change.MutationId,
            change.OccurredAtUtc);

    private static bool IsJsonContainer(JsonElement value) =>
        value.ValueKind is JsonValueKind.Object or JsonValueKind.Array;

    /// <summary>
    /// D8: validates a submitted observation measurement against the attempt method's
    /// canonical semantic range, when the method is recognized, the resolved kind has a
    /// defined range, and the measurement key is present in the payload.
    /// </summary>
    /// <param name="attemptMethod">The immutable attempt's recorded method name.</param>
    /// <param name="measurements">The submitted <c>measurements</c> JSON payload.</param>
    /// <returns>A validation error code, or <see langword="null"/> when no range applies or the value is in range.</returns>
    private static string? ValidateMeasurementRange(string attemptMethod, JsonElement measurements)
    {
        if (!CalibrationMethods.TryParse(attemptMethod, out CalibrationMethod method))
        {
            return null;
        }

        CalibrationMeasurementRange? range =
            CalibrationMeasurementRanges.ForKind(CalibrationMethodKinds.ToKind(method));
        if (range is null ||
            measurements.ValueKind != JsonValueKind.Object ||
            !measurements.TryGetProperty(range.MeasurementKey, out JsonElement measurementValue))
        {
            return null;
        }

        if (measurementValue.ValueKind != JsonValueKind.Number ||
            !measurementValue.TryGetDecimal(out decimal value))
        {
            return "observation_measurement_invalid";
        }

        return value < range.Minimum || value > range.Maximum
            ? "observation_measurement_out_of_range"
            : null;
    }

    private static string? ValidateSafeJson(JsonElement value)
    {
        CalibrationProfileSafetyResult safety =
            CalibrationProfileSafetyValidator.Validate(value.GetRawText(), "payload");
        return safety.IsSafe ? null : safety.Code;
    }

    private static string Json(JsonElement value) => value.GetRawText();

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string ComputeCanonicalHash<T>(T value)
    {
        JsonNode? node = JsonSerializer.SerializeToNode(value, JsonOptions);
        ArgumentNullException.ThrowIfNull(node);
        NormalizeOperationIdentifiers(node);
        return CalibrationSnapshotBuilder.ComputeSha256(node);
    }

    // Direct operations bind their route resource identity (parent aggregate) and operation type
    // into the canonical hash so replaying the same client operation ID against a different route
    // target deterministically produces a distinct hash. FindReplayAsync's canonical-hash equality
    // check then returns 409 idempotency_payload_mismatch instead of a stored response that belongs
    // to another resource. Mirrors CreateMutationIdentity used by the sync lane.
    private static string ComputeDirectOperationHash<T>(
        string operationType,
        string routeResourceType,
        Guid routeResourceId,
        T request) =>
        ComputeCanonicalHash(new
        {
            OperationType = operationType,
            RouteResourceType = routeResourceType,
            RouteResourceId = routeResourceId,
            Payload = request,
        });

    private static void NormalizeOperationIdentifiers(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach ((string propertyName, JsonNode? propertyValue) in jsonObject.ToArray())
            {
                if (propertyValue is JsonValue jsonValue &&
                    (string.Equals(propertyName, "clientId", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(propertyName, "operationId", StringComparison.OrdinalIgnoreCase)) &&
                    jsonValue.TryGetValue(out string? identifier) &&
                    identifier is not null)
                {
                    jsonObject[propertyName] = identifier.Trim();
                    continue;
                }

                if (propertyValue is not null)
                {
                    NormalizeOperationIdentifiers(propertyValue);
                }
            }

            return;
        }

        if (node is JsonArray jsonArray)
        {
            foreach (JsonNode? item in jsonArray.Where(item => item is not null))
            {
                NormalizeOperationIdentifiers(item!);
            }
        }
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ETag(string resourceType, Guid resourceId, long revision) =>
        $"\"calibration-{resourceType}-{resourceId:N}-{revision}\"";

    private static string MutationId() => Guid.NewGuid().ToString("N");

    private static string GetScope(CalibrationActor actor) =>
        actor.IsFarmAdmin ? $"farm-admin:{actor.UserId:N}" : $"owner:{actor.UserId:N}";

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;
}
