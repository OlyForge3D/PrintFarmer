using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Normalization;
using Farm.Infrastructure.Repositories.Catalog;
using Farm.Infrastructure.Services.Catalog.Caching;
using Farm.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Catalog;

/// <summary>
/// Core catalog service with pure business logic.
/// Delegates caching to ICatalogCacheProvider abstraction.
/// </summary>
public class CatalogService : ICatalogService
{
    private readonly ICatalogRepository _repo;
    private readonly INormalizationEventLogger _normLogger;
    private readonly ICatalogCacheProvider _cacheProvider;
    private readonly IUnifiedLoggingService _logger;

    // Cache for unknown catalog IDs to avoid repeated database queries
    private Guid? _cachedUnknownMfgId;
    private Guid? _cachedUnknownModelId;

    // Cache for name-based lookups to avoid repeated database queries during bulk operations
    private Dictionary<string, ManufacturerDto?>? _manufacturerNameCache;
    private Dictionary<(Guid ManufacturerId, string ModelName), PrinterModelDto?>? _modelNameCache;

    public CatalogService(
        ICatalogRepository repo,
        INormalizationEventLogger normLogger,
        ICatalogCacheProvider cacheProvider,
        IUnifiedLoggingService logger)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _normLogger = normLogger ?? throw new ArgumentNullException(nameof(normLogger));
        _cacheProvider = cacheProvider ?? throw new ArgumentNullException(nameof(cacheProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<(IReadOnlyList<ManufacturerDto> list, string? etag)> GetManufacturersAsync(CancellationToken ct)
    {
        try
        {
            return await _cacheProvider.GetManufacturersAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CatalogService] GetManufacturersAsync failed: " + ex.Message);
            throw;
        }
    }

    public async Task<ManufacturerDto> CreateManufacturerAsync(string name, string? url, string? description, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required", nameof(name));
        }

        string original = name;
        string normalized = CatalogNameNormalizer.NormalizeManufacturer(original);
        _normLogger.Log("Manufacturer", original, normalized, "create");

        // Check if manufacturer already exists - return it without creating
        IReadOnlyList<(Guid Id, string Name, string? Url, string? Description)> manufacturerRows = await _repo.GetManufacturersAsync(ct);
        (Guid Id, string Name, string? Url, string? Description) existing = manufacturerRows.ToList().Find(r => string.Equals(r.Name, normalized, StringComparison.OrdinalIgnoreCase));

        if (existing.Id != Guid.Empty)
        {
            _logger.LogInformation($"Manufacturer '{normalized}' already exists with ID {existing.Id}, returning existing manufacturer");
            return new ManufacturerDto(existing.Id, existing.Name, existing.Url, existing.Description);
        }

        // Manufacturer doesn't exist, create it
        Manufacturer mfg = new() { Id = Guid.NewGuid(), Name = normalized, Url = url, Description = description };
        await _repo.AddManufacturerAsync(mfg.Id, mfg.Name, mfg.Url, mfg.Description, ct);

        try
        {
            await _repo.SaveChangesAsync(ct);
            _logger.LogInformation($"Created new manufacturer '{normalized}' with ID {mfg.Id}");
        }
        catch (DbUpdateException ex) when (IsUniqueConstraint(ex))
        {
            // Race condition: another thread created the manufacturer between our check and insert
            // Fetch the existing manufacturer and return it
            _logger.LogInformation($"Race condition detected for manufacturer '{normalized}', fetching existing");
            (Guid Id, string Name, string? Url, string? Description) found = (await _repo.GetManufacturersAsync(ct)).FirstOrDefault(m => m.Name == normalized);
            if (found.Id != Guid.Empty)
            {
                _logger.LogInformation($"Found existing manufacturer '{normalized}' with ID {found.Id} after race condition");
                return new ManufacturerDto(found.Id, found.Name, found.Url, found.Description);
            }

            throw new InvalidOperationException(
                $"Failed to create or retrieve manufacturer '{normalized}' due to database constraint", ex);
        }

        _cacheProvider.InvalidateManufacturers();
        _cacheProvider.InvalidateModels();

        return new ManufacturerDto(mfg.Id, mfg.Name, mfg.Url, mfg.Description);
    }

