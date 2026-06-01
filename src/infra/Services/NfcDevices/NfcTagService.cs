using System.Collections.Concurrent;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.NfcDevices;

/// <summary>
/// Handles NFC tag binding lookups, SpoolLastSeenAt updates, and SignalR broadcasts.
/// Maintains a per-device in-memory queue for scan events that arrive while a device
/// is considered offline (heartbeat timeout) — events are flushed when the device reconnects.
/// </summary>
public class NfcTagService(
    IServiceScopeFactory scopeFactory,
    IHubContext<NfcHub> hub,
    ILogger<NfcTagService> logger) : INfcTagService
{
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMinutes(3);

    // Per-device queue of events pending broadcast (offline scenario)
    private readonly ConcurrentDictionary<Guid, Queue<PendingNfcEvent>> _offlineQueues = new();

    public async Task ProcessTagReadAsync(
        string tagUid,
        Guid nfcDeviceId,
        Guid? printerId,
        DateTime readAt,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var binding = await db.NfcTagBindings
            .Include(b => b.Printer)
            .FirstOrDefaultAsync(b => b.TagUid == tagUid, ct);

        bool deviceIsOnline = await IsDeviceOnlineAsync(db, nfcDeviceId, ct);

        if (binding is not null)
        {
            binding.SpoolLastSeenAt = readAt;
            binding.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            var payload = new
            {
                tagUid,
                spoolId = binding.SpoolId,
                spoolName = binding.SpoolName,
                printerId = binding.PrinterId,
                trayId = binding.TrayId,
                readAt
            };

            if (deviceIsOnline)
            {
                await hub.Clients.All.SendAsync(NfcHubEvents.TagRead, payload, ct);
                logger.LogInformation(
                    "nfctagread: tag {TagUid} → spool {SpoolId} (device {DeviceId})",
                    tagUid, binding.SpoolId, nfcDeviceId);
            }
            else
            {
                EnqueueOffline(nfcDeviceId, new PendingNfcEvent(NfcHubEvents.TagRead, payload));
                logger.LogDebug(
                    "Device {DeviceId} offline — queued nfctagread for tag {TagUid}",
                    nfcDeviceId, tagUid);
            }
        }
        else
        {
            var payload = new { tagUid, printerId, readAt };

            if (deviceIsOnline)
            {
                await hub.Clients.All.SendAsync(NfcHubEvents.TagUnknown, payload, ct);
                logger.LogInformation(
                    "nfctagunknown: tag {TagUid} has no binding (device {DeviceId})",
                    tagUid, nfcDeviceId);
            }
            else
            {
                EnqueueOffline(nfcDeviceId, new PendingNfcEvent(NfcHubEvents.TagUnknown, payload));
                logger.LogDebug(
                    "Device {DeviceId} offline — queued nfctagunknown for tag {TagUid}",
                    nfcDeviceId, tagUid);
            }
        }
    }

    public async Task<NfcTagBindingDto> LinkTagAsync(LinkNfcTagRequest request, CancellationToken ct)
    {
        const int maxRetries = 3;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var binding = await db.NfcTagBindings
                .Include(b => b.Printer)
                .FirstOrDefaultAsync(b => b.TagUid == request.TagUid, ct);

            if (binding is null)
            {
                binding = new NfcTagBinding
                {
                    Id = Guid.NewGuid(),
                    TagUid = request.TagUid,
                    CreatedAt = DateTime.UtcNow
                };
                db.NfcTagBindings.Add(binding);
            }

            binding.SpoolId = request.SpoolId;
            binding.SpoolName = request.SpoolName;
            binding.PrinterId = request.PrinterId;
            binding.TrayId = request.TrayId;
            binding.UpdatedAt = DateTime.UtcNow;

            if (request.PrinterId.HasValue)
            {
                await db.Entry(binding).Reference(b => b.Printer).LoadAsync(ct);
            }

            try
            {
                await db.SaveChangesAsync(ct);

                logger.LogInformation(
                    "NFC tag {TagUid} linked → spool {SpoolId}, printer {PrinterId}",
                    request.TagUid, request.SpoolId, request.PrinterId);

                return MapToDto(binding);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                // Another concurrent caller inserted first — detach the failed entity and retry
                logger.LogDebug(
                    "Unique constraint race on TagUid {TagUid}, attempt {Attempt} — retrying",
                    request.TagUid, attempt + 1);

                db.Entry(binding).State = EntityState.Detached;
            }
        }

        // Final fallback: return the existing binding (winner of the race)
        await using var fallbackScope = scopeFactory.CreateAsyncScope();
        var fallbackDb = fallbackScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existing = await fallbackDb.NfcTagBindings
            .Include(b => b.Printer)
            .FirstAsync(b => b.TagUid == request.TagUid, ct);

        existing.SpoolId = request.SpoolId;
        existing.SpoolName = request.SpoolName;
        existing.PrinterId = request.PrinterId;
        existing.TrayId = request.TrayId;
        existing.UpdatedAt = DateTime.UtcNow;
        await fallbackDb.SaveChangesAsync(ct);

        logger.LogInformation(
            "NFC tag {TagUid} linked (after race resolution) → spool {SpoolId}, printer {PrinterId}",
            request.TagUid, request.SpoolId, request.PrinterId);

        return MapToDto(existing);
    }

    public async Task<IReadOnlyList<NfcTagBindingDto>> ListBindingsAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var bindings = await db.NfcTagBindings
            .Include(b => b.Printer)
            .OrderBy(b => b.CreatedAt)
            .ToListAsync(ct);

        return bindings.Select(MapToDto).ToList();
    }

    public async Task<bool> DeleteBindingAsync(Guid id, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var binding = await db.NfcTagBindings.FindAsync([id], ct);
        if (binding is null)
        {
            return false;
        }

        db.NfcTagBindings.Remove(binding);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("NFC tag binding {Id} (tag {TagUid}) deleted", id, binding.TagUid);
        return true;
    }

    public async Task FlushOfflineQueueAsync(Guid nfcDeviceId, CancellationToken ct)
    {
        if (!_offlineQueues.TryRemove(nfcDeviceId, out var queue) || queue.Count == 0)
        {
            return;
        }

        logger.LogInformation(
            "Flushing {Count} queued NFC event(s) for device {DeviceId} on reconnect",
            queue.Count, nfcDeviceId);

        while (queue.TryDequeue(out var evt))
        {
            await hub.Clients.All.SendAsync(evt.EventName, evt.Payload, ct);
        }
    }

    private static async Task<bool> IsDeviceOnlineAsync(AppDbContext db, Guid nfcDeviceId, CancellationToken ct)
    {
        var lastHeartbeat = await db.NfcDevices
            .Where(d => d.Id == nfcDeviceId)
            .Select(d => d.LastHeartbeat)
            .FirstOrDefaultAsync(ct);

        return lastHeartbeat.HasValue &&
               (DateTime.UtcNow - lastHeartbeat.Value) < HeartbeatTimeout;
    }

    private void EnqueueOffline(Guid nfcDeviceId, PendingNfcEvent evt)
    {
        var queue = _offlineQueues.GetOrAdd(nfcDeviceId, _ => new Queue<PendingNfcEvent>());
        lock (queue)
        {
            // Cap the queue to avoid unbounded growth
            if (queue.Count < 100)
            {
                queue.Enqueue(evt);
            }
        }
    }

    private static NfcTagBindingDto MapToDto(NfcTagBinding b) => new()
    {
        Id = b.Id,
        TagUid = b.TagUid,
        SpoolId = b.SpoolId,
        SpoolName = b.SpoolName,
        PrinterId = b.PrinterId,
        PrinterName = b.Printer?.Name,
        TrayId = b.TrayId,
        SpoolLastSeenAt = b.SpoolLastSeenAt,
        CreatedAt = b.CreatedAt,
        UpdatedAt = b.UpdatedAt
    };

    /// <summary>
    /// Detects unique constraint violations across database providers.
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        var inner = ex.InnerException;
        if (inner is null)
        {
            return false;
        }

        var message = inner.Message;

        // PostgreSQL: duplicate key value violates unique constraint
        if (message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // SQL Server: violation of UNIQUE KEY constraint / cannot insert duplicate key
        if (message.Contains("unique", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // SQLite: UNIQUE constraint failed
        if (message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private sealed record PendingNfcEvent(string EventName, object Payload);
}
