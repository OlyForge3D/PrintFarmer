using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Attention;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.FailureDetection;

/// <summary>
/// Persists and queries recent failure-detection incidents.
/// </summary>
public interface IFailureDetectionIncidentHistoryService
{
    /// <summary>
    /// Persists a failure-detection incident for later operator review.
    /// </summary>
    Task<FailureDetectionIncident> RecordFailureAsync(
        Guid printerId,
        Guid? jobId,
        string? jobName,
        string? fileName,
        decimal confidence,
        DateTime detectedAt,
        string? snapshotUrl,
        bool autoPaused,
        CancellationToken ct = default);

    /// <summary>
    /// Returns recent persisted incidents, optionally filtered to a single printer.
    /// </summary>
    Task<List<FailureDetectionDto>> GetRecentAsync(Guid? printerId, int take, CancellationToken ct = default);

    /// <summary>
    /// Marks a failure incident as resolved by a successful operator attention action so it
    /// is suppressed from the unified attention feed on the next refetch (issue #707, R2).
    /// The incident row is preserved for history/audit. Callers MUST invoke this only after
    /// the authoritative printer/job mutation has succeeded and been committed.
    /// </summary>
    /// <param name="incidentId">The incident identifier.</param>
    /// <param name="resolvedAtUtc">The UTC instant the resolving action succeeded.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> when a still-open incident was transitioned to resolved; <c>false</c> when
    /// the incident does not exist or was already resolved (idempotent no-op).
    /// </returns>
    Task<bool> MarkResolvedAsync(Guid incidentId, DateTime resolvedAtUtc, CancellationToken ct = default);
}

/// <summary>
/// Persists and queries recent failure-detection incidents.
/// </summary>
public sealed class FailureDetectionIncidentHistoryService : IFailureDetectionIncidentHistoryService
{
    public const int DefaultTake = 50;
    public const int MaxTake = 200;

    private readonly AppDbContext _dbContext;

    // Attention feed invalidation (issue #707). Optional to preserve existing test constructors.
    private readonly IAttentionBroadcaster? _attentionBroadcaster;

    /// <summary>
    /// Creates the service.
    /// </summary>
    /// <param name="dbContext">The application database context.</param>
    /// <param name="attentionBroadcaster">Optional broadcaster for attention-feed invalidation.</param>
    public FailureDetectionIncidentHistoryService(AppDbContext dbContext, IAttentionBroadcaster? attentionBroadcaster = null)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
        _attentionBroadcaster = attentionBroadcaster;
    }

    /// <summary>
    /// Persists a failure-detection incident for later operator review.
    /// </summary>
    /// <param name="printerId">The printer identifier.</param>
    /// <param name="jobId">The active job identifier, if known.</param>
    /// <param name="jobName">The active job display name, if known.</param>
    /// <param name="fileName">The active file name, if known.</param>
    /// <param name="confidence">The model confidence that triggered the incident.</param>
    /// <param name="detectedAt">When the incident was detected.</param>
    /// <param name="snapshotUrl">The snapshot URL used for analysis, if known.</param>
    /// <param name="autoPaused">Whether the active print was auto-paused.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<FailureDetectionIncident> RecordFailureAsync(
        Guid printerId,
        Guid? jobId,
        string? jobName,
        string? fileName,
        decimal confidence,
        DateTime detectedAt,
        string? snapshotUrl,
        bool autoPaused,
        CancellationToken ct = default)
    {
        FailureDetectionIncident incident = new()
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            JobId = jobId,
            JobName = jobName,
            FileName = fileName,
            Confidence = confidence,
            DetectedAt = detectedAt,
            SnapshotUrl = snapshotUrl,
            AutoPaused = autoPaused,
        };

        _ = _dbContext.FailureDetectionIncidents.Add(incident);
        _ = await _dbContext.SaveChangesAsync(ct);

        // Invalidate the unified attention feed (issue #707).
        if (_attentionBroadcaster is not null)
        {
            await _attentionBroadcaster.NotifyChangedAsync(new AttentionChangedPayload(
                AttentionIdPrefixes.Build(AttentionIdPrefixes.Failure, incident.Id),
                AttentionChangeKind.Created,
                incident.DetectedAt));
        }

        return incident;
    }

    /// <summary>
    /// Returns recent persisted incidents, optionally filtered to a single printer.
    /// </summary>
    /// <param name="printerId">Optional printer filter.</param>
    /// <param name="take">Maximum incidents to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Recent incidents ordered newest first.</returns>
    public Task<List<FailureDetectionDto>> GetRecentAsync(Guid? printerId, int take, CancellationToken ct = default)
    {
        int normalizedTake = NormalizeTake(take);
        IQueryable<FailureDetectionIncident> query = _dbContext.FailureDetectionIncidents.AsNoTracking();

        if (printerId.HasValue)
        {
            Guid printerIdValue = printerId.Value;
            query = query.Where(incident => incident.PrinterId == printerIdValue);
        }

        return query
            .OrderByDescending(incident => incident.DetectedAt)
            .Select(incident => new FailureDetectionDto
            {
                Id = incident.Id,
                PrinterId = incident.PrinterId,
                PrinterName = incident.Printer!.Name,
                JobId = incident.JobId,
                JobName = incident.JobName,
                FileName = incident.FileName,
                Confidence = incident.Confidence,
                DetectedAt = incident.DetectedAt,
                SnapshotUrl = incident.SnapshotUrl,
                AutoPaused = incident.AutoPaused,
                ResolvedAtUtc = incident.ResolvedAtUtc,
            })
            .Take(normalizedTake)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Marks a failure incident as resolved by a successful operator attention action.
    /// </summary>
    /// <param name="incidentId">The incident identifier.</param>
    /// <param name="resolvedAtUtc">The UTC instant the resolving action succeeded.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> when a still-open incident was resolved; otherwise <c>false</c>.</returns>
    public async Task<bool> MarkResolvedAsync(Guid incidentId, DateTime resolvedAtUtc, CancellationToken ct = default)
    {
        FailureDetectionIncident? incident = await _dbContext.FailureDetectionIncidents
            .FirstOrDefaultAsync(i => i.Id == incidentId, ct);
        if (incident is null || incident.ResolvedAtUtc is not null)
        {
            return false;
        }

        incident.ResolvedAtUtc = DateTime.SpecifyKind(resolvedAtUtc, DateTimeKind.Utc);
        _ = await _dbContext.SaveChangesAsync(ct);
        return true;
    }

    private static int NormalizeTake(int take)
    {
        if (take <= 0)
        {
            return DefaultTake;
        }

        return Math.Min(take, MaxTake);
    }
}
