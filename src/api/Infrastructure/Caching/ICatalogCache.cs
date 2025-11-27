using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Infrastructure.Caching;

/// <summary>
/// Abstraction for retrieving and invalidating cached catalog (manufacturer/model) data with ETag support.
/// </summary>
public interface ICatalogCache
{
    Task<(IReadOnlyList<ManufacturerDto> list, string etag)> GetManufacturersAsync(CancellationToken ct);
    Task<(IReadOnlyList<PrinterModelDto> list, string etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct);
    void InvalidateManufacturers();
    void InvalidateModels(Guid? manufacturerId = null);
}

public sealed class CatalogCacheOptions
{
    /// <summary>TTL for manufacturer and model list cache entries. Default 2 minutes.</summary>
    public TimeSpan ListTtl { get; set; } = TimeSpan.FromMinutes(2);
}

internal sealed class CatalogCache(IMemoryCache cache, Microsoft.Extensions.Options.IOptions<CatalogCacheOptions> options, IServiceProvider services) : ICatalogCache
{
    private readonly IMemoryCache _cache = cache;
    private readonly CatalogCacheOptions _options = options.Value;
    private readonly IServiceProvider _services = services;
    private Microsoft.EntityFrameworkCore.IDbContextFactory<AppDbContext>? _dbFactory;

    private const string ManufacturersKey = "catalog:mfglst";
    private const string ModelsAllKey = "catalog:models:all";
    private static string ModelsKey(Guid id) => $"catalog:models:{id}";

    public async Task<(IReadOnlyList<ManufacturerDto> list, string etag)> GetManufacturersAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue<(IReadOnlyList<ManufacturerDto> list, string etag)>(ManufacturersKey, out (IReadOnlyList<ManufacturerDto> list, string etag) cached))
        {
            return cached;
        }

        // Resolve the IDbContextFactory lazily from the service provider. Some test
        // scenarios register or mutate DbContextFactory registration at test-host
        // build time; resolving lazily avoids forcing the factory to exist during
        // singleton validation/build-time checks.
        IDbContextFactory<AppDbContext> dbFactory = _dbFactory ??= _services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<AppDbContext>>();
        await using AppDbContext db = dbFactory.CreateDbContext();
        List<ManufacturerDto> list = await db.Manufacturers.AsNoTracking().OrderBy(m => m.Name)
            .Select(m => new ManufacturerDto(m.Id, m.Name)).ToListAsync(ct);
        string etag = ComputeWeakEtag(list.Select(m => m.Id.ToString("N") + ":" + m.Name));
        _ = _cache.Set(ManufacturersKey, (list, etag), _options.ListTtl);
        return (list, etag);
    }

    public async Task<(IReadOnlyList<PrinterModelDto> list, string etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct)
    {
        string key = manufacturerId is Guid mid ? ModelsKey(mid) : ModelsAllKey;
        if (_cache.TryGetValue<(IReadOnlyList<PrinterModelDto> list, string etag)>(key, out (IReadOnlyList<PrinterModelDto> list, string etag) cached))
        {
            return cached;
        }

        IDbContextFactory<AppDbContext> dbFactory2 = _dbFactory ??= _services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<AppDbContext>>();
        await using AppDbContext db = dbFactory2.CreateDbContext();

        IQueryable<PrinterModel> q = db.Models.AsNoTracking().Include(m => m.SupportedFilamentTypes).ThenInclude(sf => sf.FilamentType).AsQueryable();
        if (manufacturerId is Guid mid2)
        {
            q = q.Where(m => m.ManufacturerId == mid2);
        }
        List<PrinterModelDto> list = await q.OrderBy(m => m.Name)
            .Select(m => new PrinterModelDto(
                m.Id,
                m.Name,
                m.ManufacturerId,
                m.MotionType.HasValue ? (MotionType)m.MotionType.Value : (MotionType?)null,
                m.MaxX,
                m.MaxY,
                m.MaxZ,
                m.DefaultBackend.HasValue ? (PrinterBackend)m.DefaultBackend.Value : (PrinterBackend?)null,
                m.SupportedFilamentTypes.Select(sf => sf.FilamentType!.Name).ToArray(),
                // Default capabilities
                m.DefaultNozzleDiameter,
                m.HasHeatedBed,
                m.HasEnclosure,
                m.MultiMaterial,
                m.NumberOfExtruders,
                m.SupportsAutoLeveling,
                // Temperature ranges
                m.MinHotendTemp,
                m.MaxHotendTemp,
                m.MinBedTemp,
                m.MaxBedTemp,
                // Speed capabilities
                m.MaxPrintSpeed)).ToListAsync(ct);
        IEnumerable<string> etagInput = list.Select(m => m.Id.ToString("N") + ":" + m.Name).Prepend(manufacturerId?.ToString("N") ?? "all");
        string etag = ComputeWeakEtag(etagInput);
        _ = _cache.Set(key, (list, etag), _options.ListTtl);
        return (list, etag);
    }

    public void InvalidateManufacturers()
    {
        _cache.Remove(ManufacturersKey);
        // models depend on manufacturer names indirectly (rare) but keep simple
    }

    public void InvalidateModels(Guid? manufacturerId = null)
    {
        if (manufacturerId is Guid mid)
        {
            _cache.Remove(ModelsKey(mid));
        }
        else
        {
            _cache.Remove(ModelsAllKey);
        }
    }

    private static string ComputeWeakEtag(IEnumerable<string> parts)
    {
        string joined = string.Join('|', parts);
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        string hash = Convert.ToHexString(bytes, 0, 8);
        return $"W/\"{hash}\"";
    }
}
