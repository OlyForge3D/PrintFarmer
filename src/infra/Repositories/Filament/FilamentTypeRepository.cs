using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Filament;

public class FilamentTypeRepository(AppDbContext db) : IFilamentTypeRepository
{
    private readonly AppDbContext _db = db;

    public async Task<IReadOnlyList<FilamentTypeDto>> GetFilamentTypesAsync(CancellationToken ct = default)
    {
        return await _db.FilamentTypes.AsNoTracking()
            .Select(f => new FilamentTypeDto(f.Id, f.Name, new TempTargets(f.DefaultHotendTemp, f.DefaultBedTemp), f.IsAbrasive, f.NeedsEnclosure, f.DefaultPricePerKg, f.DefaultDensity))
            .ToListAsync(ct);
    }

    public async Task<PagedResult<FilamentTypeDto>> GetPagedAsync(int page, int pageSize, string? search = null, CancellationToken ct = default)
    {
        IQueryable<FilamentType> query = _db.FilamentTypes.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            string term = search.Trim().ToUpperInvariant();
            query = query.Where(f => EF.Functions.Like(f.Name.ToUpper(), $"%{term}%"));
        }

        query = query.OrderBy(f => f.Name);

        int totalCount = await query.CountAsync(ct);
        int totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        List<FilamentTypeDto> items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(f => new FilamentTypeDto(f.Id, f.Name, new TempTargets(f.DefaultHotendTemp, f.DefaultBedTemp), f.IsAbrasive, f.NeedsEnclosure, f.DefaultPricePerKg, f.DefaultDensity))
            .ToListAsync(ct);

        return new PagedResult<FilamentTypeDto>(items, totalCount, page, pageSize, totalPages);
    }

    public async Task<FilamentPresetsDto> GetFilamentPresetsAsync(CancellationToken ct = default)
    {
        var items = await _db.FilamentTypes.AsNoTracking().Select(f => new { f.Name, f.DefaultHotendTemp, f.DefaultBedTemp }).ToListAsync(ct);
        Dictionary<string, TempTargets> dict = items.ToDictionary(i => i.Name, i => new TempTargets(i.DefaultHotendTemp, i.DefaultBedTemp));
        return new FilamentPresetsDto(dict);
    }

    public Task AddFilamentTypeAsync(FilamentType ft, CancellationToken ct = default)
    {
        _ = _db.FilamentTypes.Add(ft);
        return Task.CompletedTask;
    }

    public async Task<FilamentTypeDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        FilamentType? f = await _db.FilamentTypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return f is null ? null : new FilamentTypeDto(f.Id, f.Name, new TempTargets(f.DefaultHotendTemp, f.DefaultBedTemp), f.IsAbrasive, f.NeedsEnclosure, f.DefaultPricePerKg, f.DefaultDensity);
    }

    public Task UpdateFilamentTypeAsync(FilamentType ft, CancellationToken ct = default)
    {
        _ = _db.FilamentTypes.Update(ft);
        return Task.CompletedTask;
    }

    public Task DeleteFilamentTypeAsync(Guid id, CancellationToken ct = default)
    {
        _ = _db.FilamentTypes.Remove(new FilamentType { Id = id });
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        return _db.SaveChangesAsync(ct);
    }

    public Task<FilamentType?> GetEntityByIdAsync(Guid id, CancellationToken ct = default)
    {
        return _db.FilamentTypes.FirstOrDefaultAsync(f => f.Id == id, ct);
    }

    public Task<FilamentType?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        return _db.FilamentTypes.FirstOrDefaultAsync(f => f.Name == name, ct);
    }
}
