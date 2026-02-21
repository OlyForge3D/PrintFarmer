using Farm.Infrastructure;
using Farm.Infrastructure.Services.NfcDevices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages NFC reader/writer devices (ESP32 + PN532) for filament spool tracking.
/// Receives heartbeats and scan events from firmware devices.
/// </summary>
[ApiController]
[Route("api/nfc-devices")]
[Tags("NFC Devices")]
public class NfcDevicesController(INfcDeviceService nfcDeviceService) : ControllerBase
{
    /// <summary>
    /// Gets all registered NFC devices.
    /// </summary>
    [Authorize]
    [HttpGet]
    [ProducesResponseType(typeof(NfcDeviceDto[]), 200)]
    public async Task<ActionResult<NfcDeviceDto[]>> GetAllAsync(CancellationToken ct)
    {
        var devices = await nfcDeviceService.GetAllAsync(ct);
        return Ok(devices);
    }

    /// <summary>
    /// Gets a specific NFC device by ID.
    /// </summary>
    [Authorize]
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(NfcDeviceDto), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<NfcDeviceDto>> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var device = await nfcDeviceService.GetByIdAsync(id, ct);
        return device is null ? NotFound() : Ok(device);
    }

    /// <summary>
    /// Registers a new NFC device.
    /// </summary>
    [Authorize]
    [HttpPost]
    [ProducesResponseType(typeof(NfcDeviceDto), 201)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<NfcDeviceDto>> CreateAsync([FromBody] CreateNfcDeviceDto dto, CancellationToken ct)
    {
        try
        {
            var device = await nfcDeviceService.CreateAsync(dto, ct);
            return Created($"/api/nfc-devices/{device.Id}", device);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Updates an NFC device.
    /// </summary>
    [Authorize]
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(NfcDeviceDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<NfcDeviceDto>> UpdateAsync(Guid id, [FromBody] UpdateNfcDeviceDto dto, CancellationToken ct)
    {
        try
        {
            var device = await nfcDeviceService.UpdateAsync(id, dto, ct);
            return device is null ? NotFound() : Ok(device);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Deletes an NFC device.
    /// </summary>
    [Authorize]
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken ct)
    {
        var deleted = await nfcDeviceService.DeleteAsync(id, ct);
        return deleted ? NoContent() : NotFound();
    }

    /// <summary>
    /// Receives a heartbeat from an NFC device.
    /// Auto-registers the device if not already known.
    /// Called periodically by the ESP32 firmware (every 60 seconds).
    /// </summary>
    [AllowAnonymous]
    [HttpPost("heartbeat")]
    [ProducesResponseType(typeof(NfcDeviceDto), 200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<NfcDeviceDto>> HeartbeatAsync([FromBody] NfcDeviceHeartbeatDto dto, CancellationToken ct)
    {
        var device = await nfcDeviceService.ProcessHeartbeatAsync(dto, ct);
        return device is null ? BadRequest(new { error = "Invalid printer ID" }) : Ok(device);
    }

    /// <summary>
    /// Receives a scan event from an NFC device.
    /// Called when a tag is scanned by the ESP32 firmware.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("scan")]
    [ProducesResponseType(typeof(NfcScanHistoryDto), 200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<NfcScanHistoryDto>> ScanEventAsync([FromBody] NfcScanEventDto dto, CancellationToken ct)
    {
        var result = await nfcDeviceService.ProcessScanEventAsync(dto, ct);
        return result is null ? BadRequest(new { error = "Device not found for printer ID" }) : Ok(result);
    }

    /// <summary>
    /// Gets scan history for a specific NFC device.
    /// </summary>
    [Authorize]
    [HttpGet("{id:guid}/history")]
    [ProducesResponseType(typeof(NfcScanHistoryDto[]), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<NfcScanHistoryDto[]>> GetHistoryAsync(
        Guid id,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        if (limit is < 1 or > 200)
        {
            return BadRequest(new { error = "Limit must be between 1 and 200." });
        }

        if (offset < 0)
        {
            return BadRequest(new { error = "Offset must be non-negative." });
        }

        var device = await nfcDeviceService.GetByIdAsync(id, ct);
        if (device is null)
        {
            return NotFound();
        }

        var history = await nfcDeviceService.GetScanHistoryAsync(id, limit, offset, ct);
        return Ok(history);
    }
}
