using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Queue;

/// <summary>
/// EF Core implementation of IPrintJobStatisticsRepository
/// Provides LINQ-to-SQL queries for job statistics and prediction data
/// </summary>
public class EfPrintJobStatisticsRepository(AppDbContext context) : IPrintJobStatisticsRepository
{
    public async Task AddAsync(PrintJobStatistics statistics, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        await context.PrintJobStatistics.AddAsync(statistics, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(PrintJobStatistics statistics, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        context.PrintJobStatistics.Update(statistics);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<PrintJobStatistics?> GetByJobIdAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        return await context.PrintJobStatistics
            .AsNoTracking()
            .Include(s => s.PrinterModel)
            .FirstOrDefaultAsync(s => s.PrintJobId == jobId, cancellationToken);
    }

    public async Task<List<PrintJobStatistics>> GetByModelAndMaterialAsync(
        Guid? modelId,
        string? material,
        bool successfulOnly = true,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        IQueryable<PrintJobStatistics> query = context.PrintJobStatistics
            .AsNoTracking()
            .AsQueryable();

        if (modelId.HasValue)
        {
            query = query.Where(s => s.PrinterModelId == modelId);
        }

        if (!string.IsNullOrWhiteSpace(material))
        {
            query = query.Where(s => s.Material == material);
        }

        if (successfulOnly)
        {
            query = query.Where(s => s.IsSuccess);
        }

        return await query
            .OrderByDescending(s => s.CompletedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<PrintJobStatistics>> GetSuccessfulJobsAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        IQueryable<PrintJobStatistics> query = context.PrintJobStatistics
            .AsNoTracking()
            .Where(s => s.IsSuccess)
            .AsQueryable();

        if (fromDate.HasValue)
        {
            query = query.Where(s => s.CompletedAtUtc >= fromDate);
        }

        if (toDate.HasValue)
        {
            query = query.Where(s => s.CompletedAtUtc <= toDate);
        }

        return await query
            .OrderByDescending(s => s.CompletedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<PrintJobStatistics>> GetByPrinterModelAsync(
        Guid modelId,
        bool successfulOnly = true,
        DateTime? fromDate = null,
        CancellationToken cancellationToken = default)
    {
        IQueryable<PrintJobStatistics> query = context.PrintJobStatistics
            .AsNoTracking()
            .Where(s => s.PrinterModelId == modelId)
            .AsQueryable();

        if (successfulOnly)
        {
            query = query.Where(s => s.IsSuccess);
        }

        if (fromDate.HasValue)
        {
            query = query.Where(s => s.CompletedAtUtc >= fromDate);
        }

        return await query
            .OrderByDescending(s => s.CompletedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<PrintJobStatistics>> GetByMaterialAsync(
        string material,
        bool successfulOnly = true,
        DateTime? fromDate = null,
        CancellationToken cancellationToken = default)
    {
        IQueryable<PrintJobStatistics> query = context.PrintJobStatistics
            .AsNoTracking()
            .Where(s => s.Material == material)
            .AsQueryable();

        if (successfulOnly)
        {
            query = query.Where(s => s.IsSuccess);
        }

        if (fromDate.HasValue)
        {
            query = query.Where(s => s.CompletedAtUtc >= fromDate);
        }

        return await query
            .OrderByDescending(s => s.CompletedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<PrintJobStatistics>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await context.PrintJobStatistics
            .AsNoTracking()
            .OrderByDescending(s => s.CompletedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> CountAsync(
        Guid? modelId = null,
        string? material = null,
        bool? successOnly = null,
        DateTime? fromDate = null,
        CancellationToken cancellationToken = default)
    {
        IQueryable<PrintJobStatistics> query = context.PrintJobStatistics.AsQueryable();

        if (modelId.HasValue)
        {
            query = query.Where(s => s.PrinterModelId == modelId);
        }

        if (!string.IsNullOrWhiteSpace(material))
        {
            query = query.Where(s => s.Material == material);
        }

        if (successOnly.HasValue)
        {
            query = query.Where(s => s.IsSuccess == successOnly.Value);
        }

        if (fromDate.HasValue)
        {
            query = query.Where(s => s.CompletedAtUtc >= fromDate);
        }

        return await query.CountAsync(cancellationToken);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await context.SaveChangesAsync(cancellationToken);
    }
}
