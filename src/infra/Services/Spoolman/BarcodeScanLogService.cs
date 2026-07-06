using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Spoolman;

/// <summary>
/// Stores barcode scan diagnostics when explicitly enabled in application settings.
/// </summary>
public class BarcodeScanLogService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISettingsService settingsService,
    ILogger<BarcodeScanLogService> logger) : IBarcodeScanLogService
{
    public async Task LogAsync(BarcodeScanLog log, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(log);

        if (!IsEnabled())
        {
            return;
        }

        try
        {
            log.Timestamp = log.Timestamp == default ? DateTime.UtcNow : log.Timestamp.ToUniversalTime();
            log.Barcode = Truncate(log.Barcode.Trim(), 256) ?? string.Empty;
            log.UserId = Truncate(log.UserId, 450);
            log.Message = Truncate(log.Message, 1024);

            await using AppDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
            _ = db.BarcodeScanLogs.Add(log);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist barcode scan diagnostic log.");
        }
    }

    public async Task<IReadOnlyList<BarcodeScanLog>> GetRecentAsync(int limit, CancellationToken ct)
    {
        int boundedLimit = Math.Clamp(limit, 1, 500);
        await using AppDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.BarcodeScanLogs
            .AsNoTracking()
            .OrderByDescending(l => l.Timestamp)
            .Take(boundedLimit)
            .ToListAsync(ct);
    }

    private bool IsEnabled()
    {
        try
        {
            return settingsService.Get<SpoolmanSettings>().BarcodeScanDebugLoggingEnabled;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read barcode scan debug logging setting; diagnostics are disabled.");
            return false;
        }
    }

    private static string? Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];
}
