using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.NfcDevices;

public class NfcDeviceService(AppDbContext db, ILogger<NfcDeviceService> logger) : INfcDeviceService
{
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMinutes(3);

    public async Task<NfcDeviceDto[]> GetAllAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var devices = await db.NfcDevices
            .Include(d => d.Printer)
            .OrderBy(d => d.Name)
            .ToArrayAsync(ct);

        return devices.Select(d => MapToDto(d, now)).ToArray();
    }

    public async Task<NfcDeviceDto?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var device = await db.NfcDevices
            .Include(d => d.Printer)
            .FirstOrDefaultAsync(d => d.Id == id, ct);

        return device is null ? null : MapToDto(device, DateTime.UtcNow);
    }

    public async Task<NfcDeviceDto> CreateAsync(CreateNfcDeviceDto dto, CancellationToken ct)
    {
        if (dto.PrinterId.HasValue && !await db.Printers.AnyAsync(p => p.Id == dto.PrinterId.Value, ct))
        {
            throw new ArgumentException($"Printer with ID {dto.PrinterId} not found.");
        }

        var device = new NfcDevice
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            IpAddress = dto.IpAddress,
            PrinterId = dto.PrinterId,
            FirmwareVersion = dto.FirmwareVersion,
            CreatedAt = DateTime.UtcNow
        };

        db.NfcDevices.Add(device);
        await db.SaveChangesAsync(ct);

        await db.Entry(device).Reference(d => d.Printer).LoadAsync(ct);
        logger.LogInformation("NFC device registered: {Name} ({Id})", device.Name, device.Id);
        return MapToDto(device, DateTime.UtcNow);
    }

    public async Task<NfcDeviceDto?> UpdateAsync(Guid id, UpdateNfcDeviceDto dto, CancellationToken ct)
    {
        var device = await db.NfcDevices.FindAsync([id], ct);
        if (device is null)
        {
            return null;
        }

        if (dto.Name is not null)
        {
            device.Name = dto.Name;
        }

        if (dto.PrinterId.HasValue && !await db.Printers.AnyAsync(p => p.Id == dto.PrinterId.Value, ct))
        {
            throw new ArgumentException($"Printer with ID {dto.PrinterId} not found.");
        }

        device.PrinterId = dto.PrinterId;

        device.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        await db.Entry(device).Reference(d => d.Printer).LoadAsync(ct);
        return MapToDto(device, DateTime.UtcNow);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var device = await db.NfcDevices.FindAsync([id], ct);
        if (device is null)
        {
            return false;
        }

        db.NfcDevices.Remove(device);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("NFC device deleted: {Name} ({Id})", device.Name, device.Id);
        return true;
    }

    public async Task<NfcDeviceDto?> ProcessHeartbeatAsync(NfcDeviceHeartbeatDto dto, CancellationToken ct)
    {
        var printerId = Guid.TryParse(dto.PrinterId, out var pid) ? pid : (Guid?)null;
        if (printerId is null)
        {
            return null;
        }

        var device = await db.NfcDevices
            .Include(d => d.Printer)
            .FirstOrDefaultAsync(d => d.PrinterId == printerId, ct);

        if (device is null)
        {
            device = new NfcDevice
            {
                Id = Guid.NewGuid(),
                Name = $"NFC Reader ({dto.Ip ?? "unknown"})",
                PrinterId = printerId,
                IpAddress = dto.Ip,
                FirmwareVersion = dto.FirmwareVersion,
                CreatedAt = DateTime.UtcNow
            };
            db.NfcDevices.Add(device);
            logger.LogInformation("NFC device auto-registered via heartbeat: {Ip} → printer {PrinterId}", dto.Ip, printerId);
        }

        device.WifiRssi = dto.WifiRssi;
        device.NfcReaderOk = dto.NfcReaderOk;
        device.FreeHeap = dto.FreeHeap;
        device.IpAddress = dto.Ip ?? device.IpAddress;
        device.FirmwareVersion = dto.FirmwareVersion ?? device.FirmwareVersion;
        device.LastHeartbeat = DateTime.UtcNow;
        device.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return MapToDto(device, DateTime.UtcNow);
    }

    public async Task<NfcScanHistoryDto?> ProcessScanEventAsync(NfcScanEventDto dto, CancellationToken ct)
    {
        var printerId = Guid.TryParse(dto.PrinterId, out var pid) ? pid : (Guid?)null;
        if (printerId is null)
        {
            return null;
        }

        var device = await db.NfcDevices
            .FirstOrDefaultAsync(d => d.PrinterId == printerId, ct);

        if (device is null)
        {
            return null;
        }

        var scanEvent = new NfcScanEvent
        {
            Id = Guid.NewGuid(),
            NfcDeviceId = device.Id,
            SpoolId = dto.SpoolId,
            TagFormat = dto.TagFormat ?? "nfc",
            MaterialType = dto.MaterialType,
            BrandName = dto.BrandName,
            Action = dto.SpoolId.HasValue ? "spool_set" : "tag_read",
            ScannedAt = DateTime.UtcNow
        };

        db.NfcScanEvents.Add(scanEvent);

        device.LastScanAt = scanEvent.ScannedAt;
        device.LastScannedSpoolId = dto.SpoolId;
        device.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "NFC scan event: device {DeviceId}, spool {SpoolId}, format {Format}",
            device.Id, dto.SpoolId, dto.TagFormat);

        return new NfcScanHistoryDto
        {
            Id = scanEvent.Id,
            NfcDeviceId = device.Id,
            DeviceName = device.Name,
            SpoolId = scanEvent.SpoolId,
            TagFormat = scanEvent.TagFormat,
            MaterialType = scanEvent.MaterialType,
            BrandName = scanEvent.BrandName,
            Action = scanEvent.Action,
            ScannedAt = scanEvent.ScannedAt
        };
    }

    public async Task<NfcScanHistoryDto[]> GetScanHistoryAsync(Guid deviceId, int limit, int offset, CancellationToken ct)
    {
        return await db.NfcScanEvents
            .Where(s => s.NfcDeviceId == deviceId)
            .OrderByDescending(s => s.ScannedAt)
            .Skip(offset)
            .Take(limit)
            .Select(s => new NfcScanHistoryDto
            {
                Id = s.Id,
                NfcDeviceId = s.NfcDeviceId,
                DeviceName = s.NfcDevice.Name,
                SpoolId = s.SpoolId,
                TagFormat = s.TagFormat,
                MaterialType = s.MaterialType,
                BrandName = s.BrandName,
                Action = s.Action,
                ScannedAt = s.ScannedAt
            })
            .ToArrayAsync(ct);
    }

    private static NfcDeviceDto MapToDto(NfcDevice d, DateTime now)
    {
        var isOnline = d.LastHeartbeat.HasValue && (now - d.LastHeartbeat.Value) < HeartbeatTimeout;
        return new NfcDeviceDto
        {
            Id = d.Id,
            Name = d.Name,
            IpAddress = d.IpAddress,
            PrinterId = d.PrinterId,
            PrinterName = d.Printer?.Name,
            FirmwareVersion = d.FirmwareVersion,
            WifiRssi = d.WifiRssi,
            NfcReaderOk = d.NfcReaderOk,
            FreeHeap = d.FreeHeap,
            IsOnline = isOnline,
            LastHeartbeat = d.LastHeartbeat,
            LastScanAt = d.LastScanAt,
            LastScannedSpoolId = d.LastScannedSpoolId,
            CreatedAt = d.CreatedAt,
            UpdatedAt = d.UpdatedAt
        };
    }
}
