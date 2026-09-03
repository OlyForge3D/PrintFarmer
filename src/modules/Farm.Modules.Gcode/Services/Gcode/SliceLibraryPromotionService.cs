using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.SignalR;
using Farm.Modules.Calibration.Services.Calibration;
using Farm.Modules.Calibration.Services.Gcode;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Modules.Gcode.Services.Gcode;

/// <summary>
/// Promotes completed user slice artifacts into durable G-code library storage.
/// </summary>
/// <remarks>
/// The scan is the durable source of truth for both newly completed and historical jobs. Promotion
/// remains asynchronous so worker completion acknowledgements never wait for G-code parsing or copy
/// operations, while <see cref="IGcodeArtifactPromoter"/> retains ownership of pinning, verified copy,
/// checkpoint, and replay semantics. Promoted files intentionally enter the existing farm-global
/// G-code library; owner isolation applies to the temporary source artifact and the notification,
/// not to visibility of the durable destination library.
/// </remarks>
public sealed class SliceLibraryPromotionService(
    IServiceScopeFactory scopeFactory,
    IHubContext<PrinterHub> hub,
    ILogger<SliceLibraryPromotionService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    private const int MaxCandidatesPerPass = 200;
    private const int MaxPromotionsPerPass = 50;

    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly IHubContext<PrinterHub> _hub =
        hub ?? throw new ArgumentNullException(nameof(hub));

    private readonly ILogger<SliceLibraryPromotionService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private int _capabilityUnavailableLogged;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan nextInterval = StartupDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (nextInterval > TimeSpan.Zero)
            {
                await Task.Delay(nextInterval, stoppingToken);
            }

            try
            {
                _ = await PromoteMissingAsync(stoppingToken);
                nextInterval = PollInterval;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Slice-library G-code promotion failed and will be retried.");
                nextInterval = FailureBackoff;
            }
        }
    }

    /// <summary>Runs one bounded discovery and promotion pass.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of successful new or replayed promotions.</returns>
    internal async Task<int> PromoteMissingAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IGcodeArtifactPromoter promoter = scope.ServiceProvider.GetRequiredService<IGcodeArtifactPromoter>();
        GcodePromotionCapabilityDto capability = await promoter.GetCapabilityAsync(cancellationToken);
        if (!capability.Operational)
        {
            if (Interlocked.Exchange(ref _capabilityUnavailableLogged, 1) == 0)
            {
                _logger.LogInformation(
                    "Automatic slice-library promotion is unavailable ({UnavailableCode}); " +
                    "completed artifacts will remain pending until the promotion dependencies are available.",
                    capability.UnavailableCode ?? "promotion_dependency_unavailable");
            }

            return 0;
        }

        _ = Interlocked.Exchange(ref _capabilityUnavailableLogged, 0);

        AppDbContext appDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Guid[] terminalArtifactIds = await appDb.GcodePromotionCheckpoints
            .AsNoTracking()
            .Where(checkpoint => checkpoint.State == GcodePromotionState.Failed)
            .Select(checkpoint => checkpoint.SourceArtifactId)
            .Distinct()
            .ToArrayAsync(cancellationToken);

        SlicerDbContext slicerDb = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        List<PromotionCandidate> candidates = await (
            from artifact in slicerDb.Artifacts.AsNoTracking()
            join job in slicerDb.SliceJobs.AsNoTracking() on artifact.JobId equals job.Id
            where job.Status == SliceJobStatus.Completed &&
                artifact.Kind == SlicerArtifactKinds.Gcode &&
                artifact.PromotedGcodeFileId == null &&
                !terminalArtifactIds.Contains(artifact.Id)
            orderby artifact.CreatedAt, artifact.Id
            select new PromotionCandidate(
                job.Id,
                job.UserId,
                artifact.Id,
                artifact.WorkerId,
                artifact.Sha256,
                artifact.SizeBytes))
            .Take(MaxCandidatesPerPass)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return 0;
        }

        Guid[] candidateArtifactIds = [.. candidates.Select(candidate => candidate.ArtifactId)];
        HashSet<Guid> promotedArtifactIds = await appDb.GcodeFiles
            .AsNoTracking()
            .Where(file =>
                file.SourceArtifactId.HasValue &&
                candidateArtifactIds.Contains(file.SourceArtifactId.Value))
            .Select(file => file.SourceArtifactId!.Value)
            .ToHashSetAsync(cancellationToken);

        int promotedCount = 0;
        IEnumerable<PromotionCandidate> missing = candidates
            .Where(candidate => !promotedArtifactIds.Contains(candidate.ArtifactId))
            .Take(MaxPromotionsPerPass);

        foreach (PromotionCandidate candidate in missing)
        {
            string operationId = BuildOperationId(candidate.JobId, candidate.ArtifactId);
            var request = new GcodeArtifactPromotionRequest
            {
                OperationId = operationId,
                SourceArtifactId = candidate.ArtifactId,
                SourceSliceJobId = candidate.JobId,
                SourceWorkerId = candidate.WorkerId,
                ExpectedSha256 = candidate.Sha256,
                ExpectedSizeBytes = candidate.SizeBytes,
            };
            var actor = new CalibrationActor(
                candidate.OwnerUserId,
                $"slice-library-promotion:{candidate.JobId:N}",
                IsFarmAdmin: false);

            CalibrationApiResult<GcodePromotionDto> result =
                await promoter.PromoteAsync(request, actor, cancellationToken);
            if (result.IsSuccess)
            {
                promotedCount++;
                if (!result.Replayed)
                {
                    await NotifyLibraryUpdatedAsync(candidate.OwnerUserId, cancellationToken);
                }

                continue;
            }

            _logger.LogWarning(
                "Could not promote completed slice job {SliceJobId} artifact {ArtifactId}: " +
                "{StatusCode} {FailureCode}.",
                candidate.JobId,
                candidate.ArtifactId,
                result.StatusCode,
                result.Code ?? "promotion_failed");
        }

        if (promotedCount > 0)
        {
            _logger.LogInformation(
                "Promoted {PromotionCount} completed slice artifacts into the G-code library.",
                promotedCount);
        }

        return promotedCount;
    }

    private async Task NotifyLibraryUpdatedAsync(Guid ownerUserId, CancellationToken cancellationToken)
    {
        try
        {
            await _hub.Clients.Group(AuthorizedHubGroups.User(ownerUserId))
                .SendAsync("gcodelibraryupdated", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not notify owner {OwnerUserId} that the G-code library was updated.",
                ownerUserId);
        }
    }

    /// <summary>Builds the stable idempotency key for one slice artifact.</summary>
    internal static string BuildOperationId(Guid jobId, Guid artifactId) =>
        $"slice-library:{jobId:N}:{artifactId:N}";

    private sealed record PromotionCandidate(
        Guid JobId,
        Guid OwnerUserId,
        Guid ArtifactId,
        Guid? WorkerId,
        string Sha256,
        long SizeBytes);
}
