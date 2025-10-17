using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Infrastructure.Caching;
using Farm.Web.Api.Infrastructure.Exceptions;
using Farm.Infrastructure.Normalization;
using Farm.Web.Api.Infrastructure.Normalization;
using Farm.Web.Shared;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services.Catalog;

public class CatalogService : ICatalogService
{
    private readonly AppDbContext _db;
    private readonly INormalizationEventLogger _normLogger;
    private readonly ICatalogCache _catalogCache;
    private readonly Farm.Infrastructure.Telemetry.IUnifiedLoggingService _unifiedLoggingService;

    public CatalogService(AppDbContext db, INormalizationEventLogger normLogger, ICatalogCache catalogCache, Farm.Infrastructure.Telemetry.IUnifiedLoggingService unifiedLoggingService)
    {
        _db = db;
        _normLogger = normLogger;
        _catalogCache = catalogCache;
        _unifiedLoggingService = unifiedLoggingService;
    }

    public async Task<(IReadOnlyList<ManufacturerDto> list, string? etag)> GetManufacturersAsync(CancellationToken ct)
    {
        try
        {
            return await _catalogCache.GetManufacturersAsync(ct);
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, "[CatalogService] GetManufacturersAsync failed: " + ex.Message);
            throw;
        }
    }

    public async Task<ManufacturerDto> CreateManufacturerAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required", nameof(name));
        }
        string original = name;
        string normalized = CatalogNameNormalizer.NormalizeManufacturer(original);
        _normLogger.Log("Manufacturer", original, normalized, "create");

        var manufacturerRows = await _db.Manufacturers.AsNoTracking()
            .Select(m => new { m.Id, m.Name })
            .ToListAsync(ct);
        var existing = manufacturerRows.Find(r => string.Equals(r.Name, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            string? headerName = null;
            if (!string.Equals(original.Trim(), existing.Name, StringComparison.Ordinal))
            {
                headerName = existing.Name;
            }
            throw new DuplicateEntityException("Manufacturer", new ManufacturerDto(existing.Id, existing.Name), headerName,
                $"A manufacturer with the normalized name '{existing.Name}' already exists.");
        }

        Manufacturer mfg = new() { Id = Guid.NewGuid(), Name = normalized };
        _ = _db.Manufacturers.Add(mfg);
        try
        {
            _ = await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraint(ex))
        {
            Manufacturer existingNow = await _db.Manufacturers.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Name == normalized, ct) ?? new Manufacturer { Id = mfg.Id, Name = normalized };
            throw new DuplicateEntityException("Manufacturer", new ManufacturerDto(existingNow.Id, existingNow.Name), null,
                $"A manufacturer with the normalized name '{existingNow.Name}' already exists.");
        }

        _catalogCache.InvalidateManufacturers();
        _catalogCache.InvalidateModels();
        return new ManufacturerDto(mfg.Id, mfg.Name);
    }

    public async Task<ManufacturerDto?> GetManufacturerByIdAsync(Guid id, CancellationToken ct)
    {
        Manufacturer? m = await _db.Manufacturers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return m is null ? null : new ManufacturerDto(m.Id, m.Name);
    }

    public async Task<(IReadOnlyList<PrinterModelDto> list, string? etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct)
    {
        return await _catalogCache.GetModelsAsync(manufacturerId, ct);
    }

    public async Task<PrinterModelDto?> GetModelByIdAsync(Guid id, CancellationToken ct)
    {
        PrinterModel? model = await _db.Models.AsNoTracking().Include(m => m.SupportedFilamentTypes).ThenInclude(sf => sf.FilamentType)
            .FirstOrDefaultAsync(m => m.Id == id, ct);
        if (model is null)
        {
            return null;
        }
        return new PrinterModelDto(model.Id,
            model.Name,
            model.ManufacturerId,
            model.MotionType.HasValue ? (MotionType)model.MotionType.Value : (MotionType?)null,
            model.MaxX,
            model.MaxY,
            model.MaxZ,
            model.DefaultBackend.HasValue ? (PrinterBackend)model.DefaultBackend.Value : (PrinterBackend?)null,
            model.SupportedFilamentTypes.Select(sf => sf.FilamentType!.Name).ToArray(),
            model.DefaultNozzleDiameter,
            model.HasHeatedBed,
            model.HasEnclosure,
            model.MultiMaterial,
            model.NumberOfExtruders,
            model.SupportsAutoLeveling,
            model.MinHotendTemp,
            model.MaxHotendTemp,
            model.MinBedTemp,
            model.MaxBedTemp,
            model.MaxPrintSpeed);
    }

    public async Task<PrinterModelDto> CreateModelAsync(Farm.Web.Api.Controllers.Requests.CreateModelRequest req, CancellationToken ct)
    {
        string originalModelName = req.Name;
        string normalizedName = CatalogNameNormalizer.NormalizeModel(originalModelName);
        _normLogger.Log("Model", originalModelName, normalizedName, "create");

        bool mfgExists = await _db.Manufacturers.AsNoTracking().AnyAsync(m => m.Id == req.ManufacturerId, ct);
        if (!mfgExists)
        {
            throw new KeyNotFoundException("Manufacturer not found");
        }

        var candidateNames = await _db.Models.AsNoTracking()
            .Where(m => m.ManufacturerId == req.ManufacturerId)
            .Select(m => new { m.Id, m.ManufacturerId, m.Name, m.MotionType, m.MaxX, m.MaxY, m.MaxZ, m.DefaultBackend })
            .ToListAsync(ct);
        var existing = candidateNames.Find(m => string.Equals(m.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            string? headerName = null;
            if (!string.Equals(originalModelName.Trim(), existing.Name, StringComparison.Ordinal))
            {
                headerName = existing.Name;
            }
            throw new DuplicateEntityException("Model", new PrinterModelDto(existing.Id, existing.Name, existing.ManufacturerId, existing.MotionType.HasValue ? (MotionType)existing.MotionType.Value : (MotionType?)null, existing.MaxX, existing.MaxY, existing.MaxZ,
                existing.DefaultBackend.HasValue ? (PrinterBackend)existing.DefaultBackend.Value : (PrinterBackend?)null), headerName,
                $"A model with the normalized name '{existing.Name}' already exists for this manufacturer.");
        }

        PrinterModel model = new()
        {
            Id = Guid.NewGuid(),
            ManufacturerId = req.ManufacturerId,
            Name = normalizedName,
            MotionType = req.Type.HasValue ? (int)req.Type.Value : (int?)null,
            MaxX = req.MaxX,
            MaxY = req.MaxY,
            MaxZ = req.MaxZ,
            DefaultBackend = req.DefaultBackend.HasValue ? (int)req.DefaultBackend.Value : (int?)null,
            DefaultNozzleDiameter = req.DefaultNozzleDiameter,
            HasHeatedBed = req.HasHeatedBed,
            HasEnclosure = req.HasEnclosure,
            MultiMaterial = req.MultiMaterial,
            NumberOfExtruders = req.NumberOfExtruders,
            SupportsAutoLeveling = req.SupportsAutoLeveling,
            MinHotendTemp = req.MinHotendTemp,
            MaxHotendTemp = req.MaxHotendTemp,
            MinBedTemp = req.MinBedTemp,
            MaxBedTemp = req.MaxBedTemp,
            MaxPrintSpeed = req.MaxPrintSpeed
        };
        _ = _db.Models.Add(model);

        if (req.SupportedFilamentTypeIds?.Length > 0)
        {
            List<Guid> validFilamentTypeIds = await _db.FilamentTypes.AsNoTracking()
                .Where(f => req.SupportedFilamentTypeIds.Contains(f.Id))
                .Select(f => f.Id)
                .ToListAsync(ct);

            foreach (Guid filamentTypeId in validFilamentTypeIds)
            {
                _ = _db.PrinterModelFilamentTypes.Add(new PrinterModelFilamentType
                {
                    PrinterModelId = model.Id,
                    FilamentTypeId = filamentTypeId
                });
            }
        }

        try
        {
            _ = await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraint(ex))
        {
            PrinterModel existingNow = await _db.Models.AsNoTracking()
                .FirstOrDefaultAsync(m => m.ManufacturerId == req.ManufacturerId && m.Name == normalizedName, ct) ?? new PrinterModel { Id = model.Id, Name = normalizedName, ManufacturerId = req.ManufacturerId };
            throw new DuplicateEntityException("Model", new PrinterModelDto(existingNow.Id, existingNow.Name, existingNow.ManufacturerId, existingNow.MotionType.HasValue ? (MotionType)existingNow.MotionType.Value : (MotionType?)null, existingNow.MaxX, existingNow.MaxY, existingNow.MaxZ,
                existingNow.DefaultBackend.HasValue ? (PrinterBackend)existingNow.DefaultBackend.Value : (PrinterBackend?)null), null,
                $"A model with the normalized name '{existingNow.Name}' already exists for this manufacturer.");
        }

        PrinterModel? createdModel = await _db.Models.AsNoTracking()
            .Include(m => m.SupportedFilamentTypes).ThenInclude(sf => sf.FilamentType)
            .FirstOrDefaultAsync(m => m.Id == model.Id, ct);

        _catalogCache.InvalidateModels(model.ManufacturerId);
        return new PrinterModelDto(model.Id, model.Name, model.ManufacturerId, model.MotionType.HasValue ? (MotionType)model.MotionType.Value : (MotionType?)null, model.MaxX, model.MaxY, model.MaxZ,
            model.DefaultBackend.HasValue ? (PrinterBackend)model.DefaultBackend.Value : (PrinterBackend?)null,
            createdModel?.SupportedFilamentTypes.Select(sf => sf.FilamentType!.Name).ToArray());
    }

    public async Task<PrinterModelDto?> UpdateModelAsync(Guid id, Farm.Web.Api.Controllers.Requests.UpdateModelRequest req, CancellationToken ct)
    {
        PrinterModel? model = await _db.Models.Include(m => m.SupportedFilamentTypes).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (model is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(req.Name))
        {
            string before = model.Name;
            string after = CatalogNameNormalizer.NormalizeModel(req.Name);
            model.Name = after;
            _normLogger.Log("Model", before, after, "update");
        }

        model.MotionType = req.Type.HasValue ? (int)req.Type.Value : (int?)null;
        model.MaxX = req.MaxX;
        model.MaxY = req.MaxY;
        model.MaxZ = req.MaxZ;
        model.DefaultBackend = req.DefaultBackend.HasValue ? (int)req.DefaultBackend.Value : (int?)null;

        if (req.DefaultNozzleDiameter.HasValue)
        {
            model.DefaultNozzleDiameter = req.DefaultNozzleDiameter.Value;
        }
        if (req.HasHeatedBed.HasValue)
        {
            model.HasHeatedBed = req.HasHeatedBed.Value;
        }
        if (req.HasEnclosure.HasValue)
        {
            model.HasEnclosure = req.HasEnclosure.Value;
        }
        if (req.MultiMaterial.HasValue)
        {
            model.MultiMaterial = req.MultiMaterial.Value;
        }
        if (req.NumberOfExtruders.HasValue)
        {
            model.NumberOfExtruders = req.NumberOfExtruders.Value;
        }
        if (req.SupportsAutoLeveling.HasValue)
        {
            model.SupportsAutoLeveling = req.SupportsAutoLeveling.Value;
        }
        if (req.MinHotendTemp.HasValue)
        {
            model.MinHotendTemp = req.MinHotendTemp.Value;
        }
        if (req.MaxHotendTemp.HasValue)
        {
            model.MaxHotendTemp = req.MaxHotendTemp.Value;
        }
        if (req.MinBedTemp.HasValue)
        {
            model.MinBedTemp = req.MinBedTemp.Value;
        }
        if (req.MaxBedTemp.HasValue)
        {
            model.MaxBedTemp = req.MaxBedTemp.Value;
        }
        if (req.MaxPrintSpeed.HasValue)
        {
            model.MaxPrintSpeed = req.MaxPrintSpeed.Value;
        }

        if (req.SupportedFilamentTypeIds != null)
        {
            _db.PrinterModelFilamentTypes.RemoveRange(model.SupportedFilamentTypes);
            if (req.SupportedFilamentTypeIds.Length > 0)
            {
                List<Guid> validFilamentTypeIds = await _db.FilamentTypes.AsNoTracking()
                    .Where(f => req.SupportedFilamentTypeIds.Contains(f.Id))
                    .Select(f => f.Id)
                    .ToListAsync(ct);
                foreach (Guid filamentTypeId in validFilamentTypeIds)
                {
                    _ = _db.PrinterModelFilamentTypes.Add(new PrinterModelFilamentType
                    {
                        PrinterModelId = model.Id,
                        FilamentTypeId = filamentTypeId
                    });
                }
            }
        }

        _ = await _db.SaveChangesAsync(ct);
        _catalogCache.InvalidateModels(model.ManufacturerId);
        return new PrinterModelDto(model.Id, model.Name, model.ManufacturerId, model.MotionType.HasValue ? (MotionType)model.MotionType.Value : (MotionType?)null, model.MaxX, model.MaxY, model.MaxZ,
            model.DefaultBackend.HasValue ? (PrinterBackend)model.DefaultBackend.Value : (PrinterBackend?)null,
            model.SupportedFilamentTypes.Select(sf => sf.FilamentType!.Name).ToArray());
    }

    private static bool IsUniqueConstraint(DbUpdateException ex)
    {
        // Copy the same logic from the controller helper
        if (ex.InnerException is Microsoft.Data.Sqlite.SqliteException se && se.SqliteErrorCode == 19)
        {
            return true;
        }
#if NET8_0_OR_GREATER
        if (ex.InnerException is System.Data.Common.DbException dbx)
        {
            string typeName = dbx.GetType().FullName ?? string.Empty;
            if (typeName.Contains("SqlException", StringComparison.OrdinalIgnoreCase) && dbx.ErrorCode is 2601 or 2627)
            {
                return true;
            }
        }
#endif
        if (ex.InnerException?.GetType().FullName?.Contains("PostgresException", StringComparison.OrdinalIgnoreCase) == true &&
            ex.InnerException?.GetType().GetProperty("SqlState")?.GetValue(ex.InnerException)?.ToString() == "23505")
        {
            return true;
        }
        if (ex.InnerException?.GetType().FullName?.Contains("MySqlException", StringComparison.OrdinalIgnoreCase) == true &&
            ex.InnerException?.GetType().GetProperty("Number")?.GetValue(ex.InnerException) is int num && num == 1062)
        {
            return true;
        }
        string msg = ex.InnerException?.Message ?? ex.Message;
        if (!string.IsNullOrEmpty(msg) && (msg.Contains("NameLowered", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("IX_Manufacturers_NameLowered", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        return false;
    }
}
