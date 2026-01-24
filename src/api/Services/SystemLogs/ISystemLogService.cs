using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Services.SystemLogs;

/// <summary>
/// Service for querying application system logs.
/// </summary>
public interface ISystemLogService
{
    /// <summary>Queries logs with optional filtering, returning recent entries.</summary>
    Task<IReadOnlyList<SystemLog>> QueryLogsAsync(string? correlationId, string? level, DateTime? from, DateTime? to, string? metadata, CancellationToken ct);

    /// <summary>Queries all logs without row limits (admin-only).</summary>
    Task<IReadOnlyList<SystemLog>> QueryAllLogsAsync(string? correlationId, string? level, DateTime? from, DateTime? to, string? metadata, CancellationToken ct);
}
