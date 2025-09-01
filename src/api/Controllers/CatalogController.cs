using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for managing printer manufacturer and model catalog data.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class CatalogController(AppDbContext db) : ControllerBase
{
    /// <summary>
    /// Gets all available printer manufacturers.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of all printer manufacturers ordered by name</returns>
    /// <response code="200">Returns the list of manufacturers</response>
    [HttpGet("manufacturers")]
    public async Task<ActionResult<IEnumerable<ManufacturerDto>>> GetManufacturersAsync(CancellationToken ct)
    {
        var list = await db.Manufacturers.AsNoTracking().OrderBy(m => m.Name)
            .Select(m => new ManufacturerDto(m.Id, m.Name)).ToListAsync(ct);
        return Ok(list);
    }

    /// <summary>
    /// Creates a new printer manufacturer.
    /// </summary>
    /// <param name="name">The name of the manufacturer to create</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>The created manufacturer</returns>
    /// <response code="201">Returns the newly created manufacturer</response>
    /// <response code="400">If the manufacturer name is invalid or empty</response>
    /// <response code="409">If a manufacturer with the same name already exists</response>
    [HttpPost("manufacturers")]
    public async Task<ActionResult<ManufacturerDto>> CreateManufacturerAsync([FromBody] string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest("Name is required");
        }

        var trimmed = name.Trim();
        var existing = await db.Manufacturers.AsNoTracking().FirstOrDefaultAsync(m => m.Name == trimmed, ct);
        if (existing is not null)
        {
            return Conflict(new ManufacturerDto(existing.Id, existing.Name));
        }

        var mfg = new Manufacturer { Id = Guid.NewGuid(), Name = trimmed };
        db.Manufacturers.Add(mfg);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(GetManufacturersAsync), new { id = mfg.Id }, new ManufacturerDto(mfg.Id, mfg.Name));
    }

    [HttpGet("models")]
    public async Task<ActionResult<IEnumerable<ModelDto>>> GetModelsAsync([FromQuery] Guid? manufacturerId, CancellationToken ct)
    {
        var q = db.Models.AsNoTracking().AsQueryable();
        if (manufacturerId is Guid mid)
        {
            q = q.Where(m => m.ManufacturerId == mid);
        }

        var list = await q.OrderBy(m => m.Name)
            .Select(m => new ModelDto(m.Id, m.Name, m.ManufacturerId, m.MaxX, m.MaxY, m.MaxZ,
                m.DefaultBackend.HasValue ? (PrinterBackend)m.DefaultBackend.Value : (PrinterBackend?)null)).ToListAsync(ct);
        return Ok(list);
    }

    public record CreateModelRequest(Guid ManufacturerId, string Name, double? MaxX, double? MaxY, double? MaxZ, PrinterBackend? DefaultBackend);

    [HttpPost("models")]
    public async Task<ActionResult<ModelDto>> CreateModelAsync([FromBody] CreateModelRequest req, CancellationToken ct)
    {
        if (req.ManufacturerId == Guid.Empty)
        {
            return BadRequest("ManufacturerId is required");
        }

        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return BadRequest("Name is required");
        }
        // Ensure the manufacturer exists to avoid FK violations
        var mfgExists = await db.Manufacturers.AsNoTracking().AnyAsync(m => m.Id == req.ManufacturerId, ct);
        if (!mfgExists)
        {
            return NotFound("Manufacturer not found");
        }

        var trimmed = req.Name.Trim();
        var existing = await db.Models.AsNoTracking().FirstOrDefaultAsync(m => m.ManufacturerId == req.ManufacturerId && m.Name == trimmed, ct);
        if (existing is not null)
        {
            return Conflict(new ModelDto(existing.Id, existing.Name, existing.ManufacturerId, existing.MaxX, existing.MaxY, existing.MaxZ,
                existing.DefaultBackend.HasValue ? (PrinterBackend)existing.DefaultBackend.Value : (PrinterBackend?)null));
        }

        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            ManufacturerId = req.ManufacturerId,
            Name = trimmed,
            MaxX = req.MaxX,
            MaxY = req.MaxY,
            MaxZ = req.MaxZ,
            DefaultBackend = req.DefaultBackend.HasValue ? (int)req.DefaultBackend.Value : (int?)null
        };
        db.Models.Add(model);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException se && se.SqliteErrorCode == 19)
        {
            // Likely a FK or unique constraint violation
            return BadRequest("Invalid request: constraint failed (check ManufacturerId and uniqueness).");
        }
        return CreatedAtAction(nameof(GetModelsAsync), new { id = model.Id }, new ModelDto(model.Id, model.Name, model.ManufacturerId, model.MaxX, model.MaxY, model.MaxZ,
                model.DefaultBackend.HasValue ? (PrinterBackend)model.DefaultBackend.Value : (PrinterBackend?)null));
    }

    public record UpdateModelRequest(string Name, double? MaxX, double? MaxY, double? MaxZ, PrinterBackend? DefaultBackend);

    [HttpPut("models/{id:guid}")]
    public async Task<IActionResult> UpdateModelAsync(Guid id, [FromBody] UpdateModelRequest req, CancellationToken ct)
    {
        var model = await db.Models.FindAsync([id], ct);
        if (model is null)
        {
            return NotFound();
        }

        if (!string.IsNullOrWhiteSpace(req.Name))
        {
            model.Name = req.Name.Trim();
        }

        model.MaxX = req.MaxX;
        model.MaxY = req.MaxY;
        model.MaxZ = req.MaxZ;
        model.DefaultBackend = req.DefaultBackend.HasValue ? (int)req.DefaultBackend.Value : (int?)null;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

}
