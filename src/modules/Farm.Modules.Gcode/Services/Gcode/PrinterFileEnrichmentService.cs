using System.Security.Cryptography;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Printers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Modules.Gcode.Services.Gcode;

/// <summary>
/// Bounded per-file metadata and thumbnail acquisition under an already held, confirmed-idle catalog lease.
/// Remote I/O never runs inside a database transaction; every publication is fenced and rechecked against
/// the captured source identity. Enrichment never advances a location's observation revision.
/// </summary>
public sealed class PrinterFileEnrichmentService(
    IDbContextFactory<AppDbContext> dbFactory,
    PrinterFileThumbnailStore store,
    IBackendCapabilityFactory capabilities,
    IPrintersRepository printers,
    IPrinterStatusCacheReader statusCache,
    ILogger<PrinterFileEnrichmentService> logger,
    TimeProvider? timeProvider = null)
{
    private const int MaximumReasonLength = 128;
    private const int UnsupportedBatchSize = 256;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    /// <summary>Whether this printer has due enrichment for the lease's current authority.</summary>
    public async Task<bool> HasDueWorkAsync(IPrinterFileLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await PrinterFileEnrichmentSchedule.AnyDueAsync(
            db, lease.PrinterId, lease.ConfigurationGeneration, lease.MutationGeneration, UtcNow, ct);
    }

    /// <summary>Processes at most one bounded slice and returns the number of published outcomes.</summary>
    public async Task<int> RunSliceAsync(IPrinterFileLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Token.IsCancellationRequested ||
            !PrinterFileIndexPolicy.IsConfirmedIdle(statusCache.GetSnapshot(lease.PrinterId), UtcNow))
        {
            return 0;
        }

        Printer? printer = await printers.FindByIdWithIncludesAsync(lease.PrinterId, lease.Token);
        if (printer is null)
        {
            return 0;
        }

        if (!capabilities.TryGetFileEnrichmentClientTyped((PrinterBackend)printer.Backend, out ISupportsPrinterFileEnrichment? client) ||
            client is null)
        {
            return await PublishUnsupportedAsync(lease, ct);
        }

        List<Guid> due;
        await using (AppDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            due = await PrinterFileEnrichmentSchedule.DueLocations(
                    db, lease.PrinterId, lease.ConfigurationGeneration, lease.MutationGeneration, UtcNow)
                .Select(x => x.Id)
                .Take(PrinterFileEnrichmentLimits.MaximumFilesPerSlice)
                .ToListAsync(lease.Token);
        }

        int published = 0;
        long budget = PrinterFileEnrichmentLimits.MaximumBytesPerSlice;
        foreach (Guid locationId in due)
        {
            if (lease.Token.IsCancellationRequested || budget <= 0 ||
                !PrinterFileIndexPolicy.IsConfirmedIdle(statusCache.GetSnapshot(lease.PrinterId), UtcNow) ||
                !await lease.IsCurrentAsync(ct))
            {
                break;
            }

            FileOutcome? outcome = await AcquireAsync(lease, printer, client, locationId, budget, ct);
            if (outcome is null)
            {
                break;
            }

            budget -= outcome.ChargedBytes;
            if (await PublishAsync(lease, outcome, ct))
            {
                published++;
            }

            if (outcome.PrinterBusy)
            {
                break;
            }
        }

        return published;
    }

    private async Task<FileOutcome?> AcquireAsync(
        IPrinterFileLease lease,
        Printer printer,
        ISupportsPrinterFileEnrichment client,
        Guid locationId,
        long budget,
        CancellationToken ct)
    {
        PrinterFileLocation? location;
        PrinterFileEnrichment? previous;
        await using (AppDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            location = await db.PrinterFileLocations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == locationId, lease.Token);
            previous = await db.PrinterFileEnrichments.AsNoTracking().Include(x => x.Artifact)
                .SingleOrDefaultAsync(x => x.LocationId == locationId, lease.Token);
        }

        if (location is null || !IsCurrent(location, lease))
        {
            return FileOutcome.Skipped(locationId);
        }

        string identity = PrinterFileEnrichmentIdentity.For(location);
        bool sameSource = previous is not null && previous.SourceIdentity == identity;
        bool metadataSettled = sameSource && previous!.ExtractorVersion >= PrinterFileEnrichmentLimits.ExtractorVersion &&
            previous.MetadataState is PrinterFileEnrichmentState.Ready or PrinterFileEnrichmentState.Partial or
                PrinterFileEnrichmentState.Absent or PrinterFileEnrichmentState.Unsupported;
        bool thumbnailSettled = sameSource && previous!.ArtifactId is not null &&
            previous.ExtractorVersion >= PrinterFileEnrichmentLimits.ExtractorVersion &&
            previous.ThumbnailState == PrinterFileEnrichmentState.Ready;

        // A metadata-only retry must not download an image that is already current.
        bool captureThumbnail = !(thumbnailSettled &&
            previous!.MetadataState is PrinterFileEnrichmentState.Deferred or PrinterFileEnrichmentState.Failed);
        Guid? reservation = null;
        string? reservationReason = null;
        if (captureThumbnail)
        {
            if (budget < PrinterFileEnrichmentLimits.MaximumImageBytes)
            {
                return null;
            }

            PrinterFileThumbnailReservationResult reserved = await store.ReserveAsync(lease.Token);
            reservation = reserved.ReservationId;
            reservationReason = reserved.ReservationId is null ? reserved.Reason ?? "cache_full" : null;
            if (reservation is null && metadataSettled)
            {
                // Do not repeat remote metadata reads while only the image is blocked by local capacity.
                return new FileOutcome(locationId, identity, location.MutationGeneration)
                {
                    MetadataState = previous!.MetadataState,
                    MetadataReason = previous.MetadataReason,
                    MetadataSource = previous.MetadataSource,
                    PreserveMetadata = true,
                    CaptureRequested = true,
                    ThumbnailState = PrinterFileEnrichmentState.Deferred,
                    ThumbnailReason = reservationReason,
                };
            }
        }

        PrinterFileEnrichmentContent content;
        bool capture = reservation is not null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lease.Token);
            deadline.CancelAfter(PrinterFileEnrichmentLimits.FileDeadline);
            try
            {
                content = await client.EnrichFileAsync(
                    printer.BackendUrl,
                    new PrinterFileInventoryEntry(
                        location.StorageRoot, location.RelativePath, location.SizeBytes,
                        ProviderVersion: location.ProviderVersion, ProviderHash: location.ProviderDigest,
                        ModifiedValue: location.ModifiedValue, DisplayName: location.DisplayName),
                    printer.Credential,
                    capture,
                    deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !lease.Token.IsCancellationRequested)
            {
                content = new(null, PrinterFileEnrichmentState.Deferred,
                    capture ? PrinterFileEnrichmentState.Deferred : PrinterFileEnrichmentState.Pending,
                    MetadataReason: "deadline", ThumbnailReason: capture ? "deadline" : null);
            }
        }
        catch (OperationCanceledException) when (lease.Token.IsCancellationRequested)
        {
            await ReleaseQuietlyAsync(reservation, ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Printer file enrichment failed for {LocationId}: {FailureType}", locationId, ex.GetType().Name);
            content = new(null, PrinterFileEnrichmentState.Failed,
                capture ? PrinterFileEnrichmentState.Failed : PrinterFileEnrichmentState.Pending,
                MetadataReason: "transfer_failed", ThumbnailReason: capture ? "transfer_failed" : null);
        }

        var outcome = new FileOutcome(locationId, identity, location.MutationGeneration)
        {
            Metadata = content.Metadata,
            MetadataState = Settle(content.MetadataState),
            MetadataReason = Bound(content.MetadataReason ?? (content.MetadataState == PrinterFileEnrichmentState.Pending ? "pending" : null)),
            MetadataSource = Bound(content.MetadataSource),
            CaptureRequested = captureThumbnail,
            ChargedBytes = Math.Max(capture ? PrinterFileEnrichmentLimits.MaximumImageBytes : 0, content.ResponseBytes),
            PrinterBusy = content.MetadataReason == "capture_paused" || content.ThumbnailReason == "capture_paused",
        };

        if (!captureThumbnail)
        {
            return outcome;
        }

        if (reservation is not Guid reservationId)
        {
            outcome.ThumbnailState = PrinterFileEnrichmentState.Deferred;
            outcome.ThumbnailReason = reservationReason;
            return outcome;
        }

        outcome.ThumbnailState = Settle(content.ThumbnailState);
        outcome.ThumbnailReason = Bound(content.ThumbnailReason);
        if (content.Image is not { Length: > 0 } image)
        {
            if (outcome.ThumbnailState == PrinterFileEnrichmentState.Ready)
            {
                outcome.ThumbnailState = PrinterFileEnrichmentState.Failed;
                outcome.ThumbnailReason ??= "thumbnail_missing";
            }

            await ReleaseQuietlyAsync(reservationId, ct);
            return outcome;
        }

        if (sameSource && previous!.Artifact is { RetiredAtUtc: null } current &&
            string.Equals(current.ContentSha256, Convert.ToHexString(SHA256.HashData(image)), StringComparison.Ordinal))
        {
            await ReleaseQuietlyAsync(reservationId, ct);
            outcome.ArtifactId = current.Id;
            outcome.ThumbnailState = PrinterFileEnrichmentState.Ready;
            outcome.ThumbnailReason = null;
            return outcome;
        }

        try
        {
            outcome.ArtifactId = await store.WriteAsync(reservationId, image, content.ContentType, lease.Token);
            outcome.ThumbnailState = PrinterFileEnrichmentState.Ready;
            outcome.ThumbnailReason = null;
        }
        catch (InvalidDataException)
        {
            outcome.ThumbnailState = PrinterFileEnrichmentState.Failed;
            outcome.ThumbnailReason = "invalid_image";
            await ReleaseQuietlyAsync(reservationId, ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or DbUpdateException)
        {
            logger.LogWarning("Printer thumbnail write failed for {LocationId}: {FailureType}", locationId, ex.GetType().Name);
            outcome.ThumbnailState = PrinterFileEnrichmentState.Deferred;
            outcome.ThumbnailReason = "storage_unavailable";
            await ReleaseQuietlyAsync(reservationId, ct);
        }
        catch (OperationCanceledException) when (lease.Token.IsCancellationRequested)
        {
            await ReleaseQuietlyAsync(reservationId, ct);
            return null;
        }

        return outcome;
    }

    private async Task<bool> PublishAsync(IPrinterFileLease lease, FileOutcome outcome, CancellationToken ct)
    {
        if (outcome.IsSkipped)
        {
            return false;
        }

        try
        {
            await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (!await lease.FencePublicationAsync(db, ct))
            {
                logger.LogInformation("Discarded superseded printer file enrichment for {LocationId}", outcome.LocationId);
                return false;
            }

            PrinterFileLocation? location = await db.PrinterFileLocations
                .SingleOrDefaultAsync(x => x.Id == outcome.LocationId, ct);
            if (location is null || !IsCurrent(location, lease) ||
                location.MutationGeneration != outcome.MutationGeneration ||
                PrinterFileEnrichmentIdentity.For(location) != outcome.SourceIdentity)
            {
                // Any written image is unreferenced and is reclaimed by store cleanup.
                logger.LogInformation("Discarded printer file enrichment for changed source {LocationId}", outcome.LocationId);
                return false;
            }

            DateTime now = UtcNow;
            PrinterFileEnrichment? row = await db.PrinterFileEnrichments
                .SingleOrDefaultAsync(x => x.LocationId == location.Id, ct);
            if (row is null)
            {
                row = new PrinterFileEnrichment { LocationId = location.Id, NextAttemptAtUtc = now };
                db.PrinterFileEnrichments.Add(row);
            }

            if (row.SourceIdentity != outcome.SourceIdentity)
            {
                row.ArtifactId = null;
                row.SourceIdentity = outcome.SourceIdentity;
            }

            ApplyMetadata(location, row, outcome, now);
            ApplyThumbnail(row, outcome);
            row.ExtractorVersion = PrinterFileEnrichmentLimits.ExtractorVersion;
            row.LastAttemptAtUtc = now;
            bool transient = IsTransient(row.MetadataState) || IsTransient(row.ThumbnailState);
            if (transient && !outcome.PrinterBusy)
            {
                row.ConsecutiveFailures = Math.Min(30, row.ConsecutiveFailures + 1);
            }
            else if (!transient)
            {
                row.ConsecutiveFailures = 0;
            }

            row.NextAttemptAtUtc = transient
                ? now + PrinterFileEnrichmentSchedule.RetryDelay(
                    location.Id, outcome.PrinterBusy ? 1 : Math.Max(1, row.ConsecutiveFailures))
                : now + PrinterFileEnrichmentLimits.RevalidationInterval;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("Printer file enrichment raced with catalog publication for {LocationId}", outcome.LocationId);
            return false;
        }
    }

    private void ApplyMetadata(PrinterFileLocation location, PrinterFileEnrichment row, FileOutcome outcome, DateTime now)
    {
        row.MetadataState = outcome.MetadataState;
        row.MetadataReason = outcome.MetadataReason;
        if (outcome.PreserveMetadata)
        {
            row.MetadataSource = outcome.MetadataSource;
            return;
        }

        if (outcome.MetadataState == PrinterFileEnrichmentState.Absent)
        {
            location.Metadata = null;
            location.DetailsFetchedAtUtc = now;
            row.MetadataSource = outcome.MetadataSource;
            row.AcquiredAtUtc = now;
            return;
        }

        if (outcome.MetadataState is not (PrinterFileEnrichmentState.Ready or PrinterFileEnrichmentState.Partial))
        {
            return;
        }

        PrinterPrintMetadata? metadata = outcome.Metadata;
        if (metadata is not null && (!metadata.IsValid() || !metadata.FitsStorage()))
        {
            logger.LogWarning(
                "Skipping print metadata for catalog location {LocationId}: invalid or larger than {MaxLength} characters",
                location.Id,
                PrinterPrintMetadata.MaximumStoredJsonLength);
            row.MetadataState = PrinterFileEnrichmentState.Partial;
            row.MetadataReason = "metadata_limit";
            return;
        }

        // Rich detail is presentation state; the observation revision pins operations and must not move.
        location.Metadata = metadata is { HasValues: true } ? metadata : null;
        location.DetailsFetchedAtUtc = now;
        row.MetadataSource = outcome.MetadataSource;
        row.AcquiredAtUtc = now;
    }

    private static void ApplyThumbnail(PrinterFileEnrichment row, FileOutcome outcome)
    {
        if (!outcome.CaptureRequested)
        {
            return;
        }

        row.ThumbnailState = outcome.ThumbnailState;
        row.ThumbnailReason = outcome.ThumbnailReason;
        if (outcome.ArtifactId is Guid artifactId)
        {
            row.ArtifactId = artifactId;
        }
        else if (outcome.ThumbnailState is PrinterFileEnrichmentState.Absent or PrinterFileEnrichmentState.Unsupported)
        {
            row.ArtifactId = null;
        }

        // Transient failures keep a previous same-source image, which readers report as stale.
    }

    private async Task<int> PublishUnsupportedAsync(IPrinterFileLease lease, CancellationToken ct)
    {
        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await lease.FencePublicationAsync(db, ct))
        {
            return 0;
        }

        DateTime now = UtcNow;
        List<PrinterFileLocation> locations = await PrinterFileEnrichmentSchedule.DueLocations(
                db, lease.PrinterId, lease.ConfigurationGeneration, lease.MutationGeneration, now)
            .Take(UnsupportedBatchSize).ToListAsync(ct);
        List<Guid> ids = locations.Select(x => x.Id).ToList();
        Dictionary<Guid, PrinterFileEnrichment> rows = await db.PrinterFileEnrichments
            .Where(x => ids.Contains(x.LocationId)).ToDictionaryAsync(x => x.LocationId, ct);
        foreach (PrinterFileLocation location in locations)
        {
            if (!rows.TryGetValue(location.Id, out PrinterFileEnrichment? row))
            {
                row = new PrinterFileEnrichment { LocationId = location.Id };
                db.PrinterFileEnrichments.Add(row);
            }

            row.SourceIdentity = PrinterFileEnrichmentIdentity.For(location);
            row.ExtractorVersion = PrinterFileEnrichmentLimits.ExtractorVersion;
            row.MetadataState = PrinterFileEnrichmentState.Unsupported;
            row.ThumbnailState = PrinterFileEnrichmentState.Unsupported;
            row.MetadataReason = "enrichment_unsupported";
            row.ThumbnailReason = "enrichment_unsupported";
            row.ArtifactId = null;
            row.ConsecutiveFailures = 0;
            row.LastAttemptAtUtc = now;
            row.NextAttemptAtUtc = now + PrinterFileEnrichmentLimits.RevalidationInterval;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return locations.Count;
    }

    private async Task ReleaseQuietlyAsync(Guid? reservationId, CancellationToken ct)
    {
        if (reservationId is not Guid id)
        {
            return;
        }

        try
        {
            await store.ReleaseAsync(id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unreleased reservation stays charged until its expiry is recovered by cleanup.
            logger.LogWarning("Printer thumbnail reservation release failed: {FailureType}", ex.GetType().Name);
        }
    }

    private static bool IsCurrent(PrinterFileLocation location, IPrinterFileLease lease) =>
        location.SourcePrinterId == lease.PrinterId &&
        location.RegisteredPrinterId == lease.PrinterId &&
        location.Availability == PrinterFileAvailability.Available &&
        location.ConfigurationGeneration == lease.ConfigurationGeneration &&
        location.MutationGeneration == lease.MutationGeneration;

    private static bool IsTransient(PrinterFileEnrichmentState state) =>
        state is PrinterFileEnrichmentState.Deferred or PrinterFileEnrichmentState.Failed or PrinterFileEnrichmentState.Pending;

    // An attempt never leaves a hot "pending" outcome; that would bypass retry backoff.
    private static PrinterFileEnrichmentState Settle(PrinterFileEnrichmentState state) =>
        state == PrinterFileEnrichmentState.Pending ? PrinterFileEnrichmentState.Deferred : state;

    private static string? Bound(string? value) =>
        value is { Length: > MaximumReasonLength } ? value[..MaximumReasonLength] : value;

    private sealed class FileOutcome(Guid locationId, string sourceIdentity, long mutationGeneration)
    {
        public Guid LocationId { get; } = locationId;

        public string SourceIdentity { get; } = sourceIdentity;

        public long MutationGeneration { get; } = mutationGeneration;

        public bool IsSkipped { get; private init; }

        public PrinterPrintMetadata? Metadata { get; init; }

        public PrinterFileEnrichmentState MetadataState { get; init; }

        public string? MetadataReason { get; init; }

        public string? MetadataSource { get; init; }

        public bool PreserveMetadata { get; init; }

        public bool CaptureRequested { get; init; }

        public PrinterFileEnrichmentState ThumbnailState { get; set; }

        public string? ThumbnailReason { get; set; }

        public Guid? ArtifactId { get; set; }

        public long ChargedBytes { get; init; }

        public bool PrinterBusy { get; init; }

        public static FileOutcome Skipped(Guid locationId) => new(locationId, string.Empty, 0) { IsSkipped = true };
    }
}
