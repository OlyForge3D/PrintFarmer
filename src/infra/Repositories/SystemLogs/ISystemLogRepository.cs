using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.SystemLogs
{
    public interface ISystemLogRepository
    {
        IAsyncEnumerable<SystemLog> QueryAsync(string? correlationId, string? level, DateTime? from, DateTime? to, string? metadata);
        Task<IReadOnlyList<SystemLog>> QueryAllAsync(string? correlationId, string? level, DateTime? from, DateTime? to, string? metadata, CancellationToken ct);
        Task AddAsync(SystemLog log, CancellationToken ct);
    }
}