    public async Task<ManufacturerDto?> GetManufacturerByIdAsync(Guid id, CancellationToken ct)
    {
        (Guid Id, string Name, string? Url, string? Description)? m = await _repo.GetManufacturerByIdAsync(id, ct);
        return m is null ? null : new ManufacturerDto(m.Value.Id, m.Value.Name, m.Value.Url, m.Value.Description);
    }

    public async Task<(IReadOnlyList<PrinterModelDto> list, string? etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct)
    {
        return await _cacheProvider.GetModelsAsync(manufacturerId, ct);
    }

    public async Task<PrinterModelDto?> GetModelByIdAsync(Guid id, CancellationToken ct)
    {
        return await _repo.GetModelByIdAsync(id, ct);
    }

    public async Task<PrinterModelDto> CreateModelAsync(
        Guid manufacturerId,
        string name,
        MotionType? type,
        double? maxX,
        double? maxY,
        double? maxZ,
        PrinterBackend? defaultBackend,
        Guid[]? supportedFilamentTypeIds,
        double? defaultNozzleDiameter,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Model name is required", nameof(name));
        }

        string originalModelName = name;
        string normalizedName = CatalogNameNormalizer.NormalizeModel(originalModelName);
        _normLogger.Log("Model", originalModelName, normalizedName, "create");

        bool mfgExists = await _repo.ManufacturerExistsAsync(manufacturerId, ct);
        if (!mfgExists)
        {
            throw new KeyNotFoundException("Manufacturer not found");
        }

        // Check if model already exists - return it without creating
        List<PrinterModelDto> candidateModels = (await _repo.GetModelsCachedAsync(manufacturerId, ct)).ToList();
        PrinterModelDto? existing = candidateModels.Find(m => string.Equals(m.Name, normalizedName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            _logger.LogInformation($"Model '{normalizedName}' already exists for manufacturer {manufacturerId}, returning existing model");
            return existing;
        }

        // Model doesn't exist, create it
        PrinterModel model = new()
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturerId,
            Name = normalizedName,
            MotionType = type.HasValue ? (int)type.Value : (int?)null,
            MaxX = maxX,
            MaxY = maxY,
            MaxZ = maxZ,
            DefaultBackend = defaultBackend.HasValue ? (int)defaultBackend.Value : (int?)null,
            DefaultNozzleDiameter = defaultNozzleDiameter
        };

        await _repo.AddModelAsync(model, ct);

        try
        {
            await _repo.SaveChangesAsync(ct);
            _logger.LogInformation($"Created new model '{normalizedName}' with ID {model.Id} for manufacturer {manufacturerId}");
        }
        catch (DbUpdateException ex) when (IsUniqueConstraint(ex))
        {
            // Race condition: another thread created the model between our check and insert
            // Fetch the existing model and return it
            _logger.LogInformation($"Race condition detected for model '{normalizedName}', fetching existing");
            PrinterModelDto? existingNowDto = (await _repo.GetModelsCachedAsync(manufacturerId, ct)).FirstOrDefault(m => m.Name == normalizedName);
            if (existingNowDto is not null)
            {
                _logger.LogInformation($"Found existing model '{normalizedName}' with ID {existingNowDto.Id} after race condition");
                return existingNowDto;
            }

            throw new InvalidOperationException(
                $"Failed to create or retrieve model '{normalizedName}' due to database constraint", ex);
        }

        if (supportedFilamentTypeIds?.Length > 0)
        {
            Guid[] validFilamentTypeIds = (await _repo.GetValidFilamentTypeIdsAsync(supportedFilamentTypeIds, ct)).ToArray();
            await _repo.UpdateModelFilamentTypesAsync(model.Id, validFilamentTypeIds, ct);
            await _repo.SaveChangesAsync(ct);
        }

        PrinterModelDto? createdModel = await _repo.GetModelWithFilamentNamesAsync(model.Id, ct);
        _cacheProvider.InvalidateModels(model.ManufacturerId);

