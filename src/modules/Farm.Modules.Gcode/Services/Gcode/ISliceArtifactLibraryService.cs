using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Modules.Calibration.Services.Calibration;
using Farm.Modules.Calibration.Services.Gcode;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Farm.Modules.Gcode.Services.Gcode;

/// <summary>Public result of explicitly saving a staged slice artifact to the durable library.</summary>
public sealed record SliceArtifactLibraryResult
{
    /// <summary>Durable farm-wide G-code file identifier.</summary>
    public required Guid GcodeFileId { get; init; }

    /// <summary>Durable file display name.</summary>
    public required string Name { get; init; }

    /// <summary>Durable content size.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Whether this action created the durable file rather than replaying it.</summary>
    public required bool CreatedNew { get; init; }

    /// <summary>Whether the durable file exists and can be submitted to print workflows.</summary>
    public required bool Printable { get; init; }

    /// <summary>Slice job that produced the staged artifact.</summary>
    public required Guid SliceJobId { get; init; }

    /// <summary>Staged source artifact identifier.</summary>
    public required Guid SourceArtifactId { get; init; }
}

/// <summary>Explicitly commits staged slice output to the farm-wide G-code library.</summary>
public interface ISliceArtifactLibraryService
{
    /// <summary>Promotes the selected G-code artifact, or the job's G-code artifact when omitted.</summary>
    /// <param name="sliceJobId">Completed slice job identifier.</param>
    /// <param name="artifactId">Optional asserted artifact identifier.</param>
    /// <param name="actor">Authenticated actor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The durable library identity and replay status.</returns>
    Task<CalibrationApiResult<SliceArtifactLibraryResult>> PromoteAsync(
        Guid sliceJobId,
        Guid? artifactId,
        CalibrationActor actor,
        CancellationToken cancellationToken);
}

