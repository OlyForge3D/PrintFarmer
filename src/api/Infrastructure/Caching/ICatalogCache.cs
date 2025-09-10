using Farm.Web.Shared;
using Farm.Web.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;
using System.Text;

namespace Farm.Web.Api.Infrastructure.Caching;

/// <summary>
/// Abstraction for retrieving and invalidating cached catalog (manufacturer/model) data with ETag support.
/// </summary>
public interface ICatalogCache
{
    Task<(IReadOnlyList<ManufacturerDto> list, string etag)> GetManufacturersAsync(CancellationToken ct);
    Task<(IReadOnlyList<ModelDto> list, string etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct);
    void InvalidateManufacturers();
    void InvalidateModels(Guid? manufacturerId = null);
}

public sealed class CatalogCacheOptions
{
    /// <summary>TTL for manufacturer and model list cache entries. Default 2 minutes.</summary>
    public TimeSpan ListTtl { get; set; } = TimeSpan.FromMinutes(2);
}

internal sealed class CatalogCache : ICatalogCache
{
    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly CatalogCacheOptions _options;

    private const string ManufacturersKey = "catalog:mfglst";
    private const string ModelsAllKey = "catalog:models:all";
    private static string ModelsKey(Guid id) => $"catalog:models:{id}";

    public CatalogCache(AppDbContext db, IMemoryCache cache, Microsoft.Extensions.Options.IOptions<CatalogCacheOptions> options)
    {
        _db = db;
        _cache = cache;
        _options = options.Value;
    }

    public async Task<(IReadOnlyList<ManufacturerDto> list, string etag)> GetManufacturersAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue<(IReadOnlyList<ManufacturerDto> list, string etag)>(ManufacturersKey, out var cached))
        {
            return cached;
        }

        var list = await _db.Manufacturers.AsNoTracking().OrderBy(m => m.Name)
            .Select(m => new ManufacturerDto(m.Id, m.Name)).ToListAsync(ct);
        var etag = ComputeWeakEtag(list.Select(m => m.Id.ToString("N") + ":" + m.Name));
        _cache.Set(ManufacturersKey, (list, etag), _options.ListTtl);
        return (list, etag);
    }

    public async Task<(IReadOnlyList<ModelDto> list, string etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct)
    {
        var key = manufacturerId is Guid mid ? ModelsKey(mid) : ModelsAllKey;
        if (_cache.TryGetValue<(IReadOnlyList<ModelDto> list, string etag)>(key, out var cached))
        {
            return cached;
        }

        var q = _db.Models.AsNoTracking().Include(m => m.SupportedFilamentTypes).ThenInclude(sf => sf.FilamentType).AsQueryable();
        if (manufacturerId is Guid mid2)
        {
            q = q.Where(m => m.ManufacturerId == mid2);
        }
        var list = await q.OrderBy(m => m.Name)
            .Select(m => new ModelDto(m.Id, m.Name, m.ManufacturerId, m.MaxX, m.MaxY, m.MaxZ,
                m.DefaultBackend.HasValue ? (PrinterBackend)m.DefaultBackend.Value : (PrinterBackend?)null,
                m.SupportedFilamentTypes.Select(sf => sf.FilamentType!.Name).ToArray())).ToListAsync(ct);
        var etagInput = list.Select(m => m.Id.ToString("N") + ":" + m.Name).Prepend(manufacturerId?.ToString("N") ?? "all");
        var etag = ComputeWeakEtag(etagInput);
        _cache.Set(key, (list, etag), _options.ListTtl);
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
        var joined = string.Join('|', parts);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        var hash = Convert.ToHexString(bytes, 0, 8);
        return $"W/\"{hash}\"";
    }
}