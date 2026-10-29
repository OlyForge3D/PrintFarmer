using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Gcode.PrinterFiles;

/// <summary>Durable due-work rules for background per-file enrichment under the idle catalog lease.</summary>
public static class PrinterFileEnrichmentSchedule
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(60),
    ];

    /// <summary>
    /// Current-authority, available locations needing work: legacy rows without enrichment, pending
    /// outcomes, older extractors, or elapsed retry/revalidation times.
    /// </summary>
    public static IQueryable<PrinterFileLocation> DueQuery(
        AppDbContext db, Guid printerId, long configurationGeneration, long mutationGeneration, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.PrinterFileLocations.Where(location =>
            location.SourcePrinterId == printerId &&
            location.RegisteredPrinterId == printerId &&
            location.Availability == PrinterFileAvailability.Available &&
            location.ConfigurationGeneration == configurationGeneration &&
            location.MutationGeneration == mutationGeneration &&
            !db.PrinterFileEnrichments.Any(row => row.LocationId == location.Id &&
                row.MetadataState != PrinterFileEnrichmentState.Pending &&
                row.ThumbnailState != PrinterFileEnrichmentState.Pending &&
                row.ExtractorVersion >= PrinterFileEnrichmentLimits.ExtractorVersion &&
                row.NextAttemptAtUtc > now));
    }

    /// <summary>Fair order: never-attempted first, then oldest eligibility, then a stable key.</summary>
    public static IQueryable<PrinterFileLocation> DueLocations(
        AppDbContext db, Guid printerId, long configurationGeneration, long mutationGeneration, DateTime now) =>
        DueQuery(db, printerId, configurationGeneration, mutationGeneration, now)
            .OrderBy(location => db.PrinterFileEnrichments
                .Where(row => row.LocationId == location.Id)
                .Select(row => (DateTime?)row.NextAttemptAtUtc)
                .FirstOrDefault() ?? DateTime.MinValue)
            .ThenBy(location => location.Id);

    public static Task<bool> AnyDueAsync(
        AppDbContext db, Guid printerId, long configurationGeneration, long mutationGeneration, DateTime now,
        CancellationToken ct = default) =>
        DueQuery(db, printerId, configurationGeneration, mutationGeneration, now).AnyAsync(ct);

    /// <summary>Printers with due enrichment under their current scan-state authority, in one bounded query.</summary>
    public static Task<List<Guid>> DuePrinterIdsAsync(AppDbContext db, DateTime now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.PrinterFileScanStates
            .Where(state => state.Enabled && db.PrinterFileLocations.Any(location =>
                location.SourcePrinterId == state.SourcePrinterId &&
                location.RegisteredPrinterId == state.SourcePrinterId &&
                location.Availability == PrinterFileAvailability.Available &&
                location.ConfigurationGeneration == state.ConfigurationGeneration &&
                location.MutationGeneration == state.MutationGeneration &&
                !db.PrinterFileEnrichments.Any(row => row.LocationId == location.Id &&
                    row.MetadataState != PrinterFileEnrichmentState.Pending &&
                    row.ThumbnailState != PrinterFileEnrichmentState.Pending &&
                    row.ExtractorVersion >= PrinterFileEnrichmentLimits.ExtractorVersion &&
                    row.NextAttemptAtUtc > now)))
            .Select(state => state.SourcePrinterId)
            .ToListAsync(ct);
    }

    /// <summary>Most recent per-file enrichment attempt for each printer; it survives lease release, unlike lease expiry.</summary>
    public static async Task<Dictionary<Guid, DateTime?>> LastAttemptByPrinterAsync(
        AppDbContext db, IReadOnlyCollection<Guid> printerIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(printerIds);
        if (printerIds.Count == 0)
        {
            return [];
        }

        return await db.PrinterFileEnrichments
            .Where(row => printerIds.Contains(row.Location.SourcePrinterId))
            .GroupBy(row => row.Location.SourcePrinterId)
            .Select(group => new { PrinterId = group.Key, LastAttemptAtUtc = group.Max(row => row.LastAttemptAtUtc) })
            .ToDictionaryAsync(x => x.PrinterId, x => x.LastAttemptAtUtc, ct);
    }

    public static bool IsDue(PrinterFileEnrichment? row, DateTime now) =>
        row is null ||
        row.MetadataState == PrinterFileEnrichmentState.Pending ||
        row.ThumbnailState == PrinterFileEnrichmentState.Pending ||
        row.ExtractorVersion < PrinterFileEnrichmentLimits.ExtractorVersion ||
        row.NextAttemptAtUtc <= now;

    /// <summary>Bounded 1/5/15/60 minute backoff with deterministic per-file jitter of up to 20 percent.</summary>
    public static TimeSpan RetryDelay(Guid locationId, int consecutiveFailures)
    {
        TimeSpan delay = RetryDelays[Math.Clamp(consecutiveFailures, 1, RetryDelays.Length) - 1];
        byte[] bytes = locationId.ToByteArray();
        uint seed = BitConverter.ToUInt32(bytes, 0) ^ (uint)consecutiveFailures;
        double fraction = (seed % 1000) / 1000d;
        return delay * (1 - (fraction * 0.2));
    }
}
