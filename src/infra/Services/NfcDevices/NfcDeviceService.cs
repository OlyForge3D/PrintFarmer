using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.NfcDevices;

public class NfcDeviceService(
    AppDbContext db,
    ILogger<NfcDeviceService> logger,
    INfcTagService? nfcTagService = null) : INfcDeviceService
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
        logger.LogInformation("NFC device registered: {Name} ({Id})", LogSanitizer.Sanitize(device.Name), device.Id);
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
        logger.LogInformation("NFC device deleted: {Name} ({Id})", LogSanitizer.Sanitize(device.Name), device.Id);
        return true;
    }

    public async Task<NfcDeviceApprovalResultDto?> ApproveAsync(Guid id, CancellationToken ct)
    {
        var device = await db.NfcDevices.FindAsync([id], ct);
        if (device is null)
        {
            return null;
        }

        var rawToken = GenerateDeviceToken();
        device.DeviceTokenHash = HashToken(rawToken);
        device.IsApproved = true;
        device.ApprovedAt ??= DateTime.UtcNow;
        device.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        logger.LogInformation("NFC device approved: {Name} ({Id})", LogSanitizer.Sanitize(device.Name), device.Id);

        return new NfcDeviceApprovalResultDto
        {
            DeviceId = device.Id,
            DeviceToken = rawToken
        };
    }

    public async Task<(NfcDeviceDto? Device, bool Unauthorized)> ProcessHeartbeatAsync(
        NfcDeviceHeartbeatDto dto,
        string? presentedToken,
        CancellationToken ct)
    {
        var printerId = Guid.TryParse(dto.PrinterId, out var pid) ? pid : (Guid?)null;
        if (printerId is null)
        {
            return (null, false);
        }

        var device = await db.NfcDevices
            .Include(d => d.Printer)
            .FirstOrDefaultAsync(d => d.PrinterId == printerId, ct);

        if (device is null)
        {
            // Claim-only: an unknown printer ID creates a pending, unapproved device row.
            // This is an announcement, not a credential — it grants no write access to
            // spool/scan data until an operator explicitly approves it.
            device = new NfcDevice
            {
                Id = Guid.NewGuid(),
                Name = $"NFC Reader ({dto.Ip ?? "unknown"})",
                PrinterId = printerId,
                IpAddress = dto.Ip,
                FirmwareVersion = dto.FirmwareVersion,
                IsApproved = false,
                CreatedAt = DateTime.UtcNow
            };
            db.NfcDevices.Add(device);
            logger.LogInformation("NFC device auto-registered via heartbeat (pending approval): {Ip} → printer {PrinterId}", LogSanitizer.Sanitize(dto.Ip), printerId);
        }
        else if (device.IsApproved && !ValidateToken(device, presentedToken))
        {
            // The printer already has an approved device bound to it; only that device's
            // real token may update its heartbeat state.
            return (null, true);
        }

        device.WifiRssi = dto.WifiRssi;
        device.NfcReaderOk = dto.NfcReaderOk;
        device.FreeHeap = dto.FreeHeap;
        device.IpAddress = dto.Ip ?? device.IpAddress;
        device.FirmwareVersion = dto.FirmwareVersion ?? device.FirmwareVersion;
        device.LastHeartbeat = DateTime.UtcNow;
        device.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        // Flush any events queued while the device was offline
        if (nfcTagService is not null)
        {
            await nfcTagService.FlushOfflineQueueAsync(device.Id, ct);
        }

        return (MapToDto(device, DateTime.UtcNow), false);
    }

    public async Task<(NfcScanHistoryDto? Result, bool Unauthorized)> ProcessScanEventAsync(
        NfcScanEventDto dto,
        string? presentedToken,
        CancellationToken ct)
    {
        var printerId = Guid.TryParse(dto.PrinterId, out var pid) ? pid : (Guid?)null;
        if (printerId is null)
        {
            return (null, false);
        }

        var device = await db.NfcDevices
            .FirstOrDefaultAsync(d => d.PrinterId == printerId, ct);

        // Reject unknown, unapproved, or token-mismatched devices identically so the
        // response gives no oracle on which of these conditions actually failed.
        if (device is null || !ValidateToken(device, presentedToken))
        {
            return (null, true);
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
            device.Id, dto.SpoolId, LogSanitizer.Sanitize(dto.TagFormat));

        // Route the event through the tag-binding service for SignalR broadcast
        if (nfcTagService is not null && dto.TagUid is not null)
        {
            await nfcTagService.ProcessTagReadAsync(dto.TagUid, device.Id, device.PrinterId, scanEvent.ScannedAt, ct);
        }

        return (new NfcScanHistoryDto
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
        }, false);
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
            IsApproved = d.IsApproved,
            ApprovedAt = d.ApprovedAt,
            CreatedAt = d.CreatedAt,
            UpdatedAt = d.UpdatedAt
        };
    }

    /// <summary>
    /// Generates a cryptographically random device token. Returned once to the caller;
    /// only its hash (<see cref="HashToken"/>) is ever persisted.
    /// </summary>
    private static string GenerateDeviceToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Validates a presented device token against an approved device's stored hash using a
    /// constant-time comparison. Unapproved devices, missing hashes, or missing/blank
    /// presented tokens always fail.
    /// </summary>
    private static bool ValidateToken(NfcDevice device, string? presentedToken)
    {
        if (!device.IsApproved || string.IsNullOrEmpty(device.DeviceTokenHash) || string.IsNullOrEmpty(presentedToken))
        {
            return false;
        }

        byte[] expected = Encoding.UTF8.GetBytes(device.DeviceTokenHash);
        byte[] presented = Encoding.UTF8.GetBytes(HashToken(presentedToken));
        return expected.Length == presented.Length && CryptographicOperations.FixedTimeEquals(expected, presented);
    }
}
