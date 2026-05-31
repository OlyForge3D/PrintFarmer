using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Web.Api.Services.SmartPlug;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartPlugReading = Farm.Web.Api.Services.SmartPlug.PowerReading;

namespace Farm.Web.Api.Controllers.Admin;

/// <summary>
/// Admin endpoints for managing PowerMonitor configurations and testing smart plug connectivity.
/// </summary>
[ApiController]
[Route("api/admin/power-monitors")]
[Authorize(Roles = "farm_admin")]
[Tags("Admin - Power Monitors")]
public class AdminPowerMonitorsController(
    AppDbContext db,
    IEnumerable<ISmartPlugProvider> smartPlugProviders) : ControllerBase
{
    private readonly AppDbContext _db = db;
    private readonly IReadOnlyDictionary<string, ISmartPlugProvider> _providers =
        smartPlugProviders.ToDictionary(p => p.ProviderType, StringComparer.OrdinalIgnoreCase);

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<PowerMonitorDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<PowerMonitorDto>>> GetAllAsync(CancellationToken ct)
    {
        List<PowerMonitorDto> results = await _db.PowerMonitors
            .AsNoTracking()
            .Include(pm => pm.Printer)
            .Select(pm => MapToDto(pm))
            .ToListAsync(ct);

        return Ok(results);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(PowerMonitorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PowerMonitorDto>> GetByIdAsync(int id, CancellationToken ct)
    {
        PowerMonitor? pm = await _db.PowerMonitors
            .AsNoTracking()
            .Include(p => p.Printer)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (pm is null)
        {
            return NotFound();
        }

        return Ok(MapToDto(pm));
    }

    [HttpPost]
    [ProducesResponseType(typeof(PowerMonitorDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PowerMonitorDto>> CreateAsync(
        [FromBody] CreatePowerMonitorRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceAddress))
        {
            return BadRequest(new { error = "DeviceAddress is required." });
        }

        if (!_providers.ContainsKey(request.Provider))
        {
            return BadRequest(new { error = $"Unknown provider '{request.Provider}'. Valid providers: {string.Join(", ", _providers.Keys)}." });
        }

        bool printerExists = await _db.Printers.AnyAsync(p => p.Id == request.PrinterId, ct);
        if (!printerExists)
        {
            return BadRequest(new { error = $"Printer '{request.PrinterId}' not found." });
        }

        var pm = new PowerMonitor
        {
            PrinterId = request.PrinterId,
            ProviderType = request.Provider,
            DeviceAddress = request.DeviceAddress,
            ElectricityRateUsdPerKwh = request.ElectricityRatePerKwh ?? 0m,
            IsEnabled = request.Enabled,
        };

        _db.PowerMonitors.Add(pm);
        await _db.SaveChangesAsync(ct);

        await _db.Entry(pm).Reference(p => p.Printer).LoadAsync(ct);

        PowerMonitorDto dto = MapToDto(pm);
        return Created($"/api/admin/power-monitors/{pm.Id}", dto);
    }

    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(PowerMonitorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PowerMonitorDto>> UpdateAsync(
        int id,
        [FromBody] UpdatePowerMonitorRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceAddress))
        {
            return BadRequest(new { error = "DeviceAddress is required." });
        }

        if (!_providers.ContainsKey(request.Provider))
        {
            return BadRequest(new { error = $"Unknown provider '{request.Provider}'. Valid providers: {string.Join(", ", _providers.Keys)}." });
        }

        bool printerExists = await _db.Printers.AnyAsync(p => p.Id == request.PrinterId, ct);
        if (!printerExists)
        {
            return BadRequest(new { error = $"Printer '{request.PrinterId}' not found." });
        }

        PowerMonitor? pm = await _db.PowerMonitors
            .Include(p => p.Printer)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (pm is null)
        {
            return NotFound();
        }

        pm.PrinterId = request.PrinterId;
        pm.ProviderType = request.Provider;
        pm.DeviceAddress = request.DeviceAddress;
        pm.ElectricityRateUsdPerKwh = request.ElectricityRatePerKwh ?? 0m;
        pm.IsEnabled = request.Enabled;

        await _db.SaveChangesAsync(ct);

        // Refresh navigation property after potential PrinterId change
        await _db.Entry(pm).Reference(p => p.Printer).LoadAsync(ct);

        return Ok(MapToDto(pm));
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAsync(int id, CancellationToken ct)
    {
        PowerMonitor? pm = await _db.PowerMonitors.FindAsync([id], ct);
        if (pm is null)
        {
            return NotFound();
        }

        _db.PowerMonitors.Remove(pm);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("test")]
    [ProducesResponseType(typeof(TestPowerMonitorConnectionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TestPowerMonitorConnectionResponse>> TestConnectionAsync(
        [FromBody] TestPowerMonitorConnectionRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceAddress))
        {
            return BadRequest(new { error = "DeviceAddress is required." });
        }

        if (!_providers.TryGetValue(request.Provider, out ISmartPlugProvider? provider))
        {
            return BadRequest(new { error = $"Unknown provider '{request.Provider}'. Valid providers: {string.Join(", ", _providers.Keys)}." });
        }

        try
        {
            SmartPlugReading? reading = await provider.GetCurrentReadingAsync(request.DeviceAddress, ct);

            if (reading is not null)
            {
                return Ok(new TestPowerMonitorConnectionResponse
                {
                    Success = true,
                    Message = "Connected",
                    CurrentWatts = reading.WattsNow,
                });
            }

            bool ok = await provider.TestConnectionAsync(request.DeviceAddress, ct);
            return Ok(new TestPowerMonitorConnectionResponse
            {
                Success = ok,
                Message = ok ? "Connected" : "Device did not respond",
            });
        }
        catch (Exception ex)
        {
            return Ok(new TestPowerMonitorConnectionResponse
            {
                Success = false,
                Message = ex.Message,
            });
        }
    }

    private static PowerMonitorDto MapToDto(PowerMonitor pm) => new()
    {
        Id = pm.Id,
        PrinterId = pm.PrinterId,
        PrinterName = pm.Printer?.Name ?? string.Empty,
        Provider = pm.ProviderType,
        DeviceAddress = pm.DeviceAddress,
        ElectricityRatePerKwh = pm.ElectricityRateUsdPerKwh == 0m ? null : pm.ElectricityRateUsdPerKwh,
        Enabled = pm.IsEnabled,
    };
}
