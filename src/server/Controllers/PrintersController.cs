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
public class PrintersController(AppDbContext db, MoonrakerClient moon, PrusaLinkClient prusa) : ControllerBase
{
    private static string EnsureLocalSuffix(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return host;
        if (System.Net.IPAddress.TryParse(host, out _)) return host;
        if (host.Contains('.')) return host;
        return host + ".local";
    }
    private static string NormalizeServerUrl(string url, int defaultPort)
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
                ub.Port = defaultPort;
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
            if (p.Backend == 1) // PrusaLink
            {
                var status = await prusa.GetCompositeStatusAsync(p.ServerUrl, p.ApiKey, ct);
                return new PrinterDto(
                    Id: p.Id,
                    Name: p.Name,
                    ServerUrl: p.ServerUrl,
                    Notes: p.Notes,
                    IsOnline: status.IsOnline,
                    State: status.State,
                    ManufacturerName: p.Manufacturer?.Name,
                    ModelName: p.Model?.Name,
                    Progress: status.Progress,
                    JobName: status.JobName,
                    ThumbnailUrl: status.ThumbnailUrl,
                    CameraStreamUrl: status.CameraStreamUrl,
                    CameraSnapshotUrl: status.CameraSnapshotUrl,
                    Backend: Farm.Web.Shared.PrinterBackend.PrusaLink,
                    ApiKey: p.ApiKey,
                    OriginalServerUrl: p.OriginalServerUrl,
                    IpAddress: p.IpAddress
                );
            }
            else // Moonraker
            {
                var status = await moon.GetCompositeStatusAsync(p.ServerUrl, ct);
                return new PrinterDto(
                    Id: p.Id,
                    Name: p.Name,
                    ServerUrl: p.ServerUrl,
                    Notes: p.Notes,
                    IsOnline: status.IsOnline,
                    State: status.State,
                    ManufacturerName: p.Manufacturer?.Name,
                    ModelName: p.Model?.Name,
                    Progress: status.Progress,
                    JobName: status.JobName,
                    ThumbnailUrl: status.ThumbnailUrl,
                    CameraStreamUrl: status.CameraStreamUrl,
                    CameraSnapshotUrl: status.CameraSnapshotUrl,
                    X: status.X,
                    Y: status.Y,
                    Z: status.Z,
                    HotendTemp: status.HotendTemp,
                    BedTemp: status.BedTemp,
                    HotendTarget: status.HotendTarget,
                    BedTarget: status.BedTarget,
                    Backend: Farm.Web.Shared.PrinterBackend.Moonraker,
                    ApiKey: p.ApiKey,
                    OriginalServerUrl: p.OriginalServerUrl,
                    IpAddress: p.IpAddress
                );
            }
        }));
        return Ok(dtos);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PrinterDto>> Get(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.Include(x => x.Manufacturer).Include(x => x.Model).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        if (p.Backend == 1) // PrusaLink
        {
            var status = await prusa.GetCompositeStatusAsync(p.ServerUrl, p.ApiKey, ct);
            return new PrinterDto(
                Id: p.Id,
                Name: p.Name,
                ServerUrl: p.ServerUrl,
                Notes: p.Notes,
                IsOnline: status.IsOnline,
                State: status.State,
                ManufacturerName: p.Manufacturer?.Name,
                ModelName: p.Model?.Name,
                Progress: status.Progress,
                JobName: status.JobName,
                ThumbnailUrl: status.ThumbnailUrl,
                CameraStreamUrl: status.CameraStreamUrl,
                CameraSnapshotUrl: status.CameraSnapshotUrl,
                Backend: Farm.Web.Shared.PrinterBackend.PrusaLink,
                ApiKey: p.ApiKey,
                OriginalServerUrl: p.OriginalServerUrl,
                IpAddress: p.IpAddress
            );
        }
        else // Moonraker
        {
            var status = await moon.GetCompositeStatusAsync(p.ServerUrl, ct);
            return new PrinterDto(
                Id: p.Id,
                Name: p.Name,
                ServerUrl: p.ServerUrl,
                Notes: p.Notes,
                IsOnline: status.IsOnline,
                State: status.State,
                ManufacturerName: p.Manufacturer?.Name,
                ModelName: p.Model?.Name,
                Progress: status.Progress,
                JobName: status.JobName,
                ThumbnailUrl: status.ThumbnailUrl,
                CameraStreamUrl: status.CameraStreamUrl,
                CameraSnapshotUrl: status.CameraSnapshotUrl,
                X: status.X,
                Y: status.Y,
                Z: status.Z,
                HotendTemp: status.HotendTemp,
                BedTemp: status.BedTemp,
                HotendTarget: status.HotendTarget,
                BedTarget: status.BedTarget,
                Backend: Farm.Web.Shared.PrinterBackend.Moonraker,
                ApiKey: p.ApiKey,
                OriginalServerUrl: p.OriginalServerUrl,
                IpAddress: p.IpAddress
            );
        }
    }

    [HttpGet("{id:guid}/details")]
    public async Task<ActionResult<PrinterDetailsDto>> GetDetails(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.AsNoTracking().Include(x => x.Manufacturer).Include(x => x.Model).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        return new PrinterDetailsDto(
            p.Id,
            p.Name,
            p.ServerUrl,
            p.Notes,
        p.ManufacturerId,
        p.Manufacturer?.Name,
        p.ModelId,
        p.Model?.Name,
        p.Model?.MaxX,
        p.Model?.MaxY,
        p.Model?.MaxZ,
        p.DateAcquired,
        (PrinterBackend)p.Backend,
        p.ApiKey,
        p.OriginalServerUrl,
        p.IpAddress
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

        // Resolve host to IP and persist the IP-based base URL; store original URL for future re-resolve
        var defaultPort = dto.Backend == PrinterBackend.PrusaLink ? 80 : 7125;
        var normalizedInput = NormalizeServerUrl(dto.ServerUrl, defaultPort);
        string resolvedBase = normalizedInput;
        string? resolvedIp = null;
        try
        {
            var uri = new Uri(normalizedInput);
            if (!System.Net.IPAddress.TryParse(uri.Host, out _))
            {
                var hostToResolve = EnsureLocalSuffix(uri.Host);
                var addresses = await System.Net.Dns.GetHostAddressesAsync(hostToResolve, ct);
                var firstIp = Array.Find(addresses, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
                if (firstIp is not null)
                {
                    var ub = new UriBuilder(uri)
                    {
                        Host = firstIp.ToString()
                    };
                    resolvedBase = ub.Uri.ToString().TrimEnd('/');
                    resolvedIp = firstIp.ToString();
                }
            }
            else
            {
                resolvedIp = uri.Host;
            }
        }
        catch { }

        var p = new Printer
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            ServerUrl = resolvedBase,
            OriginalServerUrl = normalizedInput,
            IpAddress = resolvedIp,
            Notes = dto.Notes,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            DateAcquired = dto.DateAcquired,
            Backend = (int)dto.Backend,
            ApiKey = dto.ApiKey
        };
        db.Printers.Add(p);
        await db.SaveChangesAsync(ct);
        if (p.Backend == 1) // PrusaLink
        {
            var status = await prusa.GetCompositeStatusAsync(p.ServerUrl, p.ApiKey, ct);
            return CreatedAtAction(nameof(Get), new { id = p.Id }, new PrinterDto(
                Id: p.Id,
                Name: p.Name,
                ServerUrl: p.ServerUrl,
                Notes: p.Notes,
                IsOnline: status.IsOnline,
                State: status.State,
                ManufacturerName: null,
                ModelName: null,
                Progress: status.Progress,
                JobName: status.JobName,
                ThumbnailUrl: status.ThumbnailUrl,
                CameraStreamUrl: status.CameraStreamUrl,
                CameraSnapshotUrl: status.CameraSnapshotUrl,
                Backend: Farm.Web.Shared.PrinterBackend.PrusaLink,
                ApiKey: p.ApiKey,
                OriginalServerUrl: p.OriginalServerUrl,
                IpAddress: p.IpAddress
            ));
        }
        else
        {
            var status = await moon.GetCompositeStatusAsync(p.ServerUrl, ct);
            return CreatedAtAction(nameof(Get), new { id = p.Id }, new PrinterDto(
                Id: p.Id,
                Name: p.Name,
                ServerUrl: p.ServerUrl,
                Notes: p.Notes,
                IsOnline: status.IsOnline,
                State: status.State,
                ManufacturerName: null,
                ModelName: null,
                Progress: status.Progress,
                JobName: status.JobName,
                ThumbnailUrl: status.ThumbnailUrl,
                CameraStreamUrl: status.CameraStreamUrl,
                CameraSnapshotUrl: status.CameraSnapshotUrl,
                X: status.X,
                Y: status.Y,
                Z: status.Z,
                HotendTemp: status.HotendTemp,
                BedTemp: status.BedTemp,
                HotendTarget: status.HotendTarget,
                BedTarget: status.BedTarget,
                Backend: Farm.Web.Shared.PrinterBackend.Moonraker,
                ApiKey: p.ApiKey,
                OriginalServerUrl: p.OriginalServerUrl,
                IpAddress: p.IpAddress
            ));
        }
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
        var defaultPort = dto.Backend.HasValue ? (dto.Backend.Value == PrinterBackend.PrusaLink ? 80 : 7125) : (p.Backend == 1 ? 80 : 7125);
        var normalizedInput = NormalizeServerUrl(dto.ServerUrl, defaultPort);
        string resolvedBase = normalizedInput;
        string? resolvedIp = null;
        try
        {
            var uri = new Uri(normalizedInput);
            if (!System.Net.IPAddress.TryParse(uri.Host, out _))
            {
                var hostToResolve = EnsureLocalSuffix(uri.Host);
                var addresses = await System.Net.Dns.GetHostAddressesAsync(hostToResolve, ct);
                var firstIp = Array.Find(addresses, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
                if (firstIp is not null)
                {
                    var ub = new UriBuilder(uri)
                    {
                        Host = firstIp.ToString()
                    };
                    resolvedBase = ub.Uri.ToString().TrimEnd('/');
                    resolvedIp = firstIp.ToString();
                }
            }
            else
            {
                resolvedIp = uri.Host;
            }
        }
        catch { }
        p.ServerUrl = resolvedBase;
        p.OriginalServerUrl = normalizedInput;
        p.IpAddress = resolvedIp;
        p.Notes = dto.Notes;
        p.ManufacturerId = manufacturerId;
        p.ModelId = modelId;
        p.DateAcquired = dto.DateAcquired;
        if (dto.Backend.HasValue)
            p.Backend = (int)dto.Backend.Value;
        if (dto.ApiKey != null)
            p.ApiKey = dto.ApiKey;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("resolve")]
    public async Task<ActionResult<Farm.Web.Shared.ResolveHostnameResponse>> ResolveHost([FromBody] Farm.Web.Shared.ResolveHostnameRequest body, CancellationToken ct)
    {
        var defaultPort = body.Backend == Farm.Web.Shared.PrinterBackend.PrusaLink ? 80 : 7125;
        var normalized = NormalizeServerUrl(body.ServerUrl, defaultPort);
        try
        {
            var uri = new Uri(normalized);
            var host = uri.Host;
            if (!System.Net.IPAddress.TryParse(host, out _))
            {
                host = EnsureLocalSuffix(host);
            }
            string? ip = null;
            try
            {
                if (!System.Net.IPAddress.TryParse(host, out _))
                {
                    var addrs = await System.Net.Dns.GetHostAddressesAsync(host, ct);
                    var firstIp = Array.Find(addrs, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addrs.FirstOrDefault();
                    ip = firstIp?.ToString();
                }
                else
                {
                    ip = host;
                }
            }
            catch { }

            var ub = new UriBuilder(uri) { Host = ip ?? uri.Host };
            var baseUrl = ub.Uri.ToString().TrimEnd('/');
            return new Farm.Web.Shared.ResolveHostnameResponse(normalized, ip, baseUrl);
        }
        catch
        {
            return BadRequest("Invalid URL");
        }
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

    [HttpGet("{id:guid}/snapshot")]
    public async Task<IActionResult> GetSnapshot(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        var bytes = await moon.GetCameraSnapshotAsync(p.ServerUrl, ct);
        if (bytes is null) return NotFound();
        return File(bytes, "image/jpeg");
    }

    [HttpPost("{id:guid}/home")]
    public async Task<ActionResult<CommandResult>> Home(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        var ok = await moon.SendHomeAsync(p.ServerUrl, ct);
        return new CommandResult(ok, ok ? null : "Failed to send home command");
    }

    [HttpPost("{id:guid}/homexy")]
    public async Task<ActionResult<CommandResult>> HomeXY(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        var ok = await moon.HomeXYAsync(p.ServerUrl, ct);
        return new CommandResult(ok, ok ? null : "Failed to home XY");
    }

    [HttpPost("{id:guid}/homez")]
    public async Task<ActionResult<CommandResult>> HomeZ(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        var ok = await moon.HomeZAsync(p.ServerUrl, ct);
        return new CommandResult(ok, ok ? null : "Failed to home Z");
    }

    [HttpPost("{id:guid}/temps")]
    public async Task<ActionResult<CommandResult>> SetTemps(Guid id, TempTargets targets, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        var ok = await moon.SetTempsAsync(p.ServerUrl, targets.Hotend, targets.Bed, ct);
        return new CommandResult(ok, ok ? null : "Failed to set temperatures");
    }

    [HttpPost("{id:guid}/move")]
    public async Task<ActionResult<CommandResult>> Move(Guid id, MoveRequest req, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        var ok = await moon.MoveAsync(p.ServerUrl, req.X, req.Y, req.Z, req.F, ct);
        return new CommandResult(ok, ok ? null : "Failed to move");
    }

    [HttpPost("{id:guid}/moveto")]
    public async Task<ActionResult<CommandResult>> MoveTo(Guid id, MoveRequest req, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        var ok = await moon.MoveToAsync(p.ServerUrl, req.X, req.Y, req.Z, req.F, ct);
        return new CommandResult(ok, ok ? null : "Failed to move to position");
    }

    [HttpPost("{id:guid}/pause")]
    public async Task<ActionResult<CommandResult>> Pause(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        var ok = await moon.PauseAsync(p.ServerUrl, ct);
        return new CommandResult(ok, ok ? null : "Failed to pause");
    }

    [HttpPost("{id:guid}/resume")]
    public async Task<ActionResult<CommandResult>> Resume(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        var ok = await moon.ResumeAsync(p.ServerUrl, ct);
        return new CommandResult(ok, ok ? null : "Failed to resume");
    }

    [HttpPost("{id:guid}/emergency-stop")]
    public async Task<ActionResult<CommandResult>> EmergencyStop(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        var ok = await moon.EmergencyStopAsync(p.ServerUrl, ct);
        return new CommandResult(ok, ok ? null : "Failed to emergency stop");
    }
}
