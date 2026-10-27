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
/// Interactive remote reads, distinct from metadata indexing and durable Copy Local.
/// No method creates a library asset, operation, origin or queue job.
/// </summary>
public sealed class PrinterFileContentService(
    AppDbContext db,
    IPrinterFileAccessService access,
    IPrinterFileLeaseCoordinator leases,
    IPrinterStatusCacheReader status,
    IBackendClientFactory backends,
    IPrintersRepository printers,
    PrinterFilePreviewCache cache,
    IOptions<BackendTimeoutSettings> timeouts)
{
    /// <summary>Returns a cached capture or performs one explicitly requested lightweight fetch.</summary>
    public async Task<PrinterFilePreviewDto> PreviewAsync(
        Guid actor, Guid locationId, long revision, CancellationToken ct)
    {
        Source source = await LoadAsync(actor, locationId, revision, download: false, ct);
        if (GetBackend(source.Printer) is not ISupportsPrinterFilePreview preview)
        {
            return Describe(source.Location, null, "preview_unsupported");
        }

        PrinterFilePreviewCapture? capture = await cache.GetAsync(source.Key, ct);
        if (capture is not null)
        {
            await ValidateCacheReadAsync(actor, source, ct);
            return Describe(source.Location, capture);
        }

        try
        {
            return await CaptureAsync(actor, source, preview, ct);
        }
        catch (PrinterFileCommandException error) when (
            error.Reason is "waiting_for_confirmed_idle" or "source_busy")
        {
            return Describe(source.Location, null, "printer_busy", PrinterFilePreviewState.Deferred);
        }
    }

    private async Task<PrinterFilePreviewDto> CaptureAsync(
        Guid actor, Source source, ISupportsPrinterFilePreview preview, CancellationToken ct)
    {
        RequireIdle(source.Printer.Id);
        await using IPrinterFileLease lease = await AcquireAsync(source, ct);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        CancellationToken token = deadline.Token;
        await ValidateAsync(actor, source, lease, download: false, token);
        Printer printer = await printers.FindByIdWithIncludesAsync(source.Printer.Id, token)
            ?? throw new PrinterFileCommandException("source_missing", 404);
        PrinterFilePreviewContent content = await preview.GetFilePreviewAsync(
            printer.BackendUrl, Observation(source.Location), printer.Credential, token);
        await ValidateAsync(actor, source, lease, download: false, token);
        PrinterFilePreviewCapture capture = new(DateTime.UtcNow, content);
        await cache.PutAsync(source.Key, capture, token);
        await ValidateAsync(actor, source, lease, download: false, token);
        return Describe(source.Location, capture);
    }

    /// <summary>Cache-only image lookup; every read rechecks live database authority and identity.</summary>
    public async Task<PrinterFilePreviewContent> ThumbnailAsync(
        Guid actor, Guid locationId, long revision, CancellationToken ct)
    {
        Source source = await LoadAsync(actor, locationId, revision, download: false, ct);
        PrinterFilePreviewCapture? capture = await cache.GetAsync(source.Key, ct);
        await ValidateCacheReadAsync(actor, source, ct);
        return capture?.Content.Image is not null
            ? capture.Content : throw new PrinterFileCommandException("preview_not_cached", 404);
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

        return new(location, printer, PrinterFilePreviewCache.Key(location, scan.ConfigurationFingerprint));
    }

    private object? GetBackend(Printer printer) =>
        backends.IsBackendSupported((PrinterBackend)printer.Backend) ? backends.GetClient(printer.Backend) : null;

    private async Task ValidateCacheReadAsync(Guid actor, Source source, CancellationToken ct)
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
        PrinterFileLocation location, PrinterFilePreviewCapture? capture, string? reason = null,
        PrinterFilePreviewState? state = null)
    {
        string? url = capture?.Content.Image is null ? null :
            $"/api/printer-files/locations/{location.Id}/thumbnail?observationRevision={location.ObservationRevision}";
        bool hasCatalogDetails = location.DetailsFetchedAtUtc.HasValue;
        PrinterPrintMetadata? metadata = hasCatalogDetails
            ? location.Metadata : capture?.Content.Metadata;
        bool hasMetadata = metadata?.HasValues is true;
        double? printTime = metadata?.EstimatedPrintTimeSeconds ?? metadata?.PrintTimeSeconds ??
            (hasCatalogDetails ? null : capture?.Content.PrintTimeSeconds);
        double? layerHeight = metadata?.LayerHeightMm ?? (hasCatalogDetails ? null : capture?.Content.LayerHeightMm);
        long? size = location.SizeBytes ?? (hasCatalogDetails ? null : capture?.Content.SizeBytes);
        PrinterFilePreviewState resolvedState = state ?? (reason == "preview_unsupported" ? PrinterFilePreviewState.Unsupported :
            hasMetadata || capture?.Content.Image is not null || printTime.HasValue || layerHeight.HasValue ||
                size.HasValue
                ? PrinterFilePreviewState.Available : PrinterFilePreviewState.Unavailable);
        return new(location.Id, location.ObservationRevision, capture?.CapturedAtUtc, capture?.Content.Image is not null, url,
            printTime, layerHeight,
            reason ?? (capture?.Content.Image is null ? "image_unavailable" : null), resolvedState,
            size, metadata);
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

/// <summary>Safe same-origin preview contract; null measurements explicitly mean unavailable.</summary>
public sealed record PrinterFilePreviewDto(
    Guid LocationId, long ObservationRevision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? CapturedAtUtc,
    bool HasImage, string? ThumbnailUrl,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? PrintTimeSeconds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? LayerHeightMm,
    string? ReasonCode, PrinterFilePreviewState State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? SizeBytes = null,
    PrinterPrintMetadata? Metadata = null);

/// <summary>Preview availability serialized as the established case-sensitive string values.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrinterFilePreviewState>))]
public enum PrinterFilePreviewState
{
    Available,
    Unavailable,
    Unsupported,
    Deferred,
}
