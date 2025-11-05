using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.SystemLogs
{
    public class EfSystemLogRepository : ISystemLogRepository
    {
        private readonly AppDbContext _db;

        public EfSystemLogRepository(AppDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async IAsyncEnumerable<SystemLog> QueryAsync(string? correlationId, string? level, DateTime? from, DateTime? to, string? metadata)
        {
            IQueryable<SystemLog> query = _db.SystemLogs.AsQueryable();
            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                query = query.Where(l => l.CorrelationId == correlationId);
            }

            if (!string.IsNullOrWhiteSpace(level))
            {
                query = query.Where(l => l.Level == level);
            }

            if (from.HasValue)
            {
                query = query.Where(l => l.Timestamp >= from.Value);
            }

            if (to.HasValue)
            {
                query = query.Where(l => l.Timestamp <= to.Value);
            }

            if (!string.IsNullOrWhiteSpace(metadata))
            {
                string lower = metadata.ToLower();
                query = query.Where(l => l.Metadata != null && EF.Functions.Like(l.Metadata.ToLower(), $"%{lower}%"));
            }

            await foreach (var item in query.OrderByDescending(l => l.Timestamp).AsAsyncEnumerable())
            {
                yield return item;
            }
        }

        public async Task<IReadOnlyList<SystemLog>> QueryAllAsync(string? correlationId, string? level, DateTime? from, DateTime? to, string? metadata, CancellationToken ct)
        {
            IQueryable<SystemLog> query = _db.SystemLogs.AsQueryable();
            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                query = query.Where(l => l.CorrelationId == correlationId);
            }

            if (!string.IsNullOrWhiteSpace(level))
            {
                query = query.Where(l => l.Level == level);
            }

            if (from.HasValue)
            {
                query = query.Where(l => l.Timestamp >= from.Value);
            }

            if (to.HasValue)
            {
                query = query.Where(l => l.Timestamp <= to.Value);
            }

            if (!string.IsNullOrWhiteSpace(metadata))
            {
                string lower = metadata.ToLower();
                query = query.Where(l => l.Metadata != null && EF.Functions.Like(l.Metadata.ToLower(), $"%{lower}%"));
            }

            var result = await query.OrderByDescending(l => l.Timestamp).ToListAsync(ct);
            return (IReadOnlyList<SystemLog>)result;
        }

        public Task AddAsync(SystemLog log, CancellationToken ct)
        {
            _db.SystemLogs.Add(log);
            return _db.SaveChangesAsync(ct);
        }

        public async Task<int> DeleteLogsOlderThanAsync(DateTime cutoff, CancellationToken ct)
        {
            List<SystemLog> oldLogs = await _db.SystemLogs
                .Where(l => l.Timestamp < cutoff)
                .ToListAsync(ct);

            if (oldLogs.Count > 0)
            {
                _db.SystemLogs.RemoveRange(oldLogs);
                await _db.SaveChangesAsync(ct);
            }

            return oldLogs.Count;
        }
    }
}