/// <summary>
/// Builds the one canonical promotion request shared by Save, queue Print, and direct Print.
/// </summary>
public sealed class SliceArtifactLibraryService(
    AppDbContext dbContext,
    IGcodeArtifactPromoter promoter,
    IArtifactsRepository? artifactsRepository = null,
    ISliceJobRepository? sliceJobs = null) : ISliceArtifactLibraryService
{
    private static readonly TimeSpan ConflictRetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly AppDbContext _dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly IGcodeArtifactPromoter _promoter =
        promoter ?? throw new ArgumentNullException(nameof(promoter));

    private readonly IArtifactsRepository? _artifactsRepository = artifactsRepository;

    private readonly ISliceJobRepository? _sliceJobs = sliceJobs;

    /// <inheritdoc />
    public async Task<CalibrationApiResult<SliceArtifactLibraryResult>> PromoteAsync(
        Guid sliceJobId,
        Guid? artifactId,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (sliceJobId == Guid.Empty || artifactId == Guid.Empty)
        {
            return Failure(StatusCodes.Status400BadRequest, "invalid_promotion_source");
        }

        CalibrationApiResult<SliceArtifactLibraryResult>? completed =
            await ResolveCompletedPromotionAsync(sliceJobId, artifactId, actor, cancellationToken);
        if (completed is not null)
        {
            return completed;
        }

        if (_artifactsRepository is null || _sliceJobs is null)
        {
            return Failure(StatusCodes.Status503ServiceUnavailable, "promotion_dependency_unavailable");
        }

        SliceJob? job = await _sliceJobs.GetByIdAsync(sliceJobId, cancellationToken);
        if (job is null)
        {
            return Failure(StatusCodes.Status404NotFound, "slice_job_not_found");
        }

        if (!actor.IsFarmAdmin && job.UserId != actor.UserId)
        {
            return Failure(StatusCodes.Status403Forbidden, "resource_forbidden");
        }

        Artifact? artifact;
        if (artifactId.HasValue)
        {
            artifact = await _artifactsRepository.GetByIdAsync(artifactId.Value, cancellationToken);
        }
        else
        {
            IReadOnlyList<Artifact> artifacts =
                await _artifactsRepository.GetByJobIdAsync(sliceJobId, cancellationToken);
            List<Artifact> gcodeArtifacts = artifacts
                .Where(IsGcodeArtifact)
                .Take(2)
                .ToList();
            if (gcodeArtifacts.Count > 1)
            {
                return Failure(StatusCodes.Status409Conflict, "source_artifact_required");
            }

            artifact = gcodeArtifacts.SingleOrDefault();
        }

        if (artifact is null)
        {
            return Failure(StatusCodes.Status404NotFound, "source_artifact_not_found");
        }

        string operationId = BuildOperationId(job.Id, artifact.Id);
        var request = new GcodeArtifactPromotionRequest
        {
            OperationId = operationId,
            SourceArtifactId = artifact.Id,
            SourceSliceJobId = job.Id,
            SourceWorkerId = artifact.WorkerId,
            ExpectedSha256 = artifact.Sha256,
            ExpectedSizeBytes = artifact.SizeBytes,
            ArtifactKind = artifact.Kind,
            CalibrationProjectId = job.CalibrationProjectId,
            CalibrationAttemptId = job.CalibrationAttemptId,
            CalibrationOrchestrationId = job.CalibrationOrchestrationId,
        };

        CalibrationApiResult<GcodePromotionDto> result =
            await _promoter.PromoteAsync(request, actor, cancellationToken);
        if (IsRetryableConflict(result))
        {
            await Task.Delay(ConflictRetryDelay, cancellationToken);
            result = await _promoter.PromoteAsync(request, actor, cancellationToken);
        }

        if (IsRetryableConflict(result))
        {
            result = await _promoter.GetPromotionByArtifactAsync(
                artifact.Id,
                actor,
                cancellationToken);
        }

        if (!result.IsSuccess || result.Value is null)
        {
            return Failure(result.StatusCode, result.Code ?? "promotion_operation_failed");
        }

        bool createdNew =
            result.StatusCode == StatusCodes.Status201Created &&
            !result.Replayed;

        GcodeFile? file = await _dbContext.GcodeFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == result.Value.GcodeFileId, cancellationToken);
        if (file is null)
        {
            return Failure(StatusCodes.Status503ServiceUnavailable, "promoted_gcode_unavailable");
        }

        return CalibrationApiResult<SliceArtifactLibraryResult>.Success(
            new SliceArtifactLibraryResult
            {
                GcodeFileId = file.Id,
                Name = file.Name,
                SizeBytes = file.FileSizeBytes,
                CreatedNew = createdNew,
                Printable = true,
                SliceJobId = job.Id,
                SourceArtifactId = artifact.Id,
            },
            createdNew ? StatusCodes.Status201Created : StatusCodes.Status200OK,
            replayed: !createdNew);
    }

    /// <summary>Builds the server-owned deterministic operation identity for one slice artifact.</summary>
    /// <param name="jobId">Slice job identifier.</param>
    /// <param name="artifactId">Artifact identifier.</param>
    /// <returns>The stable operation identity.</returns>
    public static string BuildOperationId(Guid jobId, Guid artifactId) =>
        $"slice-library:{jobId:N}:{artifactId:N}";

    private static bool IsRetryableConflict(CalibrationApiResult<GcodePromotionDto> result) =>
        !result.IsSuccess &&
        result.StatusCode == StatusCodes.Status409Conflict &&
        result.Code is "promotion_conflict" or "promotion_in_progress";

    private static CalibrationApiResult<SliceArtifactLibraryResult> Failure(int statusCode, string code) =>
        CalibrationApiResult<SliceArtifactLibraryResult>.Failure(statusCode, code);

    private async Task<CalibrationApiResult<SliceArtifactLibraryResult>?> ResolveCompletedPromotionAsync(
        Guid sliceJobId,
        Guid? artifactId,
        CalibrationActor actor,
        CancellationToken cancellationToken)
    {
        IQueryable<GcodePromotionCheckpoint> query = _dbContext.GcodePromotionCheckpoints
            .AsNoTracking()
            .Where(checkpoint =>
                checkpoint.State == GcodePromotionState.Completed &&
                checkpoint.SourceSliceJobId == sliceJobId);

        if (artifactId.HasValue)
        {
            string operationId = BuildOperationId(sliceJobId, artifactId.Value);
            query = query.Where(checkpoint =>
                checkpoint.SourceArtifactId == artifactId.Value &&
                checkpoint.OperationId == operationId);
        }
        else
        {
            string operationPrefix = $"slice-library:{sliceJobId:N}:";
            query = query.Where(checkpoint => checkpoint.OperationId.StartsWith(operationPrefix));
        }

        List<GcodePromotionCheckpoint> checkpoints = await query
            .OrderBy(checkpoint => checkpoint.CompletedAtUtc)
            .Take(2)
            .ToListAsync(cancellationToken);
        if (checkpoints.Count == 0)
        {
            return null;
        }

        GcodePromotionCheckpoint checkpoint = checkpoints[0];
        if (!actor.IsFarmAdmin && checkpoint.OwnerUserId != actor.UserId)
        {
            return Failure(StatusCodes.Status403Forbidden, "resource_forbidden");
        }

        if (!artifactId.HasValue && checkpoints.Count > 1)
        {
            return Failure(StatusCodes.Status409Conflict, "source_artifact_required");
        }

        if (!artifactId.HasValue && _artifactsRepository is not null)
        {
            IReadOnlyList<Artifact> artifacts =
                await _artifactsRepository.GetByJobIdAsync(sliceJobId, cancellationToken);
            if (artifacts.Any(candidate =>
                    IsGcodeArtifact(candidate) &&
                    candidate.Id != checkpoint.SourceArtifactId))
            {
                return Failure(StatusCodes.Status409Conflict, "source_artifact_required");
            }
        }

        GcodeFile? file = await _dbContext.GcodeFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == checkpoint.GcodeFileId, cancellationToken);
        if (file is null)
        {
            return Failure(StatusCodes.Status503ServiceUnavailable, "promoted_gcode_unavailable");
        }

        return CalibrationApiResult<SliceArtifactLibraryResult>.Success(
            new SliceArtifactLibraryResult
            {
                GcodeFileId = file.Id,
                Name = file.Name,
                SizeBytes = file.FileSizeBytes,
                CreatedNew = false,
                Printable = true,
                SliceJobId = checkpoint.SourceSliceJobId,
                SourceArtifactId = checkpoint.SourceArtifactId,
            },
            StatusCodes.Status200OK,
            replayed: true);
    }

    private static bool IsGcodeArtifact(Artifact artifact) =>
        string.Equals(artifact.Kind, SlicerArtifactKinds.Gcode, StringComparison.OrdinalIgnoreCase);
}
