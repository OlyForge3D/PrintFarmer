using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Normalization;
using Farm.Infrastructure.Repositories.Catalog;
using Farm.Infrastructure.Services.Catalog.Caching;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Catalog;

/// <summary>
/// Core catalog service with pure business logic.
/// Delegates caching to ICatalogCacheProvider abstraction.
/// </summary>
public class CatalogService(
    ICatalogRepository repo,
    INormalizationEventLogger normLogger,
    ICatalogCacheProvider cacheProvider,
    ILogger<CatalogService> logger) : ICatalogService
{
    private readonly ICatalogRepository _repo = repo ?? throw new ArgumentNullException(nameof(repo));
    private readonly INormalizationEventLogger _normLogger = normLogger ?? throw new ArgumentNullException(nameof(normLogger));
    private readonly ICatalogCacheProvider _cacheProvider = cacheProvider ?? throw new ArgumentNullException(nameof(cacheProvider));
    private readonly ILogger<CatalogService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    // Cache for unknown catalog IDs to avoid repeated database queries
    private Guid? _cachedUnknownMfgId;
    private Guid? _cachedUnknownModelId;

    // Cache for name-based lookups to avoid repeated database queries during bulk operations
    private Dictionary<string, ManufacturerDto?>? _manufacturerNameCache;
    private Dictionary<(Guid ManufacturerId, string ModelName), PrinterModelDto?>? _modelNameCache;

    public async Task<(IReadOnlyList<ManufacturerDto> List, string? Etag)> GetManufacturersAsync(CancellationToken ct)
    {
        try
        {
            return await _cacheProvider.GetManufacturersAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CatalogService] GetManufacturersAsync failed: {Message}", ex.Message);
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
            _logger.LogInformation("Manufacturer '{Normalized}' already exists with ID {ExistingId}, returning existing manufacturer", normalized, existing.Id);
            return new ManufacturerDto(existing.Id, existing.Name, existing.Url, existing.Description);
        }

        // Manufacturer doesn't exist, create it
        Manufacturer mfg = new() { Id = Guid.NewGuid(), Name = normalized, Url = url, Description = description };
        await _repo.AddManufacturerAsync(mfg.Id, mfg.Name, mfg.Url, mfg.Description, ct);

        try
        {
            await _repo.SaveChangesAsync(ct);
            _logger.LogInformation("Created new manufacturer '{Normalized}' with ID {MfgId}", normalized, mfg.Id);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraint(ex))
        {
            // Race condition: another thread created the manufacturer between our check and insert
            // Fetch the existing manufacturer and return it
            _logger.LogInformation("Race condition detected for manufacturer '{Normalized}', fetching existing", normalized);
            (Guid Id, string Name, string? Url, string? Description) found = (await _repo.GetManufacturersAsync(ct)).FirstOrDefault(m => m.Name == normalized);
            if (found.Id != Guid.Empty)
            {
                _logger.LogInformation("Found existing manufacturer '{Normalized}' with ID {FoundId} after race condition", normalized, found.Id);
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

    public async Task<(IReadOnlyList<PrinterModelDto> List, string? Etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct)
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
        bool? hasHeatedBed,
        bool? hasEnclosure,
        bool? multiMaterial,
        bool? supportsAutoLeveling,
        int? maxBedTemp,
        int? maxPrintSpeed,
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
            _logger.LogInformation("Model '{NormalizedName}' already exists for manufacturer {ManufacturerId}, returning existing model", normalizedName, manufacturerId);
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
            HasHeatedBed = hasHeatedBed ?? true,
            HasEnclosure = hasEnclosure ?? false,
            MultiMaterial = multiMaterial ?? false,
            SupportsAutoLeveling = supportsAutoLeveling ?? false,
            MaxBedTemp = maxBedTemp,
            MaxPrintSpeed = maxPrintSpeed
        };

        await _repo.AddModelAsync(model, ct);

        try
        {
            await _repo.SaveChangesAsync(ct);
            _logger.LogInformation("Created new model '{NormalizedName}' with ID {ModelId} for manufacturer {ManufacturerId}", normalizedName, model.Id, manufacturerId);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraint(ex))
        {
            // Race condition: another thread created the model between our check and insert
            // Fetch the existing model and return it
            _logger.LogInformation("Race condition detected for model '{NormalizedName}', fetching existing", normalizedName);
            PrinterModelDto? existingNowDto = (await _repo.GetModelsCachedAsync(manufacturerId, ct)).FirstOrDefault(m => m.Name == normalizedName);
            if (existingNowDto is not null)
            {
                _logger.LogInformation("Found existing model '{NormalizedName}' with ID {ExistingNowDtoId} after race condition", normalizedName, existingNowDto.Id);
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
        bool? hasHeatedBed,
        bool? hasEnclosure,
        bool? multiMaterial,
        bool? supportsAutoLeveling,
        int? maxBedTemp,
        int? maxPrintSpeed,
        PrinterModelToolheadDto[]? toolheads,
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

        // Update capability fields
        if (hasHeatedBed.HasValue)
        {
            model.HasHeatedBed = hasHeatedBed.Value;
        }

        if (hasEnclosure.HasValue)
        {
            model.HasEnclosure = hasEnclosure.Value;
        }

        if (multiMaterial.HasValue)
        {
            model.MultiMaterial = multiMaterial.Value;
        }

        if (supportsAutoLeveling.HasValue)
        {
            model.SupportsAutoLeveling = supportsAutoLeveling.Value;
        }

        if (maxBedTemp.HasValue)
        {
            model.MaxBedTemp = maxBedTemp.Value;
        }

        if (maxPrintSpeed.HasValue)
        {
            model.MaxPrintSpeed = maxPrintSpeed.Value;
        }

        if (supportedFilamentTypeIds != null)
        {
            Guid[] validFilamentTypeIds = supportedFilamentTypeIds.Length > 0
                ? (await _repo.GetValidFilamentTypeIdsAsync(supportedFilamentTypeIds, ct)).ToArray()
                : Array.Empty<Guid>();
            await _repo.UpdateModelFilamentTypesAsync(model.Id, validFilamentTypeIds, ct);
        }

        // Update toolheads if provided
        if (toolheads != null)
        {
            await _repo.UpdateModelToolheadsAsync(model.Id, toolheads, ct);
        }

        await _repo.SaveChangesAsync(ct);
        _cacheProvider.InvalidateModels(model.ManufacturerId);

        // Re-fetch model to get updated toolheads
        return await GetModelByIdAsync(model.Id, ct);
    }

    public async Task DeleteModelAsync(Guid id, CancellationToken ct)
    {
        PrinterModel? model = await _repo.GetModelEntityAsync(id, ct) ?? throw new KeyNotFoundException($"Model with id '{id}' not found");

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
        return !string.IsNullOrEmpty(msg) && (msg.Contains("NameLowered", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("IX_Manufacturers_NameLowered", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Finds a manufacturer by name with caching. Returns null if not found.</summary>
    /// <param name="name">The manufacturer name to search for</param>
    /// <param name="ct">Cancellation token for async operation</param>
    public async Task<ManufacturerDto?> FindManufacturerByNameAsync(string name, CancellationToken ct)
    {
        // Initialize cache on first use
        if (_manufacturerNameCache == null)
        {
            _manufacturerNameCache = new Dictionary<string, ManufacturerDto?>();
        }

        // Check cache first
        if (_manufacturerNameCache.TryGetValue(name, out ManufacturerDto? cached))
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
    /// <param name="name">The model name to search for</param>
    /// <param name="manufacturerId">The manufacturer ID to filter by</param>
    /// <param name="ct">Cancellation token for async operation</param>
    public async Task<PrinterModelDto?> FindModelByNameAsync(string name, Guid manufacturerId, CancellationToken ct)
    {
        // Initialize cache on first use
        if (_modelNameCache == null)
        {
            _modelNameCache = new Dictionary<(Guid ManufacturerId, string ModelName), PrinterModelDto?>();
        }

        (Guid manufacturerId, string name) cacheKey = (manufacturerId, name);

        // Check cache first
        if (_modelNameCache.TryGetValue(cacheKey, out PrinterModelDto? cached))
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
    /// <param name="ct">Cancellation token for async operation</param>
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
    /// <param name="modelId">The printer model ID to get aliases for</param>
    /// <param name="ct">Cancellation token for async operation</param>
    public async Task<IEnumerable<SlicerModelAliasDto>> GetModelAliasesAsync(Guid modelId, CancellationToken ct)
    {
        try
        {
            List<PrinterModelAlias> aliases = await _repo.GetModelAliasesAsync(modelId, ct);
            return aliases.Select(a => new SlicerModelAliasDto(a.Id, a.PrinterModelId, a.SlicerModelName, a.SlicerType));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CatalogService] GetModelAliasesAsync failed for modelId {ModelId}: {Message}", modelId, ex.Message);
            throw;
        }
    }

    /// <summary>Updates slicer model name aliases for a printer model.</summary>
    /// <param name="modelId">The printer model ID to update aliases for</param>
    /// <param name="orcaSlicerNames">The list of OrcaSlicer model names</param>
    /// <param name="prusaSlicerNames">The list of PrusaSlicer model names</param>
    /// <param name="ct">Cancellation token for async operation</param>
    public async Task<IEnumerable<SlicerModelAliasDto>> UpdateModelAliasesAsync(Guid modelId, List<string> orcaSlicerNames, List<string> prusaSlicerNames, CancellationToken ct)
    {
        try
        {
            List<PrinterModelAlias> aliases = await _repo.UpdateModelAliasesAsync(modelId, orcaSlicerNames ?? new List<string>(), prusaSlicerNames ?? new List<string>(), ct);
            await _repo.SaveChangesAsync(ct);
            return aliases.Select(a => new SlicerModelAliasDto(a.Id, a.PrinterModelId, a.SlicerModelName, a.SlicerType));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CatalogService] UpdateModelAliasesAsync failed for modelId {ModelId}: {Message}", modelId, ex.Message);
            throw;
        }
    }

    // ============ Component Model Methods ============

    /// <summary>Gets all hotend model definitions.</summary>
    /// <param name="ct">Cancellation token for async operation</param>
    public async Task<IReadOnlyList<HotendModelDto>> GetHotendModelsAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<(Guid Id, string Name, Guid ManufacturerId, string? ManufacturerName, int? MaxTemp, bool IsHighFlow, NozzleInterfaceType NozzleInterface, string? Description, string? Url)> hotends = await _repo.GetHotendModelsAsync(ct);
            return hotends.Select(h => new HotendModelDto(
                h.Id,
                h.Name,
                h.ManufacturerId,
                h.ManufacturerName,
                h.MaxTemp,
                h.IsHighFlow,
                h.NozzleInterface,
                h.Description,
                h.Url)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CatalogService] GetHotendModelsAsync failed: {Message}", ex.Message);
            throw;
        }
    }

    /// <summary>Gets all extruder model definitions.</summary>
    /// <param name="ct">Cancellation token for async operation</param>
    public async Task<IReadOnlyList<ExtruderModelDto>> GetExtruderModelsAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<(Guid Id, string Name, Guid ManufacturerId, string? ManufacturerName, string? GearRatio, bool IsDirectDrive, string? Description, string? Url)> extruders = await _repo.GetExtruderModelsAsync(ct);
            return extruders.Select(e => new ExtruderModelDto(
                e.Id,
                e.Name,
                e.ManufacturerId,
                e.ManufacturerName,
                e.GearRatio,
                e.IsDirectDrive,
                e.Description,
                e.Url)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CatalogService] GetExtruderModelsAsync failed: {Message}", ex.Message);
            throw;
        }
    }

    /// <summary>Gets all toolhead model definitions.</summary>
    /// <param name="ct">Cancellation token for async operation</param>
    public async Task<IReadOnlyList<ToolheadModelDto>> GetToolheadModelsAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<(Guid Id, string Name, Guid ManufacturerId, string? ManufacturerName, string? Description, string? Url, Guid? DefaultHotendId, Guid? DefaultExtruderId, Guid? DefaultNozzleId)> toolheads = await _repo.GetToolheadModelsAsync(ct);
            return toolheads.Select(t => new ToolheadModelDto(
                t.Id,
                t.Name,
                t.ManufacturerId,
                t.ManufacturerName,
                t.Description,
                t.Url,
                t.DefaultHotendId,
                t.DefaultExtruderId,
                t.DefaultNozzleId)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CatalogService] GetToolheadModelsAsync failed: {Message}", ex.Message);
            throw;
        }
    }

    /// <summary>Gets all nozzle model definitions.</summary>
    /// <param name="ct">Cancellation token for async operation</param>
    public async Task<IReadOnlyList<NozzleModelDto>> GetNozzleModelsAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<(Guid Id, string Name, Guid ManufacturerId, string? ManufacturerName, double Diameter, int? MaxTemp, NozzleType NozzleType, bool IsHardened, NozzleInterfaceType NozzleInterface, string? Description, string? Url)> nozzles = await _repo.GetNozzleModelsAsync(ct);
            return nozzles.Select(n => new NozzleModelDto(
                n.Id,
                n.Name,
                n.ManufacturerId,
                n.ManufacturerName,
                n.Diameter,
                n.MaxTemp,
                n.NozzleType,
                n.IsHardened,
                n.NozzleInterface,
                n.Description,
                n.Url)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CatalogService] GetNozzleModelsAsync failed: {Message}", ex.Message);
            throw;
        }
    }

    // ============ Component Model CRUD Methods ============
    #region Hotend Model CRUD

    public async Task<HotendModelDto> CreateHotendModelAsync(CreateHotendModelDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            throw new ArgumentException("Name is required", nameof(dto));
        }

        bool mfgExists = await _repo.ManufacturerExistsAsync(dto.ManufacturerId, ct);
        if (!mfgExists)
        {
            throw new KeyNotFoundException("Manufacturer not found");
        }

        Domain.HotendModelDefinition model = new()
        {
            Id = Guid.NewGuid(),
            Name = dto.Name.Trim(),
            ManufacturerId = dto.ManufacturerId,
            MaxTemp = dto.MaxTemp,
            IsHighFlow = dto.IsHighFlow,
            NozzleInterface = dto.NozzleInterface,
            Description = dto.Description,
            Url = dto.Url
        };

        await _repo.AddHotendModelAsync(model, ct);
        _logger.LogInformation("Created hotend model '{ModelName}' with ID {ModelId}", model.Name, model.Id);

        // Fetch with manufacturer info
        Domain.HotendModelDefinition? created = await _repo.GetHotendModelByIdAsync(model.Id, ct);
        return new HotendModelDto(
            created!.Id, created.Name, created.ManufacturerId,
            created.Manufacturer?.Name, created.MaxTemp, created.IsHighFlow,
            created.NozzleInterface, created.Description, created.Url);
    }

    public async Task<HotendModelDto?> UpdateHotendModelAsync(Guid id, UpdateHotendModelDto dto, CancellationToken ct)
    {
        Domain.HotendModelDefinition? model = await _repo.GetHotendModelByIdAsync(id, ct);
        if (model is null)
        {
            return null;
        }

        if (dto.Name is not null)
        {
            model.Name = dto.Name.Trim();
        }

        if (dto.ManufacturerId.HasValue)
        {
            bool mfgExists = await _repo.ManufacturerExistsAsync(dto.ManufacturerId.Value, ct);
            if (!mfgExists)
            {
                throw new KeyNotFoundException("Manufacturer not found");
            }

            model.ManufacturerId = dto.ManufacturerId.Value;
        }

        if (dto.MaxTemp.HasValue)
        {
            model.MaxTemp = dto.MaxTemp;
        }

        if (dto.IsHighFlow.HasValue)
        {
            model.IsHighFlow = dto.IsHighFlow.Value;
        }

        if (dto.NozzleInterface.HasValue)
        {
            model.NozzleInterface = dto.NozzleInterface.Value;
        }

        if (dto.Description is not null)
        {
            model.Description = dto.Description;
        }

        if (dto.Url is not null)
        {
            model.Url = dto.Url;
        }

        await _repo.SaveChangesAsync(ct);
        _logger.LogInformation("Updated hotend model '{ModelName}' with ID {ModelId}", model.Name, model.Id);

        // Re-fetch to get updated manufacturer navigation property
        model = await _repo.GetHotendModelByIdAsync(id, ct);
        return new HotendModelDto(
            model!.Id, model.Name, model.ManufacturerId,
            model.Manufacturer?.Name, model.MaxTemp, model.IsHighFlow,
            model.NozzleInterface, model.Description, model.Url);
    }

    public async Task DeleteHotendModelAsync(Guid id, CancellationToken ct)
    {
        await _repo.RemoveHotendModelAsync(id, ct);
        await _repo.SaveChangesAsync(ct);
        _logger.LogInformation("Deleted hotend model with ID {Id}", id);
    }

    #endregion

    #region Extruder Model CRUD

    public async Task<ExtruderModelDto> CreateExtruderModelAsync(CreateExtruderModelDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            throw new ArgumentException("Name is required", nameof(dto));
        }

        bool mfgExists = await _repo.ManufacturerExistsAsync(dto.ManufacturerId, ct);
        if (!mfgExists)
        {
            throw new KeyNotFoundException("Manufacturer not found");
        }

        Domain.ExtruderModelDefinition model = new()
        {
            Id = Guid.NewGuid(),
            Name = dto.Name.Trim(),
            ManufacturerId = dto.ManufacturerId,
            GearRatio = dto.GearRatio,
            IsDirectDrive = dto.IsDirectDrive,
            Description = dto.Description,
            Url = dto.Url
        };

        await _repo.AddExtruderModelAsync(model, ct);
        _logger.LogInformation("Created extruder model '{ModelName}' with ID {ModelId}", model.Name, model.Id);

        Domain.ExtruderModelDefinition? created = await _repo.GetExtruderModelByIdAsync(model.Id, ct);
        return new ExtruderModelDto(
            created!.Id, created.Name, created.ManufacturerId,
            created.Manufacturer?.Name, created.GearRatio, created.IsDirectDrive,
            created.Description, created.Url);
    }

    public async Task<ExtruderModelDto?> UpdateExtruderModelAsync(Guid id, UpdateExtruderModelDto dto, CancellationToken ct)
    {
        Domain.ExtruderModelDefinition? model = await _repo.GetExtruderModelByIdAsync(id, ct);
        if (model is null)
        {
            return null;
        }

        if (dto.Name is not null)
        {
            model.Name = dto.Name.Trim();
        }

        if (dto.ManufacturerId.HasValue)
        {
            bool mfgExists = await _repo.ManufacturerExistsAsync(dto.ManufacturerId.Value, ct);
            if (!mfgExists)
            {
                throw new KeyNotFoundException("Manufacturer not found");
            }

            model.ManufacturerId = dto.ManufacturerId.Value;
        }

        if (dto.GearRatio is not null)
        {
            model.GearRatio = dto.GearRatio;
        }

        if (dto.IsDirectDrive.HasValue)
        {
            model.IsDirectDrive = dto.IsDirectDrive.Value;
        }

        if (dto.Description is not null)
        {
            model.Description = dto.Description;
        }

        if (dto.Url is not null)
        {
            model.Url = dto.Url;
        }

        await _repo.SaveChangesAsync(ct);
        _logger.LogInformation("Updated extruder model '{ModelName}' with ID {ModelId}", model.Name, model.Id);

        // Re-fetch to get updated manufacturer navigation property
        model = await _repo.GetExtruderModelByIdAsync(id, ct);
        return new ExtruderModelDto(
            model!.Id, model.Name, model.ManufacturerId,
            model.Manufacturer?.Name, model.GearRatio, model.IsDirectDrive,
            model.Description, model.Url);
    }

    public async Task DeleteExtruderModelAsync(Guid id, CancellationToken ct)
    {
        await _repo.RemoveExtruderModelAsync(id, ct);
        await _repo.SaveChangesAsync(ct);
        _logger.LogInformation("Deleted extruder model with ID {Id}", id);
    }

    #endregion

    #region Toolhead Model CRUD

    public async Task<ToolheadModelDto> CreateToolheadModelAsync(CreateToolheadModelDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            throw new ArgumentException("Name is required", nameof(dto));
        }

        bool mfgExists = await _repo.ManufacturerExistsAsync(dto.ManufacturerId, ct);
        if (!mfgExists)
        {
            throw new KeyNotFoundException("Manufacturer not found");
        }

        Domain.ToolheadModelDefinition model = new()
        {
            Id = Guid.NewGuid(),
            Name = dto.Name.Trim(),
            ManufacturerId = dto.ManufacturerId,
            Description = dto.Description,
            Url = dto.Url
        };

        await _repo.AddToolheadModelAsync(model, ct);
        _logger.LogInformation("Created toolhead model '{ModelName}' with ID {ModelId}", model.Name, model.Id);

        Domain.ToolheadModelDefinition? created = await _repo.GetToolheadModelByIdAsync(model.Id, ct);
        return new ToolheadModelDto(
            created!.Id, created.Name, created.ManufacturerId,
            created.Manufacturer?.Name, created.Description, created.Url);
    }

    public async Task<ToolheadModelDto?> UpdateToolheadModelAsync(Guid id, UpdateToolheadModelDefDto dto, CancellationToken ct)
    {
        Domain.ToolheadModelDefinition? model = await _repo.GetToolheadModelByIdAsync(id, ct);
        if (model is null)
        {
            return null;
        }

        if (dto.Name is not null)
        {
            model.Name = dto.Name.Trim();
        }

        if (dto.ManufacturerId.HasValue)
        {
            bool mfgExists = await _repo.ManufacturerExistsAsync(dto.ManufacturerId.Value, ct);
            if (!mfgExists)
            {
                throw new KeyNotFoundException("Manufacturer not found");
            }

            model.ManufacturerId = dto.ManufacturerId.Value;
        }

        if (dto.Description is not null)
        {
            model.Description = dto.Description;
        }

        if (dto.Url is not null)
        {
            model.Url = dto.Url;
        }

        await _repo.SaveChangesAsync(ct);
        _logger.LogInformation("Updated toolhead model '{ModelName}' with ID {ModelId}", model.Name, model.Id);

        // Re-fetch to get updated manufacturer navigation property
        model = await _repo.GetToolheadModelByIdAsync(id, ct);
        return new ToolheadModelDto(
            model!.Id, model.Name, model.ManufacturerId,
            model.Manufacturer?.Name, model.Description, model.Url);
    }

    public async Task DeleteToolheadModelAsync(Guid id, CancellationToken ct)
    {
        await _repo.RemoveToolheadModelAsync(id, ct);
        await _repo.SaveChangesAsync(ct);
        _logger.LogInformation("Deleted toolhead model with ID {Id}", id);
    }

    #endregion

    #region Nozzle Model CRUD

    public async Task<NozzleModelDto> CreateNozzleModelAsync(CreateNozzleModelDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            throw new ArgumentException("Name is required", nameof(dto));
        }

        bool mfgExists = await _repo.ManufacturerExistsAsync(dto.ManufacturerId, ct);
        if (!mfgExists)
        {
            throw new KeyNotFoundException("Manufacturer not found");
        }

        Domain.NozzleModelDefinition model = new()
        {
            Id = Guid.NewGuid(),
            Name = dto.Name.Trim(),
            ManufacturerId = dto.ManufacturerId,
            Diameter = dto.Diameter,
            MaxTemp = dto.MaxTemp,
            NozzleType = dto.NozzleType,
            NozzleInterface = dto.NozzleInterface,
            Description = dto.Description,
            Url = dto.Url
        };

        await _repo.AddNozzleModelAsync(model, ct);
        _logger.LogInformation("Created nozzle model '{ModelName}' with ID {ModelId}", model.Name, model.Id);

        Domain.NozzleModelDefinition? created = await _repo.GetNozzleModelByIdAsync(model.Id, ct);
        return new NozzleModelDto(
            created!.Id, created.Name, created.ManufacturerId,
            created.Manufacturer?.Name, created.Diameter, created.MaxTemp, created.NozzleType, created.IsHardened,
            created.NozzleInterface, created.Description, created.Url);
    }

    public async Task<NozzleModelDto?> UpdateNozzleModelAsync(Guid id, UpdateNozzleModelDto dto, CancellationToken ct)
    {
        Domain.NozzleModelDefinition? model = await _repo.GetNozzleModelByIdAsync(id, ct);
        if (model is null)
        {
            return null;
        }

        if (dto.Name is not null)
        {
            model.Name = dto.Name.Trim();
        }

        if (dto.ManufacturerId.HasValue)
        {
            bool mfgExists = await _repo.ManufacturerExistsAsync(dto.ManufacturerId.Value, ct);
            if (!mfgExists)
            {
                throw new KeyNotFoundException("Manufacturer not found");
            }

            model.ManufacturerId = dto.ManufacturerId.Value;
        }

        if (dto.Diameter.HasValue)
        {
            model.Diameter = dto.Diameter.Value;
        }

        if (dto.MaxTemp.HasValue)
        {
            model.MaxTemp = dto.MaxTemp;
        }

        if (dto.NozzleType.HasValue)
        {
            model.NozzleType = dto.NozzleType.Value;
        }

        if (dto.NozzleInterface.HasValue)
        {
            model.NozzleInterface = dto.NozzleInterface.Value;
        }

        if (dto.Description is not null)
        {
            model.Description = dto.Description;
        }

        if (dto.Url is not null)
        {
            model.Url = dto.Url;
        }

        await _repo.SaveChangesAsync(ct);
        _logger.LogInformation("Updated nozzle model '{ModelName}' with ID {ModelId}", model.Name, model.Id);

        // Re-fetch to get updated manufacturer navigation property
        model = await _repo.GetNozzleModelByIdAsync(id, ct);
        return new NozzleModelDto(
            model!.Id, model.Name, model.ManufacturerId,
            model.Manufacturer?.Name, model.Diameter, model.MaxTemp, model.NozzleType, model.IsHardened,
            model.NozzleInterface, model.Description, model.Url);
    }

    public async Task DeleteNozzleModelAsync(Guid id, CancellationToken ct)
    {
        await _repo.RemoveNozzleModelAsync(id, ct);
        await _repo.SaveChangesAsync(ct);
        _logger.LogInformation("Deleted nozzle model with ID {Id}", id);
    }

    #endregion

    #region Contextual Manufacturer Methods

    public async Task<ManufacturersByContextDto> GetManufacturersByContextAsync(CatalogContext context, CancellationToken ct)
    {
        IReadOnlyList<(Guid Id, string Name, string? Url, string? Description)> manufacturers = await _repo.GetManufacturersAsync(ct);

        List<ManufacturerWithCountDto> withItems = [];
        List<ManufacturerWithCountDto> withoutItems = [];

        foreach ((Guid Id, string Name, string? Url, string? Description) mfg in manufacturers)
        {
            int count = context switch
            {
                CatalogContext.Printers => await _repo.CountPrinterModelsByManufacturerAsync(mfg.Id, ct),
                CatalogContext.Hotends => await _repo.CountHotendModelsByManufacturerAsync(mfg.Id, ct),
                CatalogContext.Extruders => await _repo.CountExtruderModelsByManufacturerAsync(mfg.Id, ct),
                CatalogContext.Toolheads => await _repo.CountToolheadModelsByManufacturerAsync(mfg.Id, ct),
                CatalogContext.Nozzles => await _repo.CountNozzleModelsByManufacturerAsync(mfg.Id, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(context))
            };

            ManufacturerWithCountDto dto = new(mfg.Id, mfg.Name, count);
            if (count > 0)
            {
                withItems.Add(dto);
            }
            else
            {
                withoutItems.Add(dto);
            }
        }

        return new ManufacturersByContextDto(withItems, withoutItems);
    }

    #endregion
}
