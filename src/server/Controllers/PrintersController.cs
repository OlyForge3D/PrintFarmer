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
public class PrintersController(AppDbContext db, MoonrakerClient moon, PrusaLinkClient prusa, SdcpClient sdcp) : ControllerBase
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
            else if (p.Backend == 2) // SDCP
            {
                var status = await sdcp.GetCompositeStatusAsync(p.ServerUrl, ct);
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
                    Backend: Farm.Web.Shared.PrinterBackend.SDCP,
                    ApiKey: p.ApiKey,
                    OriginalServerUrl: p.OriginalServerUrl,
                    IpAddress: p.IpAddress
                );
            }
            else // Moonraker
            {
                var status = await moon.GetCompositeStatusAsync(p.ServerUrl, ct);
                var spoolInfo = await GetSpoolInfoAsync(p.ServerUrl, ct);
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
                    IpAddress: p.IpAddress,
                    SpoolInfo: spoolInfo
                );
            }
        }));
        return Ok(dtos);
    }

    [HttpGet("basic")]
    public async Task<ActionResult<IEnumerable<PrinterBasicDto>>> GetBasic(CancellationToken ct)
    {
        var items = await db.Printers.AsNoTracking().Include(p => p.Manufacturer).Include(p => p.Model).ToListAsync(ct);
        var dtos = items.Select(p => new PrinterBasicDto(
            Id: p.Id,
            Name: p.Name,
            ServerUrl: p.ServerUrl,
            Notes: p.Notes,
            ManufacturerName: p.Manufacturer?.Name,
            ModelName: p.Model?.Name,
            Backend: p.Backend == 1 ? Farm.Web.Shared.PrinterBackend.PrusaLink : 
                     p.Backend == 2 ? Farm.Web.Shared.PrinterBackend.SDCP : 
                     Farm.Web.Shared.PrinterBackend.Moonraker,
            ApiKey: p.ApiKey,
            OriginalServerUrl: p.OriginalServerUrl,
            IpAddress: p.IpAddress
        )).ToList();
        return Ok(dtos);
    }

    [HttpGet("{id:guid}/status")]
    public async Task<ActionResult<PrinterStatusDto>> GetStatus(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        
        try
        {
            if (p.Backend == 1) // PrusaLink
            {
                var status = await prusa.GetCompositeStatusAsync(p.ServerUrl, p.ApiKey, ct);
                return new PrinterStatusDto(
                    Id: p.Id,
                    IsOnline: status.IsOnline,
                    State: status.State,
                    Progress: status.Progress,
                    JobName: status.JobName,
                    ThumbnailUrl: status.ThumbnailUrl,
                    CameraStreamUrl: status.CameraStreamUrl,
                    CameraSnapshotUrl: status.CameraSnapshotUrl
                );
            }
            else if (p.Backend == 2) // SDCP
            {
                var status = await sdcp.GetCompositeStatusAsync(p.ServerUrl, ct);
                return new PrinterStatusDto(
                    Id: p.Id,
                    IsOnline: status.IsOnline,
                    State: status.State,
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
                    BedTarget: status.BedTarget
                );
            }
            else // Moonraker
            {
                var status = await moon.GetCompositeStatusAsync(p.ServerUrl, ct);
                var spoolInfo = await GetSpoolInfoAsync(p.ServerUrl, ct);
                return new PrinterStatusDto(
                    Id: p.Id,
                    IsOnline: status.IsOnline,
                    State: status.State,
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
                    SpoolInfo: spoolInfo
                );
            }
        }
        catch
        {
            // Return offline status if there's any error
            return new PrinterStatusDto(
                Id: p.Id,
                IsOnline: false,
                State: null,
                Progress: null,
                JobName: null,
                ThumbnailUrl: null,
                CameraStreamUrl: null,
                CameraSnapshotUrl: null,
                SpoolInfo: null
            );
        }
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
        else if (p.Backend == 2) // SDCP
        {
            var status = await sdcp.GetCompositeStatusAsync(p.ServerUrl, ct);
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
                Backend: Farm.Web.Shared.PrinterBackend.SDCP,
                ApiKey: p.ApiKey,
                OriginalServerUrl: p.OriginalServerUrl,
                IpAddress: p.IpAddress
            );
        }
        else // Moonraker
        {
            var status = await moon.GetCompositeStatusAsync(p.ServerUrl, ct);
            var spoolInfo = await GetSpoolInfoAsync(p.ServerUrl, ct);
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
                IpAddress: p.IpAddress,
                SpoolInfo: spoolInfo
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
        var defaultPort = dto.Backend == PrinterBackend.PrusaLink ? 80 : 
                         dto.Backend == PrinterBackend.SDCP ? 80 : 7125;
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
        else if (p.Backend == 2) // SDCP
        {
            var status = await sdcp.GetCompositeStatusAsync(p.ServerUrl, ct);
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
                Backend: Farm.Web.Shared.PrinterBackend.SDCP,
                ApiKey: p.ApiKey,
                OriginalServerUrl: p.OriginalServerUrl,
                IpAddress: p.IpAddress
            ));
        }
        else // Moonraker
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
        var defaultPort = dto.Backend.HasValue ? 
            (dto.Backend.Value == PrinterBackend.PrusaLink ? 80 : 
             dto.Backend.Value == PrinterBackend.SDCP ? 80 : 7125) : 
            (p.Backend == 1 ? 80 : p.Backend == 2 ? 80 : 7125);
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
        var defaultPort = body.Backend == Farm.Web.Shared.PrinterBackend.PrusaLink ? 80 : 
                         body.Backend == Farm.Web.Shared.PrinterBackend.SDCP ? 80 : 7125;
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
        
        bool ok;
        if (p.Backend == 2) // SDCP
        {
            ok = await sdcp.PausePrintAsync(p.ServerUrl, ct);
        }
        else // Moonraker (and PrusaLink for now)
        {
            ok = await moon.PauseAsync(p.ServerUrl, ct);
        }
        
        return new CommandResult(ok, ok ? null : "Failed to pause");
    }

    [HttpPost("{id:guid}/resume")]
    public async Task<ActionResult<CommandResult>> Resume(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        
        bool ok;
        if (p.Backend == 2) // SDCP
        {
            ok = await sdcp.ResumePrintAsync(p.ServerUrl, ct);
        }
        else // Moonraker (and PrusaLink for now)
        {
            ok = await moon.ResumeAsync(p.ServerUrl, ct);
        }
        
        return new CommandResult(ok, ok ? null : "Failed to resume");
    }

    [HttpPost("{id:guid}/emergency-stop")]
    public async Task<ActionResult<CommandResult>> EmergencyStop(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        
        bool ok;
        if (p.Backend == 2) // SDCP
        {
            ok = await sdcp.CancelPrintAsync(p.ServerUrl, ct);
        }
        else // Moonraker (and PrusaLink for now)
        {
            ok = await moon.EmergencyStopAsync(p.ServerUrl, ct);
        }
        
        return new CommandResult(ok, ok ? null : "Failed to emergency stop");
    }

    // Print job control
    [HttpPost("{id:guid}/print/start")]
    public async Task<ActionResult<CommandResult>> StartPrint(Guid id, StartPrintRequest request, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        
        if (p.Backend == 2) // SDCP
        {
            var ok = await sdcp.StartPrintAsync(p.ServerUrl, request.Filename, ct);
            return new CommandResult(ok, ok ? null : "Failed to start print");
        }
        
        return new CommandResult(false, "Start print not implemented for this printer type");
    }

    // Camera control endpoints
    [HttpPost("{id:guid}/camera/enable")]
    public async Task<ActionResult<CommandResult>> EnableCamera(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        
        if (p.Backend == 2) // SDCP
        {
            var ok = await sdcp.EnableCameraAsync(p.ServerUrl, ct);
            return new CommandResult(ok, ok ? null : "Failed to enable camera");
        }
        
        return new CommandResult(false, "Camera control not supported for this printer type");
    }

    [HttpPost("{id:guid}/camera/disable")]
    public async Task<ActionResult<CommandResult>> DisableCamera(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        
        if (p.Backend == 2) // SDCP
        {
            var ok = await sdcp.DisableCameraAsync(p.ServerUrl, ct);
            return new CommandResult(ok, ok ? null : "Failed to disable camera");
        }
        
        return new CommandResult(false, "Camera control not supported for this printer type");
    }

    [HttpGet("{id:guid}/camera/url")]
    public async Task<ActionResult<CameraUrlResult>> GetCameraUrl(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null) return NotFound();
        
        if (p.Backend == 2) // SDCP
        {
            var streamUrl = await sdcp.GetCameraUrlAsync(p.ServerUrl, ct);
            var snapshotUrl = await sdcp.GetCameraSnapshotUrlAsync(p.ServerUrl, ct);
            return new CameraUrlResult(streamUrl, snapshotUrl);
        }
        
        return new CameraUrlResult(null, null);
    }

    [HttpPost("{id:guid}/files/upload")]
    public async Task<ActionResult> UploadGcode(Guid id, IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file provided");

        if (!file.FileName.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase))
            return BadRequest("File must be a .gcode file");

        var p = await db.Printers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p == null) return NotFound();

        try
        {
            await using var fileStream = file.OpenReadStream();
            bool success = ((PrinterBackend)p.Backend) switch
            {
                PrinterBackend.Moonraker => await moon.UploadGcodeAsync(p.ServerUrl, file.FileName, fileStream, ct),
                PrinterBackend.PrusaLink => await prusa.UploadGcodeAsync(p.ServerUrl, file.FileName, fileStream, p.ApiKey, ct),
                PrinterBackend.SDCP => await sdcp.UploadGcodeAsync(p.ServerUrl, file.FileName, fileStream, ct),
                _ => false
            };

            if (success)
                return Ok(new { message = "File uploaded successfully", filename = file.FileName });
            else
                return StatusCode(500, "Failed to upload file to printer");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Upload failed: {ex.Message}");
        }
    }

    [HttpGet("{id:guid}/files")]
    public async Task<ActionResult<string[]>> GetFileList(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p == null) return NotFound();

        try
        {
            string[] files = ((PrinterBackend)p.Backend) switch
            {
                PrinterBackend.Moonraker => await moon.GetFileListAsync(p.ServerUrl, ct),
                PrinterBackend.PrusaLink => await prusa.GetFileListAsync(p.ServerUrl, p.ApiKey, ct),
                PrinterBackend.SDCP => await sdcp.GetFileListAsync(p.ServerUrl, ct),
                _ => Array.Empty<string>()
            };

            return Ok(files);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Failed to get file list: {ex.Message}");
        }
    }

    [HttpPost("{id:guid}/files/{fileName}/print")]
    public async Task<ActionResult> StartPrintFromFile(Guid id, string fileName, CancellationToken ct)
    {
        var p = await db.Printers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p == null) return NotFound();

        try
        {
            bool success = ((PrinterBackend)p.Backend) switch
            {
                PrinterBackend.Moonraker => await moon.StartPrintAsync(p.ServerUrl, fileName, ct),
                PrinterBackend.PrusaLink => await prusa.StartPrintAsync(p.ServerUrl, fileName, p.ApiKey, ct),
                PrinterBackend.SDCP => await sdcp.StartPrintAsync(p.ServerUrl, fileName, ct),
                _ => false
            };

            if (success)
                return Ok(new { message = "Print started successfully", filename = fileName });
            else
                return StatusCode(500, "Failed to start print");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Failed to start print: {ex.Message}");
        }
    }

    // Helper record for camera URL results
    public record CameraUrlResult(string? StreamUrl, string? SnapshotUrl);

    // Helper method to get spool information for Moonraker printers
    private async Task<PrinterSpoolInfoDto?> GetSpoolInfoAsync(string serverUrl, CancellationToken ct)
    {
        try
        {
            // Get the active spool ID from Moonraker
            var activeSpoolId = await moon.GetSpoolmanActiveSpoolAsync(serverUrl, ct);
            if (activeSpoolId == null)
            {
                return new PrinterSpoolInfoDto(HasActiveSpool: false);
            }

            // Get spool details from Spoolman via Moonraker
            var spoolDetailsJson = await moon.GetSpoolmanSpoolByIdAsync(serverUrl, activeSpoolId.Value, ct);
            if (string.IsNullOrWhiteSpace(spoolDetailsJson))
            {
                return new PrinterSpoolInfoDto(
                    HasActiveSpool: true,
                    ActiveSpoolId: activeSpoolId
                );
            }

            // Parse the JSON response to extract spool information
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(spoolDetailsJson);
                var root = doc.RootElement;
                
                var spoolName = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                var material = root.TryGetProperty("material", out var matEl) ? matEl.GetString() : null;
                var colorHex = root.TryGetProperty("color_hex", out var colorEl) ? colorEl.GetString() : null;
                var remainingWeight = root.TryGetProperty("remaining_weight", out var weightEl) && weightEl.ValueKind == System.Text.Json.JsonValueKind.Number 
                    ? weightEl.GetDouble() : (double?)null;
                
                // Check if filament information is nested
                string? filamentName = null;
                string? vendor = null;
                if (root.TryGetProperty("filament", out var filamentEl) && filamentEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    filamentName = filamentEl.TryGetProperty("name", out var fnameEl) ? fnameEl.GetString() : null;
                    if (filamentEl.TryGetProperty("vendor", out var vendorEl) && vendorEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        vendor = vendorEl.TryGetProperty("name", out var vNameEl) ? vNameEl.GetString() : null;
                    }
                }

                return new PrinterSpoolInfoDto(
                    HasActiveSpool: true,
                    ActiveSpoolId: activeSpoolId,
                    SpoolName: spoolName,
                    Material: material,
                    ColorHex: colorHex,
                    FilamentName: filamentName,
                    Vendor: vendor,
                    RemainingWeightG: remainingWeight,
                    SpoolInUse: true
                );
            }
            catch
            {
                // If JSON parsing fails, return basic info
                return new PrinterSpoolInfoDto(
                    HasActiveSpool: true,
                    ActiveSpoolId: activeSpoolId
                );
            }
        }
        catch
        {
            // If any Spoolman operations fail, just return no spool info
            return new PrinterSpoolInfoDto(HasActiveSpool: false);
        }
    }

    // ===== HISTORY ENDPOINTS =====
    
    [HttpGet("{id}/history")]
    public async Task<ActionResult<Farm.Web.Shared.HistoryListResponse>> GetHistory(Guid id, [FromQuery] int? limit = null, [FromQuery] int? start = null, [FromQuery] DateTime? since = null, [FromQuery] DateTime? before = null, [FromQuery] string? order = null, CancellationToken ct = default)
    {
        var printer = await db.Printers.FindAsync(id);
        if (printer == null) return NotFound();

        if (printer.Backend != (int)PrinterBackend.Moonraker)
        {
            // For non-Moonraker printers, return empty history for now
            return new Farm.Web.Shared.HistoryListResponse { Count = 0, Jobs = Array.Empty<Farm.Web.Shared.HistoryJob>() };
        }

        try
        {
            var moonrakerResponse = await moon.GetHistoryListAsync(printer.ServerUrl, limit, start, since, before, order, ct);
            if (moonrakerResponse == null)
            {
                return new Farm.Web.Shared.HistoryListResponse { Count = 0, Jobs = Array.Empty<Farm.Web.Shared.HistoryJob>() };
            }

            // Convert from Moonraker models to shared models
            var jobs = moonrakerResponse.Jobs.Select(j => new Farm.Web.Shared.HistoryJob
            {
                JobId = j.JobId,
                Exists = j.Exists,
                EndTime = j.EndTime,
                FilamentUsed = j.FilamentUsed,
                Filename = j.Filename,
                Metadata = j.Metadata,
                PrintDuration = j.PrintDuration,
                Status = j.Status,
                StartTime = j.StartTime,
                TotalDuration = j.TotalDuration,
                User = j.User,
                AuxiliaryData = j.AuxiliaryData?.Select(a => new Farm.Web.Shared.AuxiliaryData
                {
                    Provider = a.Provider,
                    Name = a.Name,
                    Value = a.Value,
                    Description = a.Description,
                    Units = a.Units
                }).ToArray(),
                ThumbnailUrl = ExtractThumbnailUrl(j.Metadata, printer.ServerUrl)
            }).ToArray();

            return new Farm.Web.Shared.HistoryListResponse
            {
                Count = moonrakerResponse.Count,
                Jobs = jobs
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get history for printer {id}: {ex.Message}");
            return new Farm.Web.Shared.HistoryListResponse { Count = 0, Jobs = Array.Empty<Farm.Web.Shared.HistoryJob>() };
        }
    }

    [HttpGet("{id}/history/{jobId}")]
    public async Task<ActionResult<Farm.Web.Shared.HistoryJob>> GetHistoryJob(Guid id, string jobId, CancellationToken ct = default)
    {
        var printer = await db.Printers.FindAsync(id);
        if (printer == null) return NotFound();

        if (printer.Backend != (int)PrinterBackend.Moonraker)
        {
            return NotFound("History is only available for Moonraker printers");
        }

        try
        {
            var moonrakerJob = await moon.GetHistoryJobAsync(printer.ServerUrl, jobId, ct);
            if (moonrakerJob == null)
            {
                return NotFound();
            }

            // Convert from Moonraker model to shared model
            var job = new Farm.Web.Shared.HistoryJob
            {
                JobId = moonrakerJob.JobId,
                Exists = moonrakerJob.Exists,
                EndTime = moonrakerJob.EndTime,
                FilamentUsed = moonrakerJob.FilamentUsed,
                Filename = moonrakerJob.Filename,
                Metadata = moonrakerJob.Metadata,
                PrintDuration = moonrakerJob.PrintDuration,
                Status = moonrakerJob.Status,
                StartTime = moonrakerJob.StartTime,
                TotalDuration = moonrakerJob.TotalDuration,
                User = moonrakerJob.User,
                AuxiliaryData = moonrakerJob.AuxiliaryData?.Select(a => new Farm.Web.Shared.AuxiliaryData
                {
                    Provider = a.Provider,
                    Name = a.Name,
                    Value = a.Value,
                    Description = a.Description,
                    Units = a.Units
                }).ToArray()
            };

            return job;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get history job {jobId} for printer {id}: {ex.Message}");
            return StatusCode(500, "Failed to retrieve history job");
        }
    }

    [HttpGet("{id}/history/totals")]
    public async Task<ActionResult<Farm.Web.Shared.HistoryTotals>> GetHistoryTotals(Guid id, CancellationToken ct = default)
    {
        var printer = await db.Printers.FindAsync(id);
        if (printer == null) return NotFound();

        Console.WriteLine($"GetHistoryTotals called for printer {id} ({printer.Name}), backend: {printer.Backend}");

        if (printer.Backend != (int)PrinterBackend.Moonraker)
        {
            Console.WriteLine($"Printer {id} is not Moonraker backend, returning empty totals");
            // Return empty totals for non-Moonraker printers
            return new Farm.Web.Shared.HistoryTotals
            {
                JobTotals = new Farm.Web.Shared.JobTotals()
            };
        }

        try
        {
            Console.WriteLine($"Calling Moonraker API for totals at: {printer.ServerUrl}");
            var moonrakerTotals = await moon.GetHistoryTotalsAsync(printer.ServerUrl, ct);
            if (moonrakerTotals == null)
            {
                Console.WriteLine("Moonraker API returned null totals");
                return new Farm.Web.Shared.HistoryTotals { JobTotals = new Farm.Web.Shared.JobTotals() };
            }

            Console.WriteLine($"Moonraker totals received - Jobs: {moonrakerTotals.JobTotals.TotalJobs}, PrintTime: {moonrakerTotals.JobTotals.TotalPrintTime}, FilamentUsed: {moonrakerTotals.JobTotals.TotalFilamentUsed}");

            // Convert from Moonraker model to shared model
            var totals = new Farm.Web.Shared.HistoryTotals
            {
                JobTotals = new Farm.Web.Shared.JobTotals
                {
                    TotalJobs = (int)moonrakerTotals.JobTotals.TotalJobs,
                    TotalTime = moonrakerTotals.JobTotals.TotalTime,
                    TotalPrintTime = moonrakerTotals.JobTotals.TotalPrintTime,
                    TotalFilamentUsed = moonrakerTotals.JobTotals.TotalFilamentUsed,
                    LongestJob = moonrakerTotals.JobTotals.LongestJob,
                    LongestPrint = moonrakerTotals.JobTotals.LongestPrint
                },
                AuxiliaryTotals = moonrakerTotals.AuxiliaryTotals?.Select(a => new Farm.Web.Shared.AuxiliaryTotals
                {
                    Provider = a.Provider,
                    Field = a.Field,
                    Maximum = a.Maximum,
                    Total = a.Total
                }).ToArray()
            };

            Console.WriteLine($"Returning converted totals - Jobs: {totals.JobTotals.TotalJobs}, PrintTime: {totals.JobTotals.TotalPrintTime}, FilamentUsed: {totals.JobTotals.TotalFilamentUsed}");
            return totals;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get history totals for printer {id}: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            return new Farm.Web.Shared.HistoryTotals { JobTotals = new Farm.Web.Shared.JobTotals() };
        }
    }

    [HttpDelete("{id}/history/{jobId}")]
    public async Task<ActionResult> DeleteHistoryJob(Guid id, string jobId, CancellationToken ct = default)
    {
        var printer = await db.Printers.FindAsync(id);
        if (printer == null) return NotFound();

        if (printer.Backend != (int)PrinterBackend.Moonraker)
        {
            return BadRequest("History deletion is only available for Moonraker printers");
        }

        try
        {
            var success = await moon.DeleteHistoryJobAsync(printer.ServerUrl, jobId, ct);
            return success ? Ok() : StatusCode(500, "Failed to delete history job");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to delete history job {jobId} for printer {id}: {ex.Message}");
            return StatusCode(500, "Failed to delete history job");
        }
    }

    [HttpGet("export")]
    public async Task<IActionResult> ExportPrinters(CancellationToken ct)
    {
        var printers = await db.Printers
            .Include(p => p.Manufacturer)
            .Include(p => p.Model)
            .Select(p => new
            {
                p.Name,
                p.ServerUrl,
                p.OriginalServerUrl,
                p.Notes,
                ManufacturerName = p.Manufacturer != null ? p.Manufacturer.Name : "",
                ModelName = p.Model != null ? p.Model.Name : "",
                Backend = p.Backend.ToString(),
                p.ApiKey,
                p.DateAcquired
            })
            .ToListAsync(ct);

        var csv = new System.Text.StringBuilder();
        csv.AppendLine("Name,ServerUrl,OriginalServerUrl,Notes,ManufacturerName,ModelName,Backend,ApiKey,DateAcquired");

        foreach (var printer in printers)
        {
            csv.AppendLine($"{EscapeCsvValue(printer.Name)}," +
                          $"{EscapeCsvValue(printer.ServerUrl)}," +
                          $"{EscapeCsvValue(printer.OriginalServerUrl)}," +
                          $"{EscapeCsvValue(printer.Notes)}," +
                          $"{EscapeCsvValue(printer.ManufacturerName)}," +
                          $"{EscapeCsvValue(printer.ModelName)}," +
                          $"{EscapeCsvValue(printer.Backend)}," +
                          $"{EscapeCsvValue(printer.ApiKey)}," +
                          $"{EscapeCsvValue(printer.DateAcquired?.ToString("yyyy-MM-dd"))}");
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(csv.ToString());
        return File(bytes, "text/csv", $"printers-export-{DateTime.UtcNow:yyyy-MM-dd-HHmm}.csv");
    }

    [HttpPost("import")]
    public async Task<IActionResult> ImportPrinters(IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file provided");

        if (!file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return BadRequest("File must be a CSV file");

        var results = new List<object>();
        var errors = new List<string>();

        try
        {
            using var reader = new StreamReader(file.OpenReadStream());
            var csvContent = await reader.ReadToEndAsync();
            var lines = csvContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length < 2)
            {
                return BadRequest("CSV file must contain at least a header row and one data row");
            }

            var header = lines[0].Split(',');
            var expectedHeaders = new[] { "Name", "ServerUrl", "OriginalServerUrl", "Notes", "ManufacturerName", "ModelName", "Backend", "ApiKey", "DateAcquired" };

            // Validate header
            for (int i = 0; i < expectedHeaders.Length; i++)
            {
                if (i >= header.Length || !header[i].Trim().Equals(expectedHeaders[i], StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Invalid header format. Expected: {string.Join(",", expectedHeaders)}");
                    break;
                }
            }

            if (errors.Count == 0)
            {
                for (int i = 1; i < lines.Length; i++)
                {
                    try
                    {
                        var values = ParseCsvLine(lines[i]);
                        if (values.Length >= 9)
                        {
                            var createDto = new CreatePrinterDto
                            {
                                Name = values[0]?.Trim() ?? "",
                                ServerUrl = values[1]?.Trim() ?? "",
                                OriginalServerUrl = string.IsNullOrWhiteSpace(values[2]) ? null : values[2].Trim(),
                                Notes = string.IsNullOrWhiteSpace(values[3]) ? null : values[3].Trim(),
                                NewManufacturerName = string.IsNullOrWhiteSpace(values[4]) ? null : values[4].Trim(),
                                NewModelName = string.IsNullOrWhiteSpace(values[5]) ? null : values[5].Trim(),
                                Backend = Enum.TryParse<PrinterBackend>(values[6]?.Trim(), true, out var backend) ? backend : PrinterBackend.Moonraker,
                                ApiKey = string.IsNullOrWhiteSpace(values[7]) ? null : values[7].Trim(),
                                DateAcquired = DateTime.TryParse(values[8]?.Trim(), out var date) ? date : null
                            };

                            // Validate required fields
                            if (string.IsNullOrWhiteSpace(createDto.Name))
                            {
                                errors.Add($"Row {i + 1}: Name is required");
                                continue;
                            }

                            if (string.IsNullOrWhiteSpace(createDto.ServerUrl))
                            {
                                errors.Add($"Row {i + 1}: ServerUrl is required");
                                continue;
                            }

                            // Check if printer already exists
                            var existingPrinter = await db.Printers
                                .FirstOrDefaultAsync(p => p.Name == createDto.Name, ct);

                            if (existingPrinter != null)
                            {
                                results.Add(new { Row = i + 1, Name = createDto.Name, Status = "Skipped", Reason = "Printer already exists" });
                                continue;
                            }

                            // Create the printer using existing logic
                            var result = await CreatePrinterFromDto(createDto, ct);
                            results.Add(new { Row = i + 1, Name = createDto.Name, Status = "Imported", Id = result.Id });
                        }
                        else
                        {
                            errors.Add($"Row {i + 1}: Invalid number of columns");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Row {i + 1}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return BadRequest($"Error processing file: {ex.Message}");
        }

        return Ok(new { 
            ImportedCount = results.Count(r => ((dynamic)r).Status == "Imported"),
            SkippedCount = results.Count(r => ((dynamic)r).Status == "Skipped"),
            Results = results,
            Errors = errors
        });
    }

    private static string EscapeCsvValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        // Escape quotes and wrap in quotes if contains comma, quote, or newline
        if (value.Contains('"'))
            value = value.Replace("\"", "\"\"");

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            value = $"\"{value}\"";

        return value;
    }

    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    // Escaped quote
                    current.Append('"');
                    i++; // Skip next quote
                }
                else
                {
                    // Toggle quote state
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                // End of field
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        
        // Add the last field
        result.Add(current.ToString());
        
        return result.ToArray();
    }

    private async Task<PrinterDto> CreatePrinterFromDto(CreatePrinterDto dto, CancellationToken ct)
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
        var defaultPort = dto.Backend == PrinterBackend.PrusaLink ? 80 : 
                         dto.Backend == PrinterBackend.SDCP ? 80 : 7125;
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

        // For import, we'll return a simplified PrinterDto without live status to avoid network delays
        return new PrinterDto(
            Id: p.Id,
            Name: p.Name,
            ServerUrl: p.ServerUrl,
            Notes: p.Notes,
            IsOnline: false, // Will be updated by background service
            State: null,
            ManufacturerName: null,
            ModelName: null,
            Backend: dto.Backend,
            ApiKey: p.ApiKey,
            OriginalServerUrl: p.OriginalServerUrl,
            IpAddress: p.IpAddress
        );
    }

    // Helper method to extract thumbnail URL from metadata
    private static string? ExtractThumbnailUrl(Dictionary<string, object> metadata, string printerServerUrl)
    {
        if (metadata == null) return null;
        
        // Look for thumbnail in common metadata keys
        var thumbnailKeys = new[] { "thumbnail", "thumbnails", "gcode_thumbnail" };
        
        foreach (var key in thumbnailKeys)
        {
            if (metadata.TryGetValue(key, out var thumbnailValue))
            {
                // Handle different thumbnail formats
                if (thumbnailValue is string thumbnailStr && !string.IsNullOrEmpty(thumbnailStr))
                {
                    // If it's already a full URL, return it
                    if (thumbnailStr.StartsWith("http://") || thumbnailStr.StartsWith("https://"))
                        return thumbnailStr;
                    
                    // Otherwise, construct the full URL
                    return $"{printerServerUrl.TrimEnd('/')}/server/files/gcodes/{thumbnailStr}";
                }
                
                // Handle array of thumbnails - take the first one
                if (thumbnailValue is System.Text.Json.JsonElement jsonElement && jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var array = jsonElement.EnumerateArray().ToList();
                    if (array.Count > 0 && array[0].ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var thumbnailPath = array[0].GetString();
                        if (!string.IsNullOrEmpty(thumbnailPath))
                        {
                            return thumbnailPath.StartsWith("http") ? thumbnailPath : $"{printerServerUrl.TrimEnd('/')}/server/files/gcodes/{thumbnailPath}";
                        }
                    }
                }
            }
        }
        
        return null;
    }

    // Request models
    public record StartPrintRequest(string Filename);
    
    public record UploadGcodeRequest(IFormFile File);
}
