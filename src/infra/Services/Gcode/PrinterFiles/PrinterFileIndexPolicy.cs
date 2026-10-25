using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Gcode.PrinterFiles;

/// <summary>Pure scheduling rules and fresh persisted policy reads.</summary>
public static class PrinterFileIndexPolicy
{
    public static async Task<PrinterFileIndexSettings> ReadAsync(
        AppDbContext db, CancellationToken ct = default)
    {
        string? json = await db.AppSettingsEntities.AsNoTracking()
            .Where(x => x.Key == PrinterFileIndexSettings.SectionName)
            .Select(x => x.SettingsJson).SingleOrDefaultAsync(ct);
        PrinterFileIndexSettings settings = json is null
            ? new PrinterFileIndexSettings()
            : JsonSerializer.Deserialize<PrinterFileIndexSettings>(json)
                ?? throw new InvalidOperationException("Printer file index settings are invalid.");
        settings.Validate();
        return settings;
    }

    /// <summary>Hashes stored configuration, never a request-supplied endpoint.</summary>
    public static string ConfigurationFingerprint(Printer printer) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            printer.Backend,
            printer.ServerUrl,
            printer.BackendPort,
            printer.ApiKey,
            printer.Username,
            printer.Password,
            printer.IsEnabled,
        }))));

    public static TimeSpan InitialJitter(Guid printerId, PrinterFileIndexSettings settings)
    {
        byte[] hash = SHA256.HashData(printerId.ToByteArray());
        double fraction = BitConverter.ToUInt32(hash) / (double)uint.MaxValue;
        return TimeSpan.FromMinutes(settings.ScanIntervalMinutes * 0.2 * fraction);
    }

    public static TimeSpan NextDelay(
        Guid printerId, int failures, PrinterFileIndexSettings settings)
    {
        double backoffMinutes = Math.Min(
            360,
            settings.ScanIntervalMinutes * Math.Pow(2, Math.Min(Math.Max(0, failures), 10)));
        return TimeSpan.FromMinutes(Math.Min(
            360,
            backoffMinutes + InitialJitter(printerId, settings).TotalMinutes));
    }

    /// <summary>
    /// UI-normalized Idle is insufficient: legacy normalization maps unknown to Idle.
    /// Require fresh, explicit raw activity evidence without any new printer requests.
    /// </summary>
    public static bool IsConfirmedIdle(PrinterStatusCacheSnapshot? snapshot, DateTime utcNow)
    {
        if (!PrinterStatusFreshness.IsFreshOnline(snapshot, utcNow))
        {
            return false;
        }

        string? raw = snapshot!.Status.RawActivityState?.Trim();
        return raw is not null && (
            raw.Equals("idle", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("standby", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("ready", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("operational", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("complete", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("finished", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("cancelled", StringComparison.OrdinalIgnoreCase));
    }
}
