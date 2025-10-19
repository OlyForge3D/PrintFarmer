using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Repositories.Slicing;

/// <summary>
/// Repository for managing SliceJob entities
/// </summary>
public interface ISliceJobRepository
{
    /// <summary>
    /// Add a new slice job to the database
    /// </summary>
    Task AddAsync(SliceJob job, CancellationToken ct = default);

    /// <summary>
    /// Get a slice job by ID
    /// </summary>
    Task<SliceJob?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Get all jobs for a specific user
    /// </summary>
    Task<IReadOnlyList<SliceJob>> GetByUserIdAsync(Guid userId, int? limit = null, int? offset = null, CancellationToken ct = default);

    /// <summary>
    /// Get jobs by status
    /// </summary>
    Task<IReadOnlyList<SliceJob>> GetByStatusAsync(string status, int? limit = null, CancellationToken ct = default);

    /// <summary>
    /// Get queued jobs ordered by priority and queue time
    /// </summary>
    Task<IReadOnlyList<SliceJob>> GetQueuedJobsAsync(int? limit = null, CancellationToken ct = default);

    /// <summary>
    /// Update job status and related fields
    /// </summary>
    Task UpdateStatusAsync(Guid jobId, string status, string? progressMessage = null, int? progressPercent = null, CancellationToken ct = default);

    /// <summary>
    /// Mark job as started by a worker
    /// </summary>
    Task MarkStartedAsync(Guid jobId, Guid workerId, CancellationToken ct = default);

    /// <summary>
    /// Mark job as completed with result
    /// </summary>
    Task MarkCompletedAsync(Guid jobId, string resultFileUrl, int? estimatedPrintTimeSeconds = null, decimal? filamentUsedGrams = null, CancellationToken ct = default);

    /// <summary>
    /// Mark job as failed with error message
    /// </summary>
    Task MarkFailedAsync(Guid jobId, string errorMessage, CancellationToken ct = default);

    /// <summary>
    /// Update job progress
    /// </summary>
    Task UpdateProgressAsync(Guid jobId, int progressPercent, string progressMessage, CancellationToken ct = default);

    /// <summary>
    /// Save changes to the database
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
