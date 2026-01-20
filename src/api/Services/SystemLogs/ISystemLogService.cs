using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Services.SystemLogs;

public interface ISystemLogService
{
    Task<IReadOnlyList<SystemLog>> QueryLogsAsync(string? correlationId, string? level, DateTime? from, DateTime? to, string? metadata, CancellationToken ct);

    Task<IReadOnlyList<SystemLog>> QueryAllLogsAsync(string? correlationId, string? level, DateTime? from, DateTime? to, string? metadata, CancellationToken ct);
}
