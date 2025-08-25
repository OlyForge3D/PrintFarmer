using Farm.Web.Server.Data;
using Farm.Web.Server.Domain;
using Farm.Web.Server.Services;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Farm.Web.Server.Controllers.CatalogController;

namespace Farm.Web.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PrintersController(AppDbContext db, MoonrakerClient moon) : ControllerBase
{
    private static string NormalizeMoonrakerUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        var trimmed = url.Trim();
        // Ensure scheme
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }
        try
        {
            var ub = new UriBuilder(trimmed);
            if (ub.Port == -1)
            {
                ub.Port = 7125;
            }
            return ub.Uri.ToString().TrimEnd('/');
        }
        catch
        {
            // If parsing fails, fall back to original input
            return url;
        }
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<PrinterDto>>> GetAll(CancellationToken ct)
    {
        var items = await db.Printers.AsNoTracking().Include(p => p.Manufacturer).Include(p => p.Model).ToListAsync(ct);
        var dtos = await Task.WhenAll(items.Select(async p =>
        {
            var status = await moon.GetCompositeStatusAsync(p.MoonrakerUrl, ct);
            return new PrinterDto(
                p.Id,
                p.Name,
                p.MoonrakerUrl,
                p.Notes,
                status.IsOnline,
                status.State,
                p.Manufacturer?.Name,
                p.Model?.Name,
                status.Progress,
                status.JobName,
                status.ThumbnailUrl,
                status.CameraStreamUrl,
                status.X,
                status.Y,
                status.Z,
                status.HotendTemp,
                status.BedTemp,
                status.HotendTarget,
                status.BedTarget);
        }));
        return Ok(dtos);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PrinterDto>> Get(Guid id, CancellationToken ct)
    {
    var p = await db.Printers.Include(x => x.Manufacturer).Include(x => x.Model).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
    var status = await moon.GetCompositeStatusAsync(p.MoonrakerUrl, ct);
    return new PrinterDto(
        p.Id,
        p.Name,
        p.MoonrakerUrl,
        p.Notes,
        status.IsOnline,
        status.State,
        p.Manufacturer?.Name,
        p.Model?.Name,
        status.Progress,
        status.JobName,
        status.ThumbnailUrl,
        status.CameraStreamUrl,
        status.X,
        status.Y,
    status.Z,
    status.HotendTemp,
    status.BedTemp,
    status.HotendTarget,
    status.BedTarget);
    }

    [HttpGet("{id:guid}/details")]
    public async Task<ActionResult<PrinterDetailsDto>> GetDetails(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.AsNoTracking().Include(x => x.Manufacturer).Include(x => x.Model).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        return new PrinterDetailsDto(
            p.Id,
            p.Name,
            p.MoonrakerUrl,
            p.Notes,
            p.ManufacturerId,
            p.Manufacturer?.Name,
            p.ModelId,
            p.Model?.Name,
            p.Model?.MaxX,
            p.Model?.MaxY,
            p.Model?.MaxZ,
            p.DateAcquired
        );
    }

    [HttpPost]
    public async Task<ActionResult<PrinterDto>> Create(CreatePrinterDto dto, CancellationToken ct)
    {
        // resolve or create manufacturer/model
        Guid? manufacturerId = dto.ManufacturerId;
        if (manufacturerId is null && !string.IsNullOrWhiteSpace(dto.NewManufacturerName))
        {
            var name = dto.NewManufacturerName!.Trim();
            var existing = await db.Manufacturers.FirstOrDefaultAsync(m => m.Name == name, ct);
            if (existing is null)
            {
                existing = new Manufacturer { Id = Guid.NewGuid(), Name = name };
                db.Manufacturers.Add(existing);
                await db.SaveChangesAsync(ct);
            }
            manufacturerId = existing.Id;
        }

        Guid? modelId = dto.ModelId;
        if (modelId is null && !string.IsNullOrWhiteSpace(dto.NewModelName) && manufacturerId is Guid mid)
        {
            var mname = dto.NewModelName!.Trim();
            var existingModel = await db.Models.FirstOrDefaultAsync(m => m.ManufacturerId == mid && m.Name == mname, ct);
            if (existingModel is null)
            {
                existingModel = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mid, Name = mname };
                db.Models.Add(existingModel);
                await db.SaveChangesAsync(ct);
            }
            modelId = existingModel.Id;
        }

    var p = new Printer {
        Id = Guid.NewGuid(),
        Name = dto.Name,
        MoonrakerUrl = NormalizeMoonrakerUrl(dto.MoonrakerUrl),
        Notes = dto.Notes,
        ManufacturerId = manufacturerId,
        ModelId = modelId,
        DateAcquired = dto.DateAcquired
    };
        db.Printers.Add(p);
        await db.SaveChangesAsync(ct);
    var status = await moon.GetCompositeStatusAsync(p.MoonrakerUrl, ct);
    return CreatedAtAction(nameof(Get), new { id = p.Id }, new PrinterDto(
        p.Id,
        p.Name,
        p.MoonrakerUrl,
        p.Notes,
        status.IsOnline,
        status.State,
        null,
        null,
        status.Progress,
        status.JobName,
        status.ThumbnailUrl,
        status.CameraStreamUrl,
        status.X,
        status.Y,
    status.Z,
    status.HotendTemp,
    status.BedTemp,
    status.HotendTarget,
    status.BedTarget));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdatePrinterDto dto, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        // resolve or create manufacturer/model
        Guid? manufacturerId = dto.ManufacturerId ?? p.ManufacturerId;
        if (dto.ManufacturerId is null && !string.IsNullOrWhiteSpace(dto.NewManufacturerName))
        {
            var name = dto.NewManufacturerName!.Trim();
            var existing = await db.Manufacturers.FirstOrDefaultAsync(m => m.Name == name, ct);
            if (existing is null)
            {
                existing = new Manufacturer { Id = Guid.NewGuid(), Name = name };
                db.Manufacturers.Add(existing);
                await db.SaveChangesAsync(ct);
            }
            manufacturerId = existing.Id;
        }

        Guid? modelId = dto.ModelId ?? p.ModelId;
        if ((dto.ModelId is null && !string.IsNullOrWhiteSpace(dto.NewModelName)) && manufacturerId is Guid mid)
        {
            var mname = dto.NewModelName!.Trim();
            var existingModel = await db.Models.FirstOrDefaultAsync(m => m.ManufacturerId == mid && m.Name == mname, ct);
            if (existingModel is null)
            {
                existingModel = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mid, Name = mname };
                db.Models.Add(existingModel);
                await db.SaveChangesAsync(ct);
            }
            modelId = existingModel.Id;
        }

        p.Name = dto.Name;
        p.MoonrakerUrl = NormalizeMoonrakerUrl(dto.MoonrakerUrl);
        p.Notes = dto.Notes;
        p.ManufacturerId = manufacturerId;
    p.ModelId = modelId;
        p.DateAcquired = dto.DateAcquired;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        db.Printers.Remove(p);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/home")]
    public async Task<ActionResult<CommandResult>> Home(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        var ok = await moon.SendHomeAsync(p.MoonrakerUrl, ct);
        return new CommandResult(ok, ok ? null : "Failed to send home command");
    }

    [HttpPost("{id:guid}/homexy")]
    public async Task<ActionResult<CommandResult>> HomeXY(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        var ok = await moon.HomeXYAsync(p.MoonrakerUrl, ct);
        return new CommandResult(ok, ok ? null : "Failed to home XY");
    }

    [HttpPost("{id:guid}/homez")]
    public async Task<ActionResult<CommandResult>> HomeZ(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        var ok = await moon.HomeZAsync(p.MoonrakerUrl, ct);
        return new CommandResult(ok, ok ? null : "Failed to home Z");
    }

    [HttpPost("{id:guid}/temps")]
    public async Task<ActionResult<CommandResult>> SetTemps(Guid id, TempTargets targets, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        var ok = await moon.SetTempsAsync(p.MoonrakerUrl, targets.Hotend, targets.Bed, ct);
        return new CommandResult(ok, ok ? null : "Failed to set temperatures");
    }

    [HttpPost("{id:guid}/move")]
    public async Task<ActionResult<CommandResult>> Move(Guid id, MoveRequest req, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        var ok = await moon.MoveAsync(p.MoonrakerUrl, req.X, req.Y, req.Z, req.F, ct);
        return new CommandResult(ok, ok ? null : "Failed to move");
    }

    [HttpPost("{id:guid}/moveto")]
    public async Task<ActionResult<CommandResult>> MoveTo(Guid id, MoveRequest req, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        var ok = await moon.MoveToAsync(p.MoonrakerUrl, req.X, req.Y, req.Z, req.F, ct);
        return new CommandResult(ok, ok ? null : "Failed to move to position");
    }

    [HttpPost("{id:guid}/pause")]
    public async Task<ActionResult<CommandResult>> Pause(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        var ok = await moon.PauseAsync(p.MoonrakerUrl, ct);
        return new CommandResult(ok, ok ? null : "Failed to pause");
    }

    [HttpPost("{id:guid}/resume")]
    public async Task<ActionResult<CommandResult>> Resume(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        var ok = await moon.ResumeAsync(p.MoonrakerUrl, ct);
        return new CommandResult(ok, ok ? null : "Failed to resume");
    }

    [HttpPost("{id:guid}/emergency-stop")]
    public async Task<ActionResult<CommandResult>> EmergencyStop(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        var ok = await moon.EmergencyStopAsync(p.MoonrakerUrl, ct);
        return new CommandResult(ok, ok ? null : "Failed to emergency stop");
    }
}
