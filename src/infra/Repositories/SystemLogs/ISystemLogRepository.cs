using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.SystemLogs;

/// <summary>
/// Repository for managing system log entries with filtering and retention.
/// </summary>
public interface ISystemLogRepository
{
    /// <summary>Queries logs as an async stream with optional filters.</summary>
    /// <param name="correlationId">Optional correlation ID to filter by.</param>
    /// <param name="level">Optional log level to filter by.</param>
    /// <param name="from">Optional start date for filtering.</param>
    /// <param name="to">Optional end date for filtering.</param>
    /// <param name="metadata">Optional metadata filter.</param>
    IAsyncEnumerable<SystemLog> QueryAsync(string? correlationId, string? level, DateTime? from, DateTime? to, string? metadata);

    /// <summary>Queries all logs matching filters and returns as a list.</summary>
    /// <param name="correlationId">Optional correlation ID to filter by.</param>
    /// <param name="level">Optional log level to filter by.</param>
    /// <param name="from">Optional start date for filtering.</param>
    /// <param name="to">Optional end date for filtering.</param>
    /// <param name="metadata">Optional metadata filter.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<SystemLog>> QueryAllAsync(string? correlationId, string? level, DateTime? from, DateTime? to, string? metadata, CancellationToken ct);

    /// <summary>Adds a new system log entry.</summary>
    /// <param name="log">The log entry to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddAsync(SystemLog log, CancellationToken ct);

    /// <summary>Deletes log entries older than the specified cutoff date.</summary>
    /// <param name="cutoff">The cutoff date for deletion.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of logs deleted.</returns>
    Task<int> DeleteLogsOlderThanAsync(DateTime cutoff, CancellationToken ct);
}
