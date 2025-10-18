using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services.SystemLogs;

public class SystemLogService : ISystemLogService
{
    private readonly Farm.Infrastructure.Repositories.SystemLogs.ISystemLogRepository _repo;

    public SystemLogService(Farm.Infrastructure.Repositories.SystemLogs.ISystemLogRepository repo)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
    }

    public async Task<IReadOnlyList<SystemLog>> QueryLogsAsync(string? correlationId, string? level, DateTime? from, DateTime? to, string? metadata, CancellationToken ct)
    {
        var list = new List<SystemLog>();
        await foreach (var item in _repo.QueryAsync(correlationId, level, from, to, metadata))
        {
            list.Add(item);
            if (list.Count >= 500)
            {
                break;
            }
        }

        return list;
    }

    public Task<IReadOnlyList<SystemLog>> QueryAllLogsAsync(string? correlationId, string? level, DateTime? from, DateTime? to, string? metadata, CancellationToken ct)
    {
        return _repo.QueryAllAsync(correlationId, level, from, to, metadata, ct);
    }
}