        return createdModel ?? new PrinterModelDto(
            model.Id,
            model.Name,
            model.ManufacturerId,
            model.MotionType.HasValue ? (MotionType)model.MotionType.Value : (MotionType?)null,
            model.MaxX,
            model.MaxY,
            model.MaxZ,
            model.DefaultBackend.HasValue ? (PrinterBackend)model.DefaultBackend.Value : (PrinterBackend?)null,
            Array.Empty<string>());
    }

    public async Task<PrinterModelDto?> UpdateModelAsync(
        Guid id,
        string? name,
        MotionType? type,
        double? maxX,
        double? maxY,
        double? maxZ,
        PrinterBackend? defaultBackend,
        Guid[]? supportedFilamentTypeIds,
        double? defaultNozzleDiameter,
        CancellationToken ct)
    {
        PrinterModel? model = await _repo.GetModelEntityAsync(id, ct);
        if (model is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            string before = model.Name;
            string after = CatalogNameNormalizer.NormalizeModel(name);
            model.Name = after;
            _normLogger.Log("Model", before, after, "update");
        }

        model.MotionType = type.HasValue ? (int)type.Value : (int?)null;
        model.MaxX = maxX;
        model.MaxY = maxY;
        model.MaxZ = maxZ;
        model.DefaultBackend = defaultBackend.HasValue ? (int)defaultBackend.Value : (int?)null;

        if (defaultNozzleDiameter.HasValue)
        {
            model.DefaultNozzleDiameter = defaultNozzleDiameter.Value;
        }

        if (supportedFilamentTypeIds != null)
        {
            Guid[] validFilamentTypeIds = supportedFilamentTypeIds.Length > 0
                ? (await _repo.GetValidFilamentTypeIdsAsync(supportedFilamentTypeIds, ct)).ToArray()
                : Array.Empty<Guid>();
            await _repo.UpdateModelFilamentTypesAsync(model.Id, validFilamentTypeIds, ct);
        }

        await _repo.SaveChangesAsync(ct);
        _cacheProvider.InvalidateModels(model.ManufacturerId);

        return new PrinterModelDto(
            model.Id,
            model.Name,
            model.ManufacturerId,
            model.MotionType.HasValue ? (MotionType)model.MotionType.Value : (MotionType?)null,
            model.MaxX,
            model.MaxY,
            model.MaxZ,
            model.DefaultBackend.HasValue ? (PrinterBackend)model.DefaultBackend.Value : (PrinterBackend?)null,
            model.SupportedFilamentTypes.Select(sf => sf.FilamentType!.Name).ToArray());
    }

    public async Task DeleteModelAsync(Guid id, CancellationToken ct)
    {
        PrinterModel? model = await _repo.GetModelEntityAsync(id, ct);
        if (model is null)
        {
            throw new KeyNotFoundException($"Model with id '{id}' not found");
        }

        Guid manufacturerId = model.ManufacturerId;
        await _repo.RemoveModelAsync(id, ct);
        await _repo.SaveChangesAsync(ct);
        _cacheProvider.InvalidateModels(manufacturerId);
    }

    private static bool IsUniqueConstraint(DbUpdateException ex)
    {
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

        string msg = ex.InnerException?.Message ?? ex.Message;
        if (!string.IsNullOrEmpty(msg) && (msg.Contains("NameLowered", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("IX_Manufacturers_NameLowered", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    /// <summary>Finds a manufacturer by name with caching. Returns null if not found.</summary>
    public async Task<ManufacturerDto?> FindManufacturerByNameAsync(string name, CancellationToken ct)
    {
        // Initialize cache on first use
        if (_manufacturerNameCache == null)
        {
            _manufacturerNameCache = new Dictionary<string, ManufacturerDto?>();
        }

        // Check cache first
        if (_manufacturerNameCache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        // Query database if not in cache
        Manufacturer? entity = await _repo.FindManufacturerByNameAsync(name, ct);
        ManufacturerDto? result = entity != null ? new ManufacturerDto(entity.Id, entity.Name) : null;

        // Store in cache (including nulls to avoid repeated queries for non-existent entries)
        _manufacturerNameCache[name] = result;

        return result;
    }

    /// <summary>Finds a printer model by name and manufacturer ID with caching. Returns null if not found.</summary>
    public async Task<PrinterModelDto?> FindModelByNameAsync(string name, Guid manufacturerId, CancellationToken ct)
    {
        // Initialize cache on first use
        if (_modelNameCache == null)
        {
            _modelNameCache = new Dictionary<(Guid ManufacturerId, string ModelName), PrinterModelDto?>();
        }

        var cacheKey = (manufacturerId, name);

        // Check cache first
        if (_modelNameCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        // Query database if not in cache
        PrinterModel? entity = await _repo.FindModelByNameAsync(name, manufacturerId, ct);
        PrinterModelDto? result = entity != null ? new PrinterModelDto(
            entity.Id,
            entity.Name,
            entity.ManufacturerId,
            entity.MotionType.HasValue ? (MotionType)entity.MotionType.Value : (MotionType?)null,
            entity.MaxX,
            entity.MaxY,
            entity.MaxZ,
            entity.DefaultBackend.HasValue ? (PrinterBackend)entity.DefaultBackend.Value : (PrinterBackend?)null,
            Array.Empty<string>()) : null;

        // Store in cache (including nulls to avoid repeated queries for non-existent entries)
        _modelNameCache[cacheKey] = result;

        return result;
    }

    /// <summary>Gets the default (Unknown) manufacturer and model IDs with caching.</summary>
    public async Task<(Guid ManufacturerId, Guid ModelId)> GetDefaultCatalogIdsAsync(CancellationToken ct)
    {
        // Return cached values if available
        if (_cachedUnknownMfgId.HasValue && _cachedUnknownModelId.HasValue)
        {
            return (_cachedUnknownMfgId.Value, _cachedUnknownModelId.Value);
        }

        Guid? unknownMfgId = _cachedUnknownMfgId ?? await _repo.GetUnknownManufacturerIdAsync(ct);
        if (!unknownMfgId.HasValue)
        {
            throw new InvalidOperationException("Unknown manufacturer not found. Ensure database seeding has been completed.");
        }
        _cachedUnknownMfgId = unknownMfgId;

        Guid? unknownModelId = _cachedUnknownModelId ?? await _repo.GetUnknownModelIdAsync(ct);
        if (!unknownModelId.HasValue)
        {
            throw new InvalidOperationException("Unknown model not found. Ensure database seeding has been completed.");
        }
        _cachedUnknownModelId = unknownModelId;

        return (unknownMfgId.Value, unknownModelId.Value);
    }

    /// <summary>Gets all slicer model name aliases for a printer model.</summary>
    public async Task<IEnumerable<SlicerModelAliasDto>> GetModelAliasesAsync(Guid modelId, CancellationToken ct)
    {
        try
        {
            var aliases = await _repo.GetModelAliasesAsync(modelId, ct);
            return aliases.Select(a => new SlicerModelAliasDto(a.Id, a.PrinterModelId, a.SlicerModelName, a.SlicerType));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[CatalogService] GetModelAliasesAsync failed for modelId {modelId}: {ex.Message}");
            throw;
        }
    }

    /// <summary>Updates slicer model name aliases for a printer model.</summary>
    public async Task<IEnumerable<SlicerModelAliasDto>> UpdateModelAliasesAsync(Guid modelId, List<string> orcaSlicerNames, List<string> prusaSlicerNames, CancellationToken ct)
    {
        try
        {
            var aliases = await _repo.UpdateModelAliasesAsync(modelId, orcaSlicerNames ?? new List<string>(), prusaSlicerNames ?? new List<string>(), ct);
            await _repo.SaveChangesAsync(ct);
            return aliases.Select(a => new SlicerModelAliasDto(a.Id, a.PrinterModelId, a.SlicerModelName, a.SlicerType));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[CatalogService] UpdateModelAliasesAsync failed for modelId {modelId}: {ex.Message}");
            throw;
        }
    }
}
