using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Infrastructure.Caching;

internal sealed class CatalogCache(IMemoryCache cache, Microsoft.Extensions.Options.IOptions<CatalogCacheOptions> options, IServiceProvider services) : ICatalogCache
{
    private const string ManufacturersKey = "catalog:mfglst";
    private const string ModelsAllKey = "catalog:models:all";

    private readonly IMemoryCache _cache = cache;
    private readonly CatalogCacheOptions _options = options.Value;
    private readonly IServiceProvider _services = services;
    private IDbContextFactory<AppDbContext>? _dbFactory;

    private static string ModelsKey(Guid id) => $"catalog:models:{id}";

    public async Task<(IReadOnlyList<ManufacturerDto> List, string Etag)> GetManufacturersAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(ManufacturersKey, out (IReadOnlyList<ManufacturerDto> List, string Etag) cached))
        {
            return cached;
        }

        // Resolve the IDbContextFactory lazily from the service provider. Some test
        // scenarios register or mutate DbContextFactory registration at test-host
        // build time; resolving lazily avoids forcing the factory to exist during
        // singleton validation/build-time checks.
        IDbContextFactory<AppDbContext> dbFactory = _dbFactory ??= _services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using AppDbContext db = dbFactory.CreateDbContext();
        List<ManufacturerDto> list = await db.Manufacturers.AsNoTracking().OrderBy(m => m.Name)
            .Select(m => new ManufacturerDto(m.Id, m.Name, m.Url, m.Description)).ToListAsync(ct);
        string etag = ComputeWeakEtag(list.Select(m => m.Id.ToString("N") + ":" + m.Name));
        _ = _cache.Set(ManufacturersKey, (list, etag), _options.ListTtl);
        return (list, etag);
    }

    public async Task<(IReadOnlyList<PrinterModelDto> List, string Etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct)
    {
        string key = manufacturerId is Guid mid ? ModelsKey(mid) : ModelsAllKey;
        if (_cache.TryGetValue(key, out (IReadOnlyList<PrinterModelDto> List, string Etag) cached))
        {
            return cached;
        }

        IDbContextFactory<AppDbContext> dbFactory2 = _dbFactory ??= _services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using AppDbContext db = dbFactory2.CreateDbContext();

        IQueryable<PrinterModel> q = db.PrinterModels.AsNoTracking()
            .Include(m => m.SupportedFilamentTypes).ThenInclude(sf => sf.FilamentType)
            .Include(m => m.Toolheads).ThenInclude(t => t.HotendModel)
            .Include(m => m.Toolheads).ThenInclude(t => t.ExtruderModel)
            .Include(m => m.Toolheads).ThenInclude(t => t.ToolheadModelDef)
            .Include(m => m.Toolheads).ThenInclude(t => t.NozzleModel)
            .AsSplitQuery()
            .AsQueryable();
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

                // Default capabilities (nozzle diameter and max hotend temp are now on toolheads)
                m.HasHeatedBed,
                m.HasEnclosure,
                m.MultiMaterial,
                m.NumberOfExtruders,
                m.SupportsAutoLeveling,

                // Temperature ranges
                m.MaxBedTemp,

                // Speed capabilities
                m.MaxPrintSpeed,

                // Toolheads
                m.Toolheads.OrderBy(t => t.Index).Select(t => new PrinterModelToolheadDto(
                    t.Id,
                    t.Name,
                    t.Index,
                    t.NozzleDiameter,
                    t.NozzleType.HasValue ? (NozzleType)t.NozzleType.Value : null,
                    t.MaxHotendTemp,
                    t.MaxFlowRate,
                    t.ToolheadType.HasValue ? (ToolheadType)t.ToolheadType.Value : null,

                    // Component model references (IDs and names)
                    t.HotendModelId,
                    t.HotendModel != null ? t.HotendModel.Name : null,
                    t.ExtruderModelId,
                    t.ExtruderModel != null ? t.ExtruderModel.Name : null,
                    t.ToolheadModelDefId,
                    t.ToolheadModelDef != null ? t.ToolheadModelDef.Name : null,
                    t.NozzleModelId,
                    t.NozzleModel != null ? t.NozzleModel.Name : null,
                    t.SupportedMaterials,
                    t.IsPrimary)).ToArray())).ToListAsync(ct);
        IEnumerable<string> etagInput = list.Select(m => m.Id.ToString("N") + ":" + m.Name).Prepend(manufacturerId?.ToString("N") ?? "all");
        string etag = ComputeWeakEtag(etagInput);
        _ = _cache.Set(key, (list, etag), _options.ListTtl);
        return (list, etag);
    }

    public void InvalidateManufacturers()
    {
        _cache.Remove(ManufacturersKey);
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
