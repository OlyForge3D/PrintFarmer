using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Api.Infrastructure.Caching;
using Farm.Web.Api.Infrastructure.Exceptions;
using Farm.Web.Api.Infrastructure.Normalization;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for managing printer manufacturer and model catalog data.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Tags("Catalog")]
public class CatalogController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly INormalizationEventLogger _normLogger;
    private readonly ICatalogCache _catalogCache;

    public CatalogController(AppDbContext db, INormalizationEventLogger normLogger, ICatalogCache catalogCache)
    {
        _db = db;
        _normLogger = normLogger;
        _catalogCache = catalogCache;
    }
    /// <summary>
    /// Gets all available printer manufacturers.
    /// </summary>
    /// <param name="ifNoneMatch">Optional ETag for conditional GET</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of all printer manufacturers ordered by name</returns>
    /// <response code="200">Returns the list of manufacturers</response>
    [HttpGet("manufacturers")]
    [ProducesResponseType(typeof(IEnumerable<ManufacturerDto>), 200)]
    [ProducesResponseType(304)]
    public async Task<ActionResult<IEnumerable<ManufacturerDto>>> GetManufacturersAsync([FromHeader(Name = "If-None-Match")] string? ifNoneMatch, CancellationToken ct)
    {
        var (list, etag) = await _catalogCache.GetManufacturersAsync(ct);
        if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch.Split(',').Select(s => s.Trim()).Contains(etag, StringComparer.Ordinal))
        {
            Response.Headers["ETag"] = etag;
            return StatusCode(StatusCodes.Status304NotModified);
        }
        Response.Headers["ETag"] = etag;
        return Ok(list);
    }

    [HttpGet("manufacturers/{id:guid}", Name = "GetManufacturerById")]
    [ProducesResponseType(typeof(ManufacturerDto), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ManufacturerDto>> GetManufacturerByIdAsync(Guid id, CancellationToken ct)
    {
        var m = await _db.Manufacturers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null)
        {
            return NotFound();
        }
        return Ok(new ManufacturerDto(m.Id, m.Name));
    }

    /// <summary>
    /// Creates a new printer manufacturer.
    /// </summary>
    /// <param name="request">Payload containing the manufacturer name</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>The created manufacturer</returns>
    /// <response code="201">Returns the newly created manufacturer</response>
    /// <response code="400">If the manufacturer name is invalid or empty</response>
    /// <response code="409">If a manufacturer with the same name already exists</response>
    [HttpPost("manufacturers")]
    [ProducesResponseType(typeof(ManufacturerDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
    public async Task<ActionResult<ManufacturerDto>> CreateManufacturerAsync([FromBody] CreateManufacturerRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Name is required");
        }
        // Normalize via shared helper for consistent rule across API & seeding
        var original = request.Name; // already validated not null/whitespace
        var normalized = CatalogNameNormalizer.NormalizeManufacturer(original);
        _normLogger.Log("Manufacturer", original, normalized, "create");

        // Case-insensitive uniqueness check (small table => safe to load into memory once)
        var manufacturerRows = await _db.Manufacturers.AsNoTracking()
            .Select(m => new { m.Id, m.Name })
            .ToListAsync(ct);
        var existing = manufacturerRows.Find(r => string.Equals(r.Name, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            string? headerName = null;
            if (!string.Equals(original.Trim(), existing.Name, StringComparison.Ordinal))
            {
                headerName = existing.Name; // header will be set by exception filter
            }
            throw new DuplicateEntityException("Manufacturer", new ManufacturerDto(existing.Id, existing.Name), headerName,
                $"A manufacturer with the normalized name '{existing.Name}' already exists.");
        }

        var mfg = new Manufacturer { Id = Guid.NewGuid(), Name = normalized };
        _db.Manufacturers.Add(mfg);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraint(ex))
        {
            // Race: another request inserted same name (case-insensitive). Surface existing via exception.
            var existingNow = await _db.Manufacturers.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Name == normalized, ct) ?? new Manufacturer { Id = mfg.Id, Name = normalized };
            throw new DuplicateEntityException("Manufacturer", new ManufacturerDto(existingNow.Id, existingNow.Name), null,
                $"A manufacturer with the normalized name '{existingNow.Name}' already exists.");
        }
        // Emit normalization header if canonical name differs in ANY way from raw input (including whitespace)
        if (!string.Equals(original, mfg.Name, StringComparison.Ordinal))
        {
            Response.Headers["X-Normalized-Name"] = mfg.Name;
        }
        _catalogCache.InvalidateManufacturers();
        _catalogCache.InvalidateModels();
        return CreatedAtRoute("GetManufacturerById", new { id = mfg.Id }, new ManufacturerDto(mfg.Id, mfg.Name));
    }

    [HttpGet("printer-models")]
    [ProducesResponseType(typeof(IEnumerable<PrinterModelDto>), 200)]
    [ProducesResponseType(304)]
    public async Task<ActionResult<IEnumerable<PrinterModelDto>>> GetPrinterModelsAsync([FromQuery] Guid? manufacturerId, [FromHeader(Name = "If-None-Match")] string? ifNoneMatch, CancellationToken ct)
    {
        var (list, etag) = await _catalogCache.GetModelsAsync(manufacturerId, ct);
        if (!string.IsNullOrEmpty(ifNoneMatch))
        {
            var clientEtags = ifNoneMatch.Split(',').Select(s => s.Trim()).ToHashSet(StringComparer.Ordinal);
            if (clientEtags.Contains(etag))
            {
                Response.Headers["ETag"] = etag;
                return StatusCode(StatusCodes.Status304NotModified);
            }
        }
        Response.Headers["ETag"] = etag;
        return Ok(list);
    }

    [HttpGet("printer-models/{id:guid}", Name = "GetPrinterModelById")]
    [ProducesResponseType(typeof(PrinterModelDto), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<PrinterModelDto>> GetPrinterModelByIdAsync(Guid id, CancellationToken ct)
    {
        var model = await _db.Models.AsNoTracking().Include(m => m.SupportedFilamentTypes).ThenInclude(sf => sf.FilamentType)
            .FirstOrDefaultAsync(m => m.Id == id, ct);
        if (model is null)
        {
            return NotFound();
        }
        return Ok(new PrinterModelDto(model.Id, model.Name, model.ManufacturerId, model.MaxX, model.MaxY, model.MaxZ,
            model.DefaultBackend.HasValue ? (PrinterBackend)model.DefaultBackend.Value : (PrinterBackend?)null,
            [.. model.SupportedFilamentTypes.Select(sf => sf.FilamentType!.Name)]));
    }

    [HttpPost("printer-models")]
    [ProducesResponseType(typeof(PrinterModelDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    public async Task<ActionResult<PrinterModelDto>> CreatePrinterModelAsync([FromBody] CreateModelRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.ManufacturerId == Guid.Empty)
        {
            return BadRequest("ManufacturerId is required");
        }

        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return BadRequest("Name is required");
        }
        var originalModelName = req.Name; // validated earlier
        var normalizedName = CatalogNameNormalizer.NormalizeModel(originalModelName);
        _normLogger.Log("Model", originalModelName, normalizedName, "create");
        // Ensure the manufacturer exists to avoid FK violations
        var mfgExists = await _db.Manufacturers.AsNoTracking().AnyAsync(m => m.Id == req.ManufacturerId, ct);
        if (!mfgExists)
        {
            return NotFound("Manufacturer not found");
        }
        // Case-insensitive uniqueness within the same manufacturer.
        // Translation constraints: EF Core (SQLite) cannot translate string.Equals with StringComparison nor
        // ToUpperInvariant()/ToLowerInvariant(). Rather than rely on ToUpper()/ToLower() (which triggers analyzers
        // for culture concerns), we pull the small candidate set (models for this manufacturer) and compare in-memory.
        // Manufacturer-level model counts are expected to be small; if this becomes hot, consider a computed
        // normalized column or a case-insensitive unique index at the database layer.
        var candidateNames = await _db.Models.AsNoTracking()
            .Where(m => m.ManufacturerId == req.ManufacturerId)
            .Select(m => new { m.Id, m.ManufacturerId, m.Name, m.MaxX, m.MaxY, m.MaxZ, m.DefaultBackend })
            .ToListAsync(ct);
        var existing = candidateNames.Find(m => string.Equals(m.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            string? headerName = null;
            if (!string.Equals(originalModelName.Trim(), existing.Name, StringComparison.Ordinal))
            {
                headerName = existing.Name;
            }
            throw new DuplicateEntityException("Model", new PrinterModelDto(existing.Id, existing.Name, existing.ManufacturerId, existing.MaxX, existing.MaxY, existing.MaxZ,
                existing.DefaultBackend.HasValue ? (PrinterBackend)existing.DefaultBackend.Value : (PrinterBackend?)null), headerName,
                $"A model with the normalized name '{existing.Name}' already exists for this manufacturer.");
        }

        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            ManufacturerId = req.ManufacturerId,
            Name = normalizedName,
            MaxX = req.MaxX,
            MaxY = req.MaxY,
            MaxZ = req.MaxZ,
            DefaultBackend = req.DefaultBackend.HasValue ? (int)req.DefaultBackend.Value : (int?)null
        };
        _db.Models.Add(model);

        // Add supported filament types if provided
        if (req.SupportedFilamentTypeIds?.Length > 0)
        {
            var validFilamentTypeIds = await _db.FilamentTypes.AsNoTracking()
                .Where(f => req.SupportedFilamentTypeIds.Contains(f.Id))
                .Select(f => f.Id)
                .ToListAsync(ct);

            foreach (var filamentTypeId in validFilamentTypeIds)
            {
                _db.PrinterModelFilamentTypes.Add(new PrinterModelFilamentType
                {
                    PrinterModelId = model.Id,
                    FilamentTypeId = filamentTypeId
                });
            }
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraint(ex))
        {
            var existingNow = await _db.Models.AsNoTracking()
                .FirstOrDefaultAsync(m => m.ManufacturerId == req.ManufacturerId && m.Name == normalizedName, ct) ?? new PrinterModel { Id = model.Id, Name = normalizedName, ManufacturerId = req.ManufacturerId };
            throw new DuplicateEntityException("Model", new PrinterModelDto(existingNow.Id, existingNow.Name, existingNow.ManufacturerId, existingNow.MaxX, existingNow.MaxY, existingNow.MaxZ,
                existingNow.DefaultBackend.HasValue ? (PrinterBackend)existingNow.DefaultBackend.Value : (PrinterBackend?)null), null,
                $"A model with the normalized name '{existingNow.Name}' already exists for this manufacturer.");
        }

        // Load the model with filament types for response
        var createdModel = await _db.Models.AsNoTracking()
            .Include(m => m.SupportedFilamentTypes).ThenInclude(sf => sf.FilamentType)
            .FirstOrDefaultAsync(m => m.Id == model.Id, ct);
        if (!string.Equals(originalModelName, model.Name, StringComparison.Ordinal))
        {
            Response.Headers["X-Normalized-Name"] = model.Name;
        }
        _catalogCache.InvalidateModels(model.ManufacturerId);
        return CreatedAtRoute("GetPrinterModelById", new { id = model.Id }, new PrinterModelDto(model.Id, model.Name, model.ManufacturerId, model.MaxX, model.MaxY, model.MaxZ,
                    model.DefaultBackend.HasValue ? (PrinterBackend)model.DefaultBackend.Value : (PrinterBackend?)null,
                    createdModel?.SupportedFilamentTypes.Select(sf => sf.FilamentType!.Name).ToArray()));
    }

    [HttpPut("models/{id:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> UpdateModelAsync(Guid id, [FromBody] UpdateModelRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var model = await _db.Models.Include(m => m.SupportedFilamentTypes).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (model is null)
        {
            return NotFound();
        }

        if (!string.IsNullOrWhiteSpace(req.Name))
        {
            var before = model.Name;
            var after = CatalogNameNormalizer.NormalizeModel(req.Name);
            model.Name = after;
            _normLogger.Log("Model", before, after, "update");
            if (!string.Equals(before, after, StringComparison.Ordinal))
            {
                Response.Headers["X-Normalized-Name"] = after;
            }
        }

        model.MaxX = req.MaxX;
        model.MaxY = req.MaxY;
        model.MaxZ = req.MaxZ;
        model.DefaultBackend = req.DefaultBackend.HasValue ? (int)req.DefaultBackend.Value : (int?)null;

        // Update supported filament types
        if (req.SupportedFilamentTypeIds != null)
        {
            // Remove existing relationships
            _db.PrinterModelFilamentTypes.RemoveRange(model.SupportedFilamentTypes);

            // Add new relationships
            if (req.SupportedFilamentTypeIds.Length > 0)
            {
                var validFilamentTypeIds = await _db.FilamentTypes.AsNoTracking()
                    .Where(f => req.SupportedFilamentTypeIds.Contains(f.Id))
                    .Select(f => f.Id)
                    .ToListAsync(ct);

                foreach (var filamentTypeId in validFilamentTypeIds)
                {
                    _db.PrinterModelFilamentTypes.Add(new PrinterModelFilamentType
                    {
                        PrinterModelId = model.Id,
                        FilamentTypeId = filamentTypeId
                    });
                }
            }
        }
        await _db.SaveChangesAsync(ct);
        _catalogCache.InvalidateModels(model.ManufacturerId);
        return NoContent();
    }

    // ETag computation moved into CatalogCache

    private static bool IsUniqueConstraint(DbUpdateException ex)
    {
        // SQLite constraint
        if (ex.InnerException is Microsoft.Data.Sqlite.SqliteException se && se.SqliteErrorCode == 19)
        {
            return true; // generic constraint failed (unique / FK). Specific name not exposed here.
        }
#if NET8_0_OR_GREATER
        if (ex.InnerException is System.Data.Common.DbException dbx)
        {
            var typeName = dbx.GetType().FullName ?? string.Empty;
            if (typeName.Contains("SqlException", StringComparison.OrdinalIgnoreCase) && dbx.ErrorCode is 2601 or 2627)
            {
                return true; // SQL Server duplicate key (unique index or constraint)
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
        // Fallback: inspect message text for our known index names
        var msg = ex.InnerException?.Message ?? ex.Message;
        if (!string.IsNullOrEmpty(msg) && (msg.Contains("NameLowered", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("IX_Manufacturers_NameLowered", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("IX_Models_ManufacturerId_NameLowered", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        return false;
    }
}
