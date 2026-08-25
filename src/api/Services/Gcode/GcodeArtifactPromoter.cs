using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Logging;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Farm.Web.Api.Services.Calibration;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services.Gcode;

/// <summary>
/// Streams completed slicer artifacts into the G-code library behind a durable, database-idempotent
/// checkpoint.
/// </summary>
/// <remarks>
/// The source artifact and the promoted file live in different database contexts. Instead of pretending
/// those two commits are atomic, every promotion writes a checkpoint first, pins the source artifact
/// against cleanup, copies the bytes, commits the terminal result and only then acknowledges the source.
/// Any interruption leaves a recoverable state that <see cref="ReconcilePendingAsync"/> resolves.
/// </remarks>
public sealed class GcodeArtifactPromoter(
    AppDbContext dbContext,
    IGcodeFilesService gcodeFiles,
    IStoragePathService storagePaths,
    GcodePromotionReconcilerState reconcilerState,
    ILogger<GcodeArtifactPromoter> logger,
    IArtifactsService? artifacts = null,
    IArtifactsRepository? artifactsRepository = null,
    ISliceJobRepository? sliceJobs = null) : IGcodeArtifactPromoter
{
    private const string ManifestSchemaVersion = "1.0";
    private const string GeneratorName = "printfarmer.gcode-artifact-promoter";

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly IGcodeFilesService _gcodeFiles = gcodeFiles ?? throw new ArgumentNullException(nameof(gcodeFiles));
    private readonly IStoragePathService _storagePaths = storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));
    private readonly GcodePromotionReconcilerState _reconcilerState =
        reconcilerState ?? throw new ArgumentNullException(nameof(reconcilerState));

    private readonly ILogger<GcodeArtifactPromoter> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IArtifactsService? _artifacts = artifacts;
    private readonly IArtifactsRepository? _artifactsRepository = artifactsRepository;
    private readonly ISliceJobRepository? _sliceJobs = sliceJobs;

    /// <inheritdoc/>
    public async Task<GcodePromotionCapabilityDto> GetCapabilityAsync(CancellationToken cancellationToken)
    {
        bool artifactSourceAvailable =
            _artifacts is not null && _artifactsRepository is not null && _sliceJobs is not null;
        bool libraryStorageWritable = IsLibraryStorageWritable();
        bool checkpointStoreAvailable = await IsCheckpointStoreAvailableAsync(cancellationToken);
        bool reconcilerHealthy = _reconcilerState.IsHealthy;
        bool operational =
            artifactSourceAvailable &&
            libraryStorageWritable &&
            checkpointStoreAvailable &&
            reconcilerHealthy;

        string? unavailableCode = operational
            ? null
            : !artifactSourceAvailable
                ? "artifact_source_unroutable"
                : !libraryStorageWritable
                    ? "gcode_library_storage_unavailable"
                    : !checkpointStoreAvailable
                        ? "promotion_checkpoint_store_unavailable"
                        : "promotion_reconciler_unavailable";

        return new GcodePromotionCapabilityDto
        {
            Operational = operational,
            ArtifactSourceAvailable = artifactSourceAvailable,
            LibraryStorageWritable = libraryStorageWritable,
            CheckpointStoreAvailable = checkpointStoreAvailable,
            ReconcilerHealthy = reconcilerHealthy,
            UnavailableCode = unavailableCode,
        };
    }

    /// <inheritdoc/>
    public async Task<CalibrationApiResult<GcodePromotionDto>> PromoteAsync(
        GcodeArtifactPromotionRequest request,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        CalibrationApiResult<GcodePromotionDto>? invalid = ValidateRequestShape(request);
        if (invalid is not null)
        {
            return invalid;
        }

        GcodePromotionCapabilityDto capability = await GetCapabilityAsync(cancellationToken);
        if (!capability.Operational)
        {
            return Failure(StatusCodes.Status503ServiceUnavailable, "promotion_dependency_unavailable");
        }

        Artifact? artifact = await _artifactsRepository!.GetByIdAsync(request.SourceArtifactId, cancellationToken);
        if (artifact is null)
        {
            return Failure(StatusCodes.Status404NotFound, "source_artifact_not_found");
        }

        SliceJob? job = await _sliceJobs!.GetByIdAsync(artifact.JobId, cancellationToken);
        if (job is null)
        {
            return Failure(StatusCodes.Status404NotFound, "slice_job_not_found");
        }

        CalibrationApiResult<GcodePromotionDto>? lineageFailure =
            await ValidateLineageAsync(request, artifact, job, actor, cancellationToken);
        if (lineageFailure is not null)
        {
            return lineageFailure;
        }

        string contentSha256 = NormalizeHex(artifact.Sha256);
        string operationScope = GetOperationScope(job.UserId);
        string operationId = request.OperationId.Trim();
        string requestSha256 = ComputeRequestSha256(request, contentSha256);

        GcodePromotionCheckpoint? checkpoint = await _dbContext.GcodePromotionCheckpoints
            .FirstOrDefaultAsync(
                candidate => candidate.OperationScope == operationScope && candidate.OperationId == operationId,
                cancellationToken);
        if (checkpoint is not null)
        {
            if (!string.Equals(checkpoint.RequestSha256, requestSha256, StringComparison.Ordinal))
            {
                return Failure(StatusCodes.Status409Conflict, "idempotency_payload_mismatch");
            }
        }
        else
        {
            // Content-level deduplication: the same artifact bytes never produce a second library file,
            // even when a different operation key asks for them.
            GcodePromotionCheckpoint? byContent = await _dbContext.GcodePromotionCheckpoints
                .FirstOrDefaultAsync(
                    candidate => candidate.SourceArtifactId == request.SourceArtifactId &&
                        candidate.SourceContentSha256 == contentSha256,
                    cancellationToken);
            if (byContent is not null)
            {
                return byContent.State == GcodePromotionState.Completed
                    ? CalibrationApiResult<GcodePromotionDto>.Success(
                        await ToDtoAsync(byContent, cancellationToken),
                        StatusCodes.Status200OK,
                        replayed: true)
                    : Failure(StatusCodes.Status409Conflict, "promotion_in_progress");
            }

            checkpoint = NewCheckpoint(request, artifact, job, operationScope, operationId, requestSha256, contentSha256);
            _ = _dbContext.GcodePromotionCheckpoints.Add(checkpoint);
            try
            {
                _ = await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // A concurrent promotion won the unique index; adopt its checkpoint instead of writing
                // a second file for the same artifact.
                _dbContext.ChangeTracker.Clear();
                checkpoint = await _dbContext.GcodePromotionCheckpoints
                    .FirstOrDefaultAsync(
                        candidate =>
                            (candidate.OperationScope == operationScope && candidate.OperationId == operationId) ||
                            (candidate.SourceArtifactId == request.SourceArtifactId &&
                                candidate.SourceContentSha256 == contentSha256),
                        cancellationToken);
                if (checkpoint is null)
                {
                    return Failure(StatusCodes.Status503ServiceUnavailable, "promotion_checkpoint_unavailable");
                }

                if (!string.Equals(checkpoint.RequestSha256, requestSha256, StringComparison.Ordinal))
                {
                    return Failure(StatusCodes.Status409Conflict, "idempotency_payload_mismatch");
                }
            }
        }

        if (checkpoint.State == GcodePromotionState.Completed)
        {
            await AcknowledgeSourceAsync(checkpoint, cancellationToken);
            return CalibrationApiResult<GcodePromotionDto>.Success(
                await ToDtoAsync(checkpoint, cancellationToken),
                StatusCodes.Status200OK,
                replayed: true);
        }

        if (checkpoint.State == GcodePromotionState.Failed)
        {
            return Failure(StatusCodes.Status409Conflict, checkpoint.FailureCode ?? "promotion_failed");
        }

        return await ExecutePromotionAsync(checkpoint, request.VirtualDirectory, artifact, job, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<CalibrationApiResult<GcodePromotionDto>> GetPromotionAsync(
        string operationId,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return Failure(StatusCodes.Status400BadRequest, "invalid_promotion_operation");
        }

        string trimmed = operationId.Trim();
        string operationScope = GetOperationScope(actor.UserId);
        GcodePromotionCheckpoint? checkpoint = await _dbContext.GcodePromotionCheckpoints
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.OperationScope == operationScope && candidate.OperationId == trimmed,
                cancellationToken);
        if (checkpoint is null && actor.IsFarmAdmin)
        {
            // An audited farm administrator may resolve an operation key in any owner scope. The key
            // is only unique per owner, so an ambiguous match is reported rather than guessed.
            List<GcodePromotionCheckpoint> matches = await _dbContext.GcodePromotionCheckpoints
                .AsNoTracking()
                .Where(candidate => candidate.OperationId == trimmed)
                .Take(2)
                .ToListAsync(cancellationToken);
            if (matches.Count > 1)
            {
                return Failure(StatusCodes.Status409Conflict, "promotion_operation_ambiguous");
            }

            checkpoint = matches.FirstOrDefault();
        }

        return await AuthorizeAndProjectAsync(checkpoint, actor, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<CalibrationApiResult<GcodePromotionDto>> GetPromotionByArtifactAsync(
        Guid sourceArtifactId,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        GcodePromotionCheckpoint? checkpoint = await _dbContext.GcodePromotionCheckpoints
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.SourceArtifactId == sourceArtifactId, cancellationToken);
        return await AuthorizeAndProjectAsync(checkpoint, actor, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> TryReserveSourceArtifactAsync(
        Guid sourceArtifactId,
        string operationId,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        PromotionOperationIdentity? identity =
            await ResolveOperationIdentityAsync(sourceArtifactId, operationId, actor, cancellationToken);
        return identity is not null &&
            await _artifactsRepository!.TryPinForPromotionAsync(
                sourceArtifactId,
                checkpointId: null,
                identity.Value,
                DateTime.UtcNow,
                cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ReleaseSourceArtifactReservationAsync(
        Guid sourceArtifactId,
        string operationId,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        PromotionOperationIdentity? identity =
            await ResolveOperationIdentityAsync(sourceArtifactId, operationId, actor, cancellationToken);
        if (identity is null)
        {
            return;
        }

        _ = await _artifactsRepository!.ReleasePromotionPinAsync(
            sourceArtifactId,
            identity.Value,
            cancellationToken);
    }

    /// <summary>
    /// Resolves the owner-scoped identity an operation key has for one artifact, after checking that
    /// the caller may act on the artifact's slice job.
    /// </summary>
    /// <param name="sourceArtifactId">The source artifact identity.</param>
    /// <param name="operationId">The caller-supplied idempotency operation key.</param>
    /// <param name="actor">The authenticated caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The scoped identity, or <see langword="null"/> when it cannot be established.</returns>
    private async Task<PromotionOperationIdentity?> ResolveOperationIdentityAsync(
        Guid sourceArtifactId,
        string operationId,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(operationId) || _artifactsRepository is null || _sliceJobs is null)
        {
            return null;
        }

        Artifact? artifact = await _artifactsRepository.GetByIdAsync(sourceArtifactId, cancellationToken);
        SliceJob? job = artifact is null
            ? null
            : await _sliceJobs.GetByIdAsync(artifact.JobId, cancellationToken);
        if (job is null || (!actor.IsFarmAdmin && job.UserId != actor.UserId))
        {
            return null;
        }

        string trimmed = operationId.Trim();
        return new PromotionOperationIdentity(
            GcodePromotionOperationKey.Compute(job.UserId, trimmed),
            trimmed);
    }

    /// <inheritdoc/>
    public async Task<CalibrationApiResult<GcodePromotionDto>> ReconcileAsync(
        Guid checkpointId,
        CancellationToken cancellationToken)
    {
        GcodePromotionCheckpoint? checkpoint = await _dbContext.GcodePromotionCheckpoints
            .FirstOrDefaultAsync(candidate => candidate.Id == checkpointId, cancellationToken);
        if (checkpoint is null)
        {
            return Failure(StatusCodes.Status404NotFound, "promotion_not_found");
        }

        if (checkpoint.State == GcodePromotionState.Completed)
        {
            await AcknowledgeSourceAsync(checkpoint, cancellationToken);
            return CalibrationApiResult<GcodePromotionDto>.Success(
                await ToDtoAsync(checkpoint, cancellationToken),
                StatusCodes.Status200OK,
                replayed: true);
        }

        if (checkpoint.State == GcodePromotionState.Failed)
        {
            // A permanent failure still owes the slicer context a release, otherwise the source stays
            // pinned against cleanup forever.
            await ReleaseFailedPromotionAsync(checkpoint, cancellationToken);
            return Failure(StatusCodes.Status409Conflict, checkpoint.FailureCode ?? "promotion_failed");
        }

        // The unknown outcome is resolved by evidence, never by assumption: if the file identity the
        // checkpoint reserved already exists, the bytes landed before the crash.
        GcodeFile? committed = await _dbContext.GcodeFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(file => file.Id == checkpoint.GcodeFileId, cancellationToken);
        if (committed is not null)
        {
            GcodePromotionCheckpoint resolved = await CompleteCheckpointAsync(
                checkpoint,
                committed.Id,
                cancellationToken);
            return CalibrationApiResult<GcodePromotionDto>.Success(
                await ToDtoAsync(resolved, cancellationToken),
                StatusCodes.Status200OK,
                replayed: true);
        }

        if (_artifacts is null || _artifactsRepository is null || _sliceJobs is null)
        {
            return Failure(StatusCodes.Status503ServiceUnavailable, "promotion_dependency_unavailable");
        }

        Artifact? artifact = await _artifactsRepository.GetByIdAsync(checkpoint.SourceArtifactId, cancellationToken);
        SliceJob? job = artifact is null
            ? null
            : await _sliceJobs.GetByIdAsync(artifact.JobId, cancellationToken);
        if (artifact is null || job is null)
        {
            await FailCheckpointAsync(checkpoint, "source_artifact_unavailable", cancellationToken);
            return Failure(StatusCodes.Status409Conflict, "source_artifact_unavailable");
        }

        checkpoint.ReconcileAttempts++;
        return await ExecutePromotionAsync(checkpoint, virtualDirectory: null, artifact, job, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<int> ReconcilePendingAsync(int maxCheckpoints, CancellationToken cancellationToken)
    {
        int limit = Math.Clamp(maxCheckpoints, 1, 200);
        List<Guid> outstanding = await _dbContext.GcodePromotionCheckpoints
            .AsNoTracking()
            .Where(checkpoint =>
                checkpoint.State == GcodePromotionState.Pending ||
                checkpoint.State == GcodePromotionState.BytesStored ||
                checkpoint.SourceAcknowledgedAtUtc == null)
            .OrderBy(checkpoint => checkpoint.UpdatedAtUtc)
            .Select(checkpoint => checkpoint.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

        int resolved = 0;
        foreach (Guid checkpointId in outstanding)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _dbContext.ChangeTracker.Clear();
            CalibrationApiResult<GcodePromotionDto> result;
            try
            {
                result = await ReconcileAsync(checkpointId, cancellationToken);
            }
            catch (DbUpdateException exception)
            {
                // One checkpoint that cannot be written must not abort the pass: the background
                // reconciler would report itself unhealthy and disable promotion deployment-wide.
                _dbContext.ChangeTracker.Clear();
                _logger.LogWarning(
                    exception,
                    "Promotion checkpoint {CheckpointId} could not be written and stays outstanding",
                    checkpointId);
                continue;
            }
            catch (System.Data.Common.DbException exception)
            {
                _dbContext.ChangeTracker.Clear();
                _logger.LogWarning(
                    exception,
                    "Promotion checkpoint {CheckpointId} could not be read and stays outstanding",
                    checkpointId);
                continue;
            }

            if (result.IsSuccess && result.Value?.SourceAcknowledged == true)
            {
                resolved++;
            }
            else if (!result.IsSuccess)
            {
                _logger.LogWarning(
                    "Promotion checkpoint {CheckpointId} stays unresolved ({Code})",
                    checkpointId,
                    result.Code);
            }
        }

        return resolved;
    }

    /// <inheritdoc/>
    public async Task<bool> IsSourceArtifactCleanupSafeAsync(
        Guid sourceArtifactId,
        CancellationToken cancellationToken)
    {
        GcodePromotionCheckpoint? checkpoint = await _dbContext.GcodePromotionCheckpoints
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.SourceArtifactId == sourceArtifactId, cancellationToken);
        return checkpoint is null ||
            (checkpoint.State is GcodePromotionState.Completed or GcodePromotionState.Failed &&
                checkpoint.SourceAcknowledgedAtUtc is not null);
    }

    private static CalibrationApiResult<GcodePromotionDto> Failure(int statusCode, string code) =>
        CalibrationApiResult<GcodePromotionDto>.Failure(statusCode, code);

    private static string GetOperationScope(Guid ownerUserId) =>
        GcodePromotionOperationKey.ScopeFor(ownerUserId);

    private static PromotionOperationIdentity OperationIdentity(GcodePromotionCheckpoint checkpoint) =>
        new(checkpoint.ScopedOperationKey(), checkpoint.OperationId);

    private static string NormalizeHex(string? value) =>
        (value ?? string.Empty).Trim().Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static bool IsHexDigest(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static CalibrationApiResult<GcodePromotionDto>? ValidateRequestShape(
        GcodeArtifactPromotionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Trim().Length > 128)
        {
            return Failure(StatusCodes.Status400BadRequest, "invalid_promotion_operation");
        }

        if (request.SourceArtifactId == Guid.Empty || request.SourceSliceJobId == Guid.Empty)
        {
            return Failure(StatusCodes.Status400BadRequest, "invalid_promotion_source");
        }

        if (request.ExpectedSizeBytes <= 0)
        {
            return Failure(StatusCodes.Status400BadRequest, "invalid_promotion_size");
        }

        return string.IsNullOrWhiteSpace(request.ExpectedSha256) || !IsHexDigest(NormalizeHex(request.ExpectedSha256))
            ? Failure(StatusCodes.Status400BadRequest, "invalid_promotion_digest")
            : null;
    }

    private async Task<CalibrationApiResult<GcodePromotionDto>?> ValidateLineageAsync(
        GcodeArtifactPromotionRequest request,
        Artifact artifact,
        SliceJob job,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        if (!actor.IsFarmAdmin && job.UserId != actor.UserId)
        {
            return Failure(StatusCodes.Status403Forbidden, "resource_forbidden");
        }

        if (artifact.JobId != request.SourceSliceJobId || job.Id != request.SourceSliceJobId)
        {
            return Failure(StatusCodes.Status409Conflict, "artifact_job_mismatch");
        }

        if (request.SourceWorkerId.HasValue && artifact.WorkerId != request.SourceWorkerId)
        {
            return Failure(StatusCodes.Status409Conflict, "artifact_worker_mismatch");
        }

        if (!string.Equals(job.Status, SliceJobStatus.Completed, StringComparison.Ordinal))
        {
            return Failure(StatusCodes.Status409Conflict, "slice_job_not_completed");
        }

        if (!string.Equals(artifact.Kind, SlicerArtifactKinds.Gcode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(request.ArtifactKind, SlicerArtifactKinds.Gcode, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(StatusCodes.Status422UnprocessableEntity, "unsupported_artifact_kind");
        }

        IReadOnlyList<string> acceptedMimeTypes = SlicerArtifactKinds.AcceptedMimeTypes(SlicerArtifactKinds.Gcode);
        string contentType = string.IsNullOrWhiteSpace(artifact.ContentType)
            ? "application/octet-stream"
            : artifact.ContentType.Split(';')[0].Trim();
        if (!acceptedMimeTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
        {
            return Failure(StatusCodes.Status422UnprocessableEntity, "unsupported_artifact_media_type");
        }

        if (artifact.SizeBytes != request.ExpectedSizeBytes)
        {
            return Failure(StatusCodes.Status409Conflict, "artifact_size_mismatch");
        }

        if (!string.Equals(NormalizeHex(artifact.Sha256), NormalizeHex(request.ExpectedSha256), StringComparison.Ordinal))
        {
            return Failure(StatusCodes.Status409Conflict, "artifact_hash_mismatch");
        }

        if (!MatchesJobLineage(job.CalibrationProjectId, request.CalibrationProjectId) ||
            !MatchesJobLineage(job.CalibrationAttemptId, request.CalibrationAttemptId) ||
            !MatchesJobLineage(job.CalibrationOrchestrationId, request.CalibrationOrchestrationId))
        {
            return Failure(StatusCodes.Status409Conflict, "calibration_lineage_mismatch");
        }

        Guid? projectId = request.CalibrationProjectId ?? job.CalibrationProjectId;
        if (projectId.HasValue)
        {
            CalibrationProject? project = await _dbContext.CalibrationProjects
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == projectId.Value, cancellationToken);
            if (project is null)
            {
                return Failure(StatusCodes.Status404NotFound, "calibration_project_not_found");
            }

            if (!actor.IsFarmAdmin && project.OwnerUserId != actor.UserId)
            {
                return Failure(StatusCodes.Status403Forbidden, "resource_forbidden");
            }
        }

        Guid? attemptId = request.CalibrationAttemptId ?? job.CalibrationAttemptId;
        if (attemptId.HasValue)
        {
            CalibrationAttempt? attempt = await _dbContext.CalibrationAttempts
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == attemptId.Value, cancellationToken);
            if (attempt is null)
            {
                return Failure(StatusCodes.Status404NotFound, "calibration_attempt_not_found");
            }

            if (projectId.HasValue && attempt.ProjectId != projectId.Value)
            {
                return Failure(StatusCodes.Status409Conflict, "calibration_lineage_mismatch");
            }
        }

        return null;
    }

    private static bool MatchesJobLineage(Guid? jobValue, Guid? requestValue) =>
        !requestValue.HasValue || !jobValue.HasValue || jobValue.Value == requestValue.Value;

    private static GcodePromotionCheckpoint NewCheckpoint(
        GcodeArtifactPromotionRequest request,
        Artifact artifact,
        SliceJob job,
        string operationScope,
        string operationId,
        string requestSha256,
        string contentSha256)
    {
        DateTime nowUtc = DateTime.UtcNow;
        return new GcodePromotionCheckpoint
        {
            Id = Guid.NewGuid(),
            OwnerUserId = job.UserId,
            OperationScope = operationScope,
            OperationId = operationId,
            RequestSha256 = requestSha256,
            SourceArtifactId = artifact.Id,
            SourceSliceJobId = job.Id,
            SourceWorkerId = artifact.WorkerId,
            SourceContentSha256 = contentSha256,
            SourceSizeBytes = artifact.SizeBytes,
            CalibrationProjectId = request.CalibrationProjectId ?? job.CalibrationProjectId,
            CalibrationAttemptId = request.CalibrationAttemptId ?? job.CalibrationAttemptId,
            CalibrationOrchestrationId = request.CalibrationOrchestrationId ?? job.CalibrationOrchestrationId,

            // Reserved before any byte is copied so an interrupted promotion is decidable by lookup.
            GcodeFileId = Guid.NewGuid(),
            State = GcodePromotionState.Pending,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            Revision = 1,
        };
    }

    private async Task<CalibrationApiResult<GcodePromotionDto>> ExecutePromotionAsync(
        GcodePromotionCheckpoint checkpoint,
        string? virtualDirectory,
        Artifact artifact,
        SliceJob job,
        CancellationToken cancellationToken)
    {
        // Pin first: cleanup must never reclaim bytes whose promotion outcome is still unknown.
        bool pinned = await _artifactsRepository!.TryPinForPromotionAsync(
            checkpoint.SourceArtifactId,
            checkpoint.Id,
            OperationIdentity(checkpoint),
            DateTime.UtcNow,
            cancellationToken);
        if (!pinned)
        {
            return Failure(StatusCodes.Status409Conflict, "promotion_in_progress");
        }

        string manifestJson = await BuildManifestAsync(checkpoint, artifact, job, cancellationToken);
        GcodePromotionLineage lineage = await BuildLineageAsync(checkpoint, artifact, job, manifestJson, cancellationToken);

        GcodeStreamIngestResult ingest;
        await using ArtifactContentStream? content = await _artifacts!.OpenReadStreamAsync(
            checkpoint.SourceArtifactId,
            cancellationToken);
        if (content is null)
        {
            await FailCheckpointAsync(checkpoint, "source_artifact_bytes_unavailable", cancellationToken);
            return Failure(StatusCodes.Status409Conflict, "source_artifact_bytes_unavailable");
        }

        try
        {
            ingest = await _gcodeFiles.IngestStreamAsync(
                new GcodeStreamIngestRequest
                {
                    FileId = checkpoint.GcodeFileId,
                    Content = content.Content,
                    FileName = artifact.FileName,
                    ExpectedSha256 = checkpoint.SourceContentSha256,
                    ExpectedSizeBytes = checkpoint.SourceSizeBytes,
                    VirtualDirectory = virtualDirectory,
                    Source = GcodeSource.Generated,
                    Lineage = lineage,
                },
                cancellationToken);
        }
        catch (GcodeStreamIngestException exception)
        {
            await FailCheckpointAsync(checkpoint, exception.Code, cancellationToken);
            return Failure(StatusCodes.Status409Conflict, exception.Code);
        }
        catch (DbUpdateException exception)
        {
            // A concurrent writer won a unique index. That is a conflict between two callers, not a
            // server fault, so it must never surface as a 500.
            _dbContext.ChangeTracker.Clear();
            _logger.LogWarning(
                exception,
                "Promotion {OperationId} lost a durable write race while ingesting its bytes",
                LogSanitizer.Sanitize(checkpoint.OperationId));
            return Failure(StatusCodes.Status409Conflict, "promotion_conflict");
        }

        try
        {
            GcodePromotionCheckpoint committed = await CompleteCheckpointAsync(
                checkpoint,
                ingest.File.Id,
                cancellationToken);
            return CalibrationApiResult<GcodePromotionDto>.Success(
                await ToDtoAsync(committed, cancellationToken),
                ingest.AlreadyExisted ? StatusCodes.Status200OK : StatusCodes.Status201Created,
                replayed: ingest.AlreadyExisted);
        }
        catch (DbUpdateException exception)
        {
            _dbContext.ChangeTracker.Clear();
            _logger.LogWarning(
                exception,
                "Promotion {OperationId} lost a durable write race while committing its result",
                LogSanitizer.Sanitize(checkpoint.OperationId));
            return Failure(StatusCodes.Status409Conflict, "promotion_conflict");
        }
    }

    /// <summary>
    /// Commits the terminal promotion result, adopting a concurrent promoter's commit instead of
    /// failing when both raced to the same checkpoint.
    /// </summary>
    /// <param name="checkpoint">The tracked checkpoint being completed.</param>
    /// <param name="gcodeFileId">The promoted file identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The checkpoint whose durable state now describes the promotion.</returns>
    private async Task<GcodePromotionCheckpoint> CompleteCheckpointAsync(
        GcodePromotionCheckpoint checkpoint,
        Guid gcodeFileId,
        CancellationToken cancellationToken)
    {
        DateTime nowUtc = DateTime.UtcNow;
        checkpoint.GcodeFileId = gcodeFileId;
        checkpoint.State = GcodePromotionState.Completed;
        checkpoint.FailureCode = null;
        checkpoint.CompletedAtUtc ??= nowUtc;
        checkpoint.UpdatedAtUtc = nowUtc;
        checkpoint.Revision++;

        GcodePromotionCheckpoint effective = checkpoint;
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another promoter may have committed the same checkpoint first. Only a durable row that
            // already describes this exact result is allowed to absorb the failure.
            GcodePromotionCheckpoint? winner = await ReloadCheckpointAsync(checkpoint.Id, cancellationToken);
            if (winner is null ||
                winner.State != GcodePromotionState.Completed ||
                winner.GcodeFileId != gcodeFileId)
            {
                throw;
            }

            effective = winner;
        }

        await AcknowledgeSourceAsync(effective, cancellationToken);
        return effective;
    }

    private async Task FailCheckpointAsync(
        GcodePromotionCheckpoint checkpoint,
        string failureCode,
        CancellationToken cancellationToken)
    {
        DateTime nowUtc = DateTime.UtcNow;
        checkpoint.State = GcodePromotionState.Failed;
        checkpoint.FailureCode = failureCode;
        checkpoint.CompletedAtUtc ??= nowUtc;
        checkpoint.UpdatedAtUtc = nowUtc;
        checkpoint.Revision++;

        GcodePromotionCheckpoint effective = checkpoint;
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent promoter already recorded a terminal state for this checkpoint.
            _dbContext.ChangeTracker.Clear();
            return;
        }

        await ReleaseFailedPromotionAsync(effective, cancellationToken);
    }

    /// <summary>
    /// Releases the source artifact after a permanent failure so cleanup is no longer blocked by an
    /// outcome that will never complete.
    /// </summary>
    /// <param name="checkpoint">The tracked failed checkpoint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task ReleaseFailedPromotionAsync(
        GcodePromotionCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        if (checkpoint.SourceAcknowledgedAtUtc is not null || _artifactsRepository is null)
        {
            return;
        }

        _ = await _artifactsRepository.ReleasePromotionPinAsync(
            checkpoint.SourceArtifactId,
            OperationIdentity(checkpoint),
            cancellationToken);
        checkpoint.SourceAcknowledgedAtUtc = DateTime.UtcNow;
        checkpoint.UpdatedAtUtc = checkpoint.SourceAcknowledgedAtUtc.Value;
        checkpoint.Revision++;
        await SaveIgnoringConcurrentWinnerAsync(cancellationToken);
    }

    private async Task AcknowledgeSourceAsync(
        GcodePromotionCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        if (checkpoint.SourceAcknowledgedAtUtc is not null || _artifactsRepository is null)
        {
            return;
        }

        bool acknowledged = await _artifactsRepository.MarkPromotedAsync(
            checkpoint.SourceArtifactId,
            checkpoint.GcodeFileId,
            DateTime.UtcNow,
            cancellationToken);
        if (!acknowledged)
        {
            // The artifact row is gone; the promoted file still carries the full lineage, so the
            // reconciler stops retrying an acknowledgement that can never land.
            _logger.LogWarning(
                "Promotion {OperationId} could not acknowledge its source artifact; lineage remains on the promoted file",
                LogSanitizer.Sanitize(checkpoint.OperationId));
        }

        checkpoint.SourceAcknowledgedAtUtc = DateTime.UtcNow;
        checkpoint.UpdatedAtUtc = checkpoint.SourceAcknowledgedAtUtc.Value;
        checkpoint.Revision++;
        await SaveIgnoringConcurrentWinnerAsync(cancellationToken);
    }

    private async Task SaveIgnoringConcurrentWinnerAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent promoter already recorded the same durable transition.
            _dbContext.ChangeTracker.Clear();
        }
    }

    private async Task<GcodePromotionCheckpoint?> ReloadCheckpointAsync(
        Guid checkpointId,
        CancellationToken cancellationToken)
    {
        _dbContext.ChangeTracker.Clear();
        return await _dbContext.GcodePromotionCheckpoints
            .FirstOrDefaultAsync(candidate => candidate.Id == checkpointId, cancellationToken);
    }

    private async Task<CalibrationApiResult<GcodePromotionDto>> AuthorizeAndProjectAsync(
        GcodePromotionCheckpoint? checkpoint,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        if (checkpoint is null)
        {
            return Failure(StatusCodes.Status404NotFound, "promotion_not_found");
        }

        if (!actor.IsFarmAdmin && checkpoint.OwnerUserId != actor.UserId)
        {
            return Failure(StatusCodes.Status403Forbidden, "resource_forbidden");
        }

        return checkpoint.State switch
        {
            GcodePromotionState.Completed => CalibrationApiResult<GcodePromotionDto>.Success(
                await ToDtoAsync(checkpoint, cancellationToken),
                StatusCodes.Status200OK,
                replayed: true),
            GcodePromotionState.Failed => Failure(
                StatusCodes.Status409Conflict,
                checkpoint.FailureCode ?? "promotion_failed"),
            _ => Failure(StatusCodes.Status503ServiceUnavailable, "promotion_operation_incomplete"),
        };
    }

    private async Task<GcodePromotionDto> ToDtoAsync(
        GcodePromotionCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        string? manifestJson = checkpoint.State == GcodePromotionState.Completed
            ? await _dbContext.GcodeFiles
                .AsNoTracking()
                .Where(file => file.Id == checkpoint.GcodeFileId)
                .Select(file => file.CalibrationManifestJson)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        return new GcodePromotionDto
        {
            OperationId = checkpoint.OperationId,
            SourceArtifactId = checkpoint.SourceArtifactId,
            SourceSliceJobId = checkpoint.SourceSliceJobId,
            GcodeFileId = checkpoint.GcodeFileId,
            ContentSha256 = checkpoint.SourceContentSha256,
            SizeBytes = checkpoint.SourceSizeBytes,
            Status = checkpoint.State.ToString().ToLowerInvariant(),
            CalibrationProjectId = checkpoint.CalibrationProjectId,
            CalibrationAttemptId = checkpoint.CalibrationAttemptId,
            CalibrationOrchestrationId = checkpoint.CalibrationOrchestrationId,
            FailureCode = checkpoint.FailureCode,
            SourceAcknowledged = checkpoint.SourceAcknowledgedAtUtc is not null,
            CalibrationManifestJson = manifestJson,
            CompletedAtUtc = checkpoint.CompletedAtUtc,
        };
    }

    private async Task<GcodePromotionLineage> BuildLineageAsync(
        GcodePromotionCheckpoint checkpoint,
        Artifact artifact,
        SliceJob job,
        string manifestJson,
        CancellationToken cancellationToken)
    {
        CalibrationLineageContext context = await LoadCalibrationContextAsync(checkpoint, cancellationToken);
        Artifact? manifestArtifact = await FindManifestArtifactAsync(job.Id, cancellationToken);
        return new GcodePromotionLineage
        {
            SourceArtifactId = artifact.Id,
            SourceSliceJobId = job.Id,
            SourceWorkerId = artifact.WorkerId,
            CalibrationProjectId = checkpoint.CalibrationProjectId,
            CalibrationAttemptId = checkpoint.CalibrationAttemptId,
            CalibrationOrchestrationId = checkpoint.CalibrationOrchestrationId,
            PromotionOperationId = checkpoint.OperationId,
            PromotionOperationKey = checkpoint.ScopedOperationKey(),
            PromotionCorrelationId = job.CorrelationId,
            SpecificationSha256 = context.SpecificationSha256,
            SourceModelSha256 = job.ModelSha256,
            MachineProfileSha256 = job.MachineProfileSha256 ?? context.MachineProfileSha256,
            ProcessProfileSha256 = job.ProcessProfileSha256 ?? context.ProcessProfileSha256,
            FilamentProfileSha256 = job.FilamentProfileSha256 ?? context.FilamentProfileSha256,
            SlicerEngineName = job.SlicerEngineName,
            SlicerDistribution = job.SlicerDistribution,
            PinnedSlicerVersion = job.SlicerVersion,
            SlicerContainerDigest = job.SlicerContainerDigest ?? context.SlicerContainerDigest,
            FirmwareFamily = context.FirmwareFamily,
            GcodeDialect = context.GcodeDialect,
            GeneratorName = GeneratorName,
            GeneratorVersion = ManifestSchemaVersion,
            CalibrationManifestJson = manifestJson,
            CalibrationManifestSha256 = manifestArtifact is null ? null : NormalizeHex(manifestArtifact.Sha256),
        };
    }

    private async Task<string> BuildManifestAsync(
        GcodePromotionCheckpoint checkpoint,
        Artifact artifact,
        SliceJob job,
        CancellationToken cancellationToken)
    {
        CalibrationLineageContext context = await LoadCalibrationContextAsync(checkpoint, cancellationToken);
        Artifact? manifestArtifact = await FindManifestArtifactAsync(job.Id, cancellationToken);

        // Identifiers, hashes and versions only: a manifest is safe to return, log and store.
        Dictionary<string, object?> manifest = new(StringComparer.Ordinal)
        {
            ["schemaVersion"] = ManifestSchemaVersion,
            ["generator"] = GeneratorName,
            ["projectId"] = checkpoint.CalibrationProjectId,
            ["attemptId"] = checkpoint.CalibrationAttemptId,
            ["orchestrationId"] = checkpoint.CalibrationOrchestrationId,
            ["operationId"] = checkpoint.OperationId,
            ["correlationId"] = job.CorrelationId,
            ["sourceArtifactId"] = artifact.Id,
            ["sourceSliceJobId"] = job.Id,
            ["sourceWorkerId"] = artifact.WorkerId,
            ["contentSha256"] = checkpoint.SourceContentSha256,
            ["contentSizeBytes"] = checkpoint.SourceSizeBytes,
            ["specificationSha256"] = context.SpecificationSha256,
            ["modelSha256"] = job.ModelSha256,
            ["machineProfileSha256"] = job.MachineProfileSha256 ?? context.MachineProfileSha256,
            ["processProfileSha256"] = job.ProcessProfileSha256 ?? context.ProcessProfileSha256,
            ["filamentProfileSha256"] = job.FilamentProfileSha256 ?? context.FilamentProfileSha256,
            ["slicerEngine"] = job.SlicerEngineName,
            ["slicerDistribution"] = job.SlicerDistribution,
            ["slicerVersion"] = job.SlicerVersion,
            ["slicerContainerDigest"] = job.SlicerContainerDigest ?? context.SlicerContainerDigest,
            ["firmwareFamily"] = context.FirmwareFamily,
            ["gcodeDialect"] = context.GcodeDialect,
            ["calibrationManifestArtifactId"] = manifestArtifact?.Id,
            ["calibrationManifestSha256"] = manifestArtifact is null ? null : NormalizeHex(manifestArtifact.Sha256),
        };
        return JsonSerializer.Serialize(manifest, ManifestJsonOptions);
    }

    private async Task<Artifact?> FindManifestArtifactAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (_artifactsRepository is null)
        {
            return null;
        }

        IReadOnlyList<Artifact> jobArtifacts = await _artifactsRepository.GetByJobIdAsync(jobId, cancellationToken);
        return jobArtifacts.FirstOrDefault(candidate => string.Equals(
            candidate.Kind,
            SlicerArtifactKinds.CalibrationManifest,
            StringComparison.OrdinalIgnoreCase));
    }

    private async Task<CalibrationLineageContext> LoadCalibrationContextAsync(
        GcodePromotionCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        if (checkpoint.CalibrationAttemptId is null)
        {
            return CalibrationLineageContext.Empty;
        }

        CalibrationAttempt? attempt = await _dbContext.CalibrationAttempts
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == checkpoint.CalibrationAttemptId.Value, cancellationToken);
        if (attempt is null)
        {
            return CalibrationLineageContext.Empty;
        }

        // Path D (#1980): PrinterConfigurationSnapshot was deleted, and every attempt created
        // after Path D (#1981) already has a null snapshot reference, so this lookup always
        // returned an empty snapshot in practice. Reproduce that behavior directly.
        return new CalibrationLineageContext(
            attempt.SpecificationSha256,
            null,
            null,
            null,
            null,
            null,
            null);
    }

    private static string ComputeRequestSha256(GcodeArtifactPromotionRequest request, string contentSha256)
    {
        // A canonical, ordered projection of only the immutable request fields: two calls agree exactly
        // or they are a payload mismatch.
        string canonical = string.Join(
            '\n',
            $"operationId={request.OperationId.Trim()}",
            $"sourceArtifactId={request.SourceArtifactId:D}",
            $"sourceSliceJobId={request.SourceSliceJobId:D}",
            $"sourceWorkerId={request.SourceWorkerId?.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty}",
            $"artifactKind={request.ArtifactKind.ToLowerInvariant()}",
            $"contentSha256={contentSha256}",
            $"expectedSha256={NormalizeHex(request.ExpectedSha256)}",
            $"expectedSizeBytes={request.ExpectedSizeBytes.ToString(CultureInfo.InvariantCulture)}",
            $"projectId={request.CalibrationProjectId?.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty}",
            $"attemptId={request.CalibrationAttemptId?.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty}",
            $"orchestrationId={request.CalibrationOrchestrationId?.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty}",
            $"virtualDirectory={request.VirtualDirectory?.Trim() ?? string.Empty}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private bool IsLibraryStorageWritable()
    {
        try
        {
            string root = _storagePaths.GetGcodeStorageDirectory();
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            _ = Directory.CreateDirectory(root);
            return true;
        }
        catch (IOException exception)
        {
            _logger.LogWarning(
                "G-code library storage is not writable ({ExceptionType})",
                exception.GetType().Name);
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(
                "G-code library storage is not writable ({ExceptionType})",
                exception.GetType().Name);
            return false;
        }
    }

    private async Task<bool> IsCheckpointStoreAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await _dbContext.GcodePromotionCheckpoints
                .AsNoTracking()
                .Select(checkpoint => checkpoint.Id)
                .FirstOrDefaultAsync(cancellationToken);
            return true;
        }
        catch (System.Data.Common.DbException exception)
        {
            _logger.LogWarning(
                "Promotion checkpoint store is unavailable ({ExceptionType})",
                exception.GetType().Name);
            return false;
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogWarning(
                "Promotion checkpoint store is unavailable ({ExceptionType})",
                exception.GetType().Name);
            return false;
        }
    }

    private sealed record CalibrationLineageContext(
        string? SpecificationSha256,
        string? FirmwareFamily,
        string? GcodeDialect,
        string? MachineProfileSha256,
        string? ProcessProfileSha256,
        string? FilamentProfileSha256,
        string? SlicerContainerDigest)
    {
        public static CalibrationLineageContext Empty { get; } =
            new(null, null, null, null, null, null, null);
    }
}
