using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
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
}

/// <summary>
/// Persists and queries recent failure-detection incidents.
/// </summary>
public sealed class FailureDetectionIncidentHistoryService : IFailureDetectionIncidentHistoryService
{
    public const int DefaultTake = 50;
    public const int MaxTake = 200;

    private readonly AppDbContext _dbContext;

    /// <summary>
    /// Creates the service.
    /// </summary>
    /// <param name="dbContext">The application database context.</param>
    public FailureDetectionIncidentHistoryService(AppDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
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
            })
            .Take(normalizedTake)
            .ToListAsync(ct);
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
