using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Farm.Modules.Gcode.Services.Gcode;

/// <summary>
/// Local preview/thumbnail reads plus the interactive remote download, distinct from background
/// enrichment and durable Copy Local. No method creates a library asset, operation, origin or queue job.
/// </summary>
public sealed class PrinterFileContentService(
    AppDbContext db,
    IPrinterFileAccessService access,
    IPrinterFileLeaseCoordinator leases,
    IPrinterStatusCacheReader status,
    IBackendClientFactory backends,
    IPrintersRepository printers,
    PrinterFileThumbnailStore thumbnails,
    IOptions<BackendTimeoutSettings> timeouts)
{
    /// <summary>
    /// Local-only: persisted metadata and background enrichment status. Never contacts the printer,
    /// acquires a lease or starts acquisition, so it answers while the printer is busy or unreachable.
    /// </summary>
    public async Task<PrinterFilePreviewDto> PreviewAsync(
        Guid actor, Guid locationId, long revision, CancellationToken ct)
    {
        Source source = await LoadAsync(actor, locationId, revision, download: false, ct);
        PrinterFileEnrichment? row = await CurrentEnrichmentAsync(source.Location, ct);
        bool supported = GetBackend(source.Printer) is ISupportsPrinterFileEnrichment;
        await ValidateLocalReadAsync(actor, source, ct);
        return Describe(source.Location, row, supported, DateTime.UtcNow);
    }

    /// <summary>
    /// Serves only a validated, locally stored image for the current source identity. A missing local
    /// artifact is reported unavailable and scheduled for background repair; there is no printer fallback.
    /// </summary>
    public async Task<PrinterFilePreviewContent> ThumbnailAsync(
        Guid actor, Guid locationId, long revision, Guid? version, CancellationToken ct)
    {
        Source source = await LoadAsync(actor, locationId, revision, download: false, ct);
        PrinterFileEnrichment? row = await CurrentEnrichmentAsync(source.Location, ct);
        if (row?.ArtifactId is not Guid artifactId)
        {
            throw new PrinterFileCommandException("thumbnail_unavailable", 404);
        }

        if (version is Guid requested && requested != artifactId)
        {
            throw new PrinterFileCommandException("thumbnail_version_changed", 404);
        }

        PrinterFilePreviewContent? image;
        try
        {
            image = await thumbnails.ReadAsync(artifactId, ct);
        }
        catch (InvalidDataException)
        {
            image = null;
        }

        if (image?.Image is null || image.ContentType is null)
        {
            await ScheduleRepairAsync(source.Location.Id, artifactId, ct);
            throw new PrinterFileCommandException("thumbnail_unavailable", 404);
        }

        await ValidateLocalReadAsync(actor, source, ct);
        PrinterFileEnrichment? current = await CurrentEnrichmentAsync(source.Location, ct);
        if (current?.ArtifactId != artifactId)
        {
            throw new PrinterFileCommandException("thumbnail_version_changed", 404);
        }

        return image;
    }

    /// <summary>
    /// Streams through certified bounded transport. The callback opens only the response stream,
    /// after preflight succeeds; it must not create a persistent file.
    /// </summary>
    public async Task DownloadAsync(
        Guid actor, Guid locationId, long revision, Func<string, Stream> openResponse, CancellationToken ct)
    {
        Source source = await LoadAsync(actor, locationId, revision, download: true, ct);
        object? backend = GetBackend(source.Printer);
        if (backend is not ISupportsPrinterFileCopy copy || backend is not ISupportsFileInventory inventory)
        {
            throw new PrinterFileCommandException("download_unsupported", 409);
        }

        if (source.Location.SizeBytes is <= 0 or > PrinterFileCopyService.MaximumBytes)
        {
            throw new PrinterFileCommandException("size_limit", 413);
        }

        RequireIdle(source.Printer.Id);
        await using IPrinterFileLease lease = await AcquireAsync(source, ct);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Token);
        deadline.CancelAfter(timeouts.Value.FileDownloadTimeout);
        CancellationToken token = deadline.Token;
        await ValidateAsync(actor, source, lease, download: true, token);
        Printer printer = await printers.FindByIdWithIncludesAsync(source.Printer.Id, token)
            ?? throw new PrinterFileCommandException("source_missing", 404);
        await ValidateRemoteAsync(inventory, printer, source.Location, token);
        await ValidateAsync(actor, source, lease, download: true, token);
        Stream response = openResponse(AttachmentName(source.Location.RelativePath));
        DateTime lastAuthorityCheck = DateTime.UtcNow;
        using GuardedResponseStream guarded = new(response, async () =>
        {
            token.ThrowIfCancellationRequested();
            RequireIdle(printer.Id);
            if (DateTime.UtcNow - lastAuthorityCheck >= TimeSpan.FromSeconds(1))
            {
                await ValidateAsync(actor, source, lease, download: true, token);
                lastAuthorityCheck = DateTime.UtcNow;
            }
        });
        PrinterFileCopyBytes received = await copy.CopyFileToAsync(
            printer.BackendUrl, source.Location.StorageRoot, source.Location.RelativePath,
            printer.Credential, guarded, source.Location.SizeBytes ?? PrinterFileCopyService.MaximumBytes, token);
        if (source.Location.SizeBytes is long expected && received.SizeBytes != expected)
        {
            throw new PrinterFileCommandException("source_size_changed");
        }

        await ValidateAsync(actor, source, lease, download: true, token);
        await ValidateRemoteAsync(inventory, printer, source.Location, token);
        await ValidateAsync(actor, source, lease, download: true, token);
    }

    /// <summary>Uses a bounded leaf name, independent of server platform filename conventions.</summary>
    public static string AttachmentName(string path)
    {
        string leaf = path[(Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1)..];
        string safe = new(leaf.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>' or ':' or '|' or '?' or '*'))
            .Take(180).ToArray());
        return string.IsNullOrWhiteSpace(safe) || safe is "." or ".." ? "printer-file.gcode" : safe;
    }

    /// <summary>Versioned local-only thumbnail route; a new stored image always yields a new URL.</summary>
    public static string ThumbnailUrl(Guid locationId, long observationRevision, Guid version) =>
        $"/api/printer-files/locations/{locationId}/thumbnail?observationRevision={observationRevision}&version={version}";

    private async Task<Source> LoadAsync(Guid actor, Guid id, long revision, bool download, CancellationToken ct)
    {
        if (revision < 1)
        {
            throw new PrinterFileCommandException("observation_revision_required", 400);
        }

        PrinterFileLocation? location = await db.PrinterFileLocations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (location?.RegisteredPrinterId != location?.SourcePrinterId || location is null ||
            !await access.CanReadAsync(actor, location.SourcePrinterId, ct) ||
            (download && !await access.CanActAsync(actor, location.SourcePrinterId, PrinterFileAction.Copy, ct: ct)))
        {
            throw new PrinterFileCommandException("source_missing", 404);
        }

        Printer? printer = await db.Printers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == location.SourcePrinterId && x.IsEnabled, ct);
        PrinterFileScanState? scan = await db.PrinterFileScanStates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SourcePrinterId == location.SourcePrinterId, ct);
        if (printer is null || scan is null ||
            location.ObservationRevision != revision || location.Availability != PrinterFileAvailability.Available ||
            location.ConfigurationGeneration != scan.ConfigurationGeneration ||
            location.MutationGeneration != scan.MutationGeneration ||
            scan.ConfigurationFingerprint != PrinterFileIndexPolicy.ConfigurationFingerprint(printer))
        {
            throw new PrinterFileCommandException("source_observation_changed");
        }

        if (!PrinterInventoryPath.TryCanonicalizeRoot(location.StorageRoot, out _) ||
            !PrinterInventoryPath.TryCanonicalizeRelativePath(location.RelativePath, out _) ||
            location.RelativePath.Contains('%'))
        {
            throw new PrinterFileCommandException("invalid_source_path", 400);
        }

        return new(location, printer, SourceKey(location, scan.ConfigurationFingerprint));
    }

    private static string SourceKey(PrinterFileLocation location, string configurationFingerprint) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 2,
            location.SourcePrinterId,
            location.StorageRoot,
            location.RelativePath,
            location.SizeBytes,
            location.ModifiedValue,
            location.ProviderVersion,
            location.ProviderDigest,
            location.ObservationRevision,
            location.ConfigurationGeneration,
            location.MutationGeneration,
            configurationFingerprint,
        })));

    private object? GetBackend(Printer printer) =>
        backends.IsBackendSupported((PrinterBackend)printer.Backend) ? backends.GetClient(printer.Backend) : null;

    /// <summary>Enrichment outcomes, only when recorded for the location's current source identity.</summary>
    private async Task<PrinterFileEnrichment?> CurrentEnrichmentAsync(
        PrinterFileLocation location, CancellationToken ct)
    {
        PrinterFileEnrichment? row = await db.PrinterFileEnrichments.AsNoTracking()
            .Include(x => x.Artifact)
            .SingleOrDefaultAsync(x => x.LocationId == location.Id, ct);
        return row?.SourceIdentity == PrinterFileEnrichmentIdentity.For(location) ? row : null;
    }

    /// <summary>Detaches a lost artifact so the next background slice re-acquires it.</summary>
    private async Task ScheduleRepairAsync(Guid locationId, Guid artifactId, CancellationToken ct)
    {
        PrinterFileEnrichment? row = await db.PrinterFileEnrichments
            .SingleOrDefaultAsync(x => x.LocationId == locationId && x.ArtifactId == artifactId, ct);
        if (row is null)
        {
            return;
        }

        row.ArtifactId = null;
        row.ThumbnailState = PrinterFileEnrichmentState.Pending;
        row.ThumbnailReason = "thumbnail_missing";
        row.NextAttemptAtUtc = DateTime.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent publication already replaced the pointer; its outcome wins.
        }
        finally
        {
            db.Entry(row).State = EntityState.Detached;
        }
    }

    private async Task ValidateLocalReadAsync(Guid actor, Source source, CancellationToken ct)
    {
        Source current = await LoadAsync(actor, source.Location.Id, source.Location.ObservationRevision, false, ct);
        if (current.Key != source.Key)
        {
            throw new PrinterFileCommandException("source_observation_changed");
        }
    }

    private async Task<IPrinterFileLease> AcquireAsync(Source source, CancellationToken ct) =>
        await leases.TryAcquireAsync(source.Printer.Id, interactive: true, ct)
        ?? throw new PrinterFileCommandException("source_busy");

    private async Task ValidateAsync(
        Guid actor, Source source, IPrinterFileLease lease, bool download, CancellationToken ct)
    {
        Source current = await LoadAsync(actor, source.Location.Id, source.Location.ObservationRevision, download, ct);
        RequireIdle(source.Printer.Id);
        if (source.Key != current.Key || lease.Token.IsCancellationRequested ||
            lease.ConfigurationGeneration != source.Location.ConfigurationGeneration ||
            lease.MutationGeneration != source.Location.MutationGeneration || !await lease.IsCurrentAsync(ct))
        {
            throw new PrinterFileCommandException("source_authority_changed");
        }
    }

    private void RequireIdle(Guid printerId)
    {
        if (!PrinterFileIndexPolicy.IsConfirmedIdle(status.GetSnapshot(printerId), DateTime.UtcNow))
        {
            throw new PrinterFileCommandException("waiting_for_confirmed_idle");
        }
    }

    private static PrinterFileInventoryEntry Observation(PrinterFileLocation location) =>
        new(location.StorageRoot, location.RelativePath, location.SizeBytes,
            ProviderVersion: location.ProviderVersion, ProviderHash: location.ProviderDigest,
            ModifiedValue: location.ModifiedValue);

    private static async Task ValidateRemoteAsync(
        ISupportsFileInventory inventory, Printer printer, PrinterFileLocation location, CancellationToken ct)
    {
        PrinterFileInventoryResult result = await inventory.GetFileInventoryAsync(
            printer.BackendUrl, printer.Credential, ct: ct);
        if (result.Outcome != PrinterFileInventoryOutcome.Complete ||
            !result.Coverage.Any(x => x.StorageRoot == location.StorageRoot &&
                x.Subtree.Length == 0 && x.IsComplete && x.Evidence != PrinterFileInventoryEvidence.None))
        {
            throw new PrinterFileCommandException("source_restat_unavailable");
        }

        PrinterFileInventoryEntry? observed = result.Entries.SingleOrDefault(
            x => x.StorageRoot == location.StorageRoot && x.RelativePath == location.RelativePath);
        if (observed is null)
        {
            throw new PrinterFileCommandException("source_missing");
        }

        PrinterFilePreviewTransport.ValidateObservation(Observation(location), observed);
    }

    private static PrinterFilePreviewDto Describe(
        PrinterFileLocation location, PrinterFileEnrichment? row, bool supported, DateTime now)
    {
        Guid? version = row?.ArtifactId;
        bool hasImage = version.HasValue;
        string? url = version is Guid artifactId
            ? ThumbnailUrl(location.Id, location.ObservationRevision, artifactId)
            : null;
        PrinterPrintMetadata? metadata = location.Metadata;
        double? printTime = metadata?.EstimatedPrintTimeSeconds ?? metadata?.PrintTimeSeconds;
        double? layerHeight = metadata?.LayerHeightMm;
        long? size = location.SizeBytes;
        PrinterFileEnrichmentState fallback = supported
            ? PrinterFileEnrichmentState.Pending : PrinterFileEnrichmentState.Unsupported;
        PrinterFileEnrichmentState metadataState = row?.MetadataState ?? fallback;
        PrinterFileEnrichmentState thumbnailState = row?.ThumbnailState ?? fallback;
        string? unsupportedReason = supported ? null : "enrichment_unsupported";
        bool unsupported = !supported || (metadataState == PrinterFileEnrichmentState.Unsupported &&
            thumbnailState == PrinterFileEnrichmentState.Unsupported);
        bool waiting = row is null || metadataState is PrinterFileEnrichmentState.Pending or PrinterFileEnrichmentState.Deferred ||
            thumbnailState is PrinterFileEnrichmentState.Pending or PrinterFileEnrichmentState.Deferred;
        PrinterFilePreviewState state = unsupported ? PrinterFilePreviewState.Unsupported :
            metadata?.HasValues is true || hasImage || printTime.HasValue || layerHeight.HasValue || size.HasValue
                ? PrinterFilePreviewState.Available :
            waiting ? PrinterFilePreviewState.Deferred : PrinterFilePreviewState.Unavailable;
        string? reason = hasImage ? null :
            !supported ? "preview_unsupported" : row?.ThumbnailReason ?? "image_unavailable";
        return new(location.Id, location.ObservationRevision, row?.AcquiredAtUtc ?? location.DetailsFetchedAtUtc,
            hasImage, url, printTime, layerHeight, reason, state, size, metadata)
        {
            MetadataState = metadataState,
            ThumbnailState = thumbnailState,
            MetadataReason = row?.MetadataReason ?? unsupportedReason,
            ThumbnailReason = row?.ThumbnailReason ?? unsupportedReason,
            ExtractorVersion = row?.ExtractorVersion,
            NextAttemptAtUtc = row?.NextAttemptAtUtc,
            ThumbnailVersion = version,
            IsStale = hasImage && (PrinterFileEnrichmentSchedule.IsDue(row, now) ||
                thumbnailState != PrinterFileEnrichmentState.Ready),
        };
    }

    private sealed record Source(PrinterFileLocation Location, Printer Printer, string Key);

    /// <summary>Checks cached activity on every chunk; does not own or dispose the HTTP response.</summary>
    private sealed class GuardedResponseStream(Stream destination, Func<Task> validate) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await validate();
            await destination.WriteAsync(buffer, cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// Safe same-origin preview contract; null measurements explicitly mean unavailable. Enrichment
/// status fields are always serialized so clients can distinguish pending, deferred and settled work.
/// </summary>
public sealed record PrinterFilePreviewDto(
    Guid LocationId, long ObservationRevision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? CapturedAtUtc,
    bool HasImage, string? ThumbnailUrl,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? PrintTimeSeconds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? LayerHeightMm,
    string? ReasonCode, PrinterFilePreviewState State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? SizeBytes = null,
    PrinterPrintMetadata? Metadata = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public PrinterFileEnrichmentState? MetadataState { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public PrinterFileEnrichmentState? ThumbnailState { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? MetadataReason { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ThumbnailReason { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? ExtractorVersion { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTime? NextAttemptAtUtc { get; init; }

    /// <summary>Opaque artifact version; changes whenever the served image bytes change.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Guid? ThumbnailVersion { get; init; }

    /// <summary>True when the served image is a previous artifact awaiting background revalidation.</summary>
    public bool IsStale { get; init; }
}

/// <summary>Preview availability serialized as the established case-sensitive string values.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrinterFilePreviewState>))]
public enum PrinterFilePreviewState
{
    Available,
    Unavailable,
    Unsupported,
    Deferred,
}
