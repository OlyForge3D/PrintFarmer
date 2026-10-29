using System.Security.Claims;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Modules.Gcode.Services.Gcode;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Modules.Gcode.Controllers;

/// <summary>Authorized metadata views; browsing never reads remote file bodies.</summary>
[ApiController]
[Route("api/printer-files")]
[Authorize(Policy = "ModelRead")]
public sealed class PrinterFileCatalogController(
    AppDbContext db,
    IPrinterFileAccessService access,
    IQueueResourceAuthorizationService resources,
    IBackendClientFactory? backendClients = null,
    PrinterFileCatalogService? catalog = null) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> ListAsync(
        [FromQuery] Guid? printerId,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] PrinterFileAvailability? availability = null,
        [FromQuery] bool? stale = null,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid actor))
        {
            return Forbid();
        }

        if (page is < 1 or > 100_000 || pageSize is < 1 or > 200 || search?.Length > 200)
        {
            return BadRequest(new { code = "invalid_catalog_query" });
        }

        Guid[] candidates = await db.Printers.AsNoTracking().Where(x => x.IsEnabled)
            .Select(x => x.Id).ToArrayAsync(ct);
        PrinterFileCatalogAuthority authority = await access.GetCatalogAuthorityAsync(actor, candidates, ct);
        Guid[] allowed = [.. authority.Readable];

        IQueryable<PrinterFileLocation> query = db.PrinterFileLocations.AsNoTracking()
            .Where(x => x.RegisteredPrinterId.HasValue && allowed.Contains(x.RegisteredPrinterId.Value));
        if (printerId.HasValue)
        {
            query = query.Where(x => x.SourcePrinterId == printerId.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = PrinterFileCatalogSearch.WherePathContains(query, search);
        }

        var policy = await PrinterFileIndexPolicy.ReadAsync(db, ct);
        DateTime staleBefore = DateTime.UtcNow.AddMinutes(-2.0 * policy.ScanIntervalMinutes);
        if (availability.HasValue)
        {
            query = query.Where(x => x.Availability == availability.Value);
        }

        if (stale.HasValue)
        {
            query = stale.Value ? query.Where(x => x.LastSeenAtUtc < staleBefore)
                : query.Where(x => x.LastSeenAtUtc >= staleBefore);
        }

        int total = await query.CountAsync(ct);
        Guid[] pageIds = await query.OrderBy(x => x.DisplayName).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).Select(x => x.Id).ToArrayAsync(ct);
        Guid[] localCandidates = await db.PrinterFileOrigins.AsNoTracking()
            .Where(x => x.LocationId.HasValue && pageIds.Contains(x.LocationId.Value) && x.GcodeFileId.HasValue)
            .Select(x => x.GcodeFileId!.Value).Distinct().ToArrayAsync(ct);
        Guid[] allowedLocal = [.. await access.FilterAccessibleLocalAsync(actor, localCandidates, ct: ct)];

        var rows = await (
                from x in query
                join row in db.PrinterFileEnrichments on x.Id equals row.LocationId into joined
                from enrichment in joined.DefaultIfEmpty()
                orderby x.DisplayName, x.Id
                select new
                {
                    x.Id,
                    printerId = x.SourcePrinterId,
                    printerName = x.RegisteredPrinter!.Name,
                    x.StorageRoot,
                    x.RelativePath,
                    x.DisplayName,
                    x.SizeBytes,
                    x.Metadata,
                    x.DetailsFetchedAtUtc,
                    x.ModifiedValue,
                    x.ProviderVersion,
                    x.ProviderDigest,
                    x.ObservationRevision,
                    x.ConfigurationGeneration,
                    x.Availability,
                    x.LastSeenAtUtc,
                    x.LastCompleteScanAtUtc,
                    isStale = x.LastSeenAtUtc < staleBefore,
                    localCopies = db.PrinterFileOrigins.Where(origin =>
                            origin.LocationId == x.Id && origin.GcodeFileId.HasValue &&
                            allowedLocal.Contains(origin.GcodeFileId.Value))
                        .Select(origin => new
                        {
                            origin.GcodeFileId,
                            origin.CapturedAtUtc,
                            origin.Verification,
                        }).ToArray(),
                    enrichment,
                })
            .Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
        DateTime now = DateTime.UtcNow;
        var items = rows.Select(x =>
        {
            // Outcomes recorded for an older source or configuration generation are never presented.
            PrinterFileEnrichment? enrichment = x.enrichment is { } candidate &&
                candidate.SourceIdentity == PrinterFileEnrichmentIdentity.For(new PrinterFileLocation
                {
                    SourcePrinterId = x.printerId,
                    StorageRoot = x.StorageRoot,
                    RelativePath = x.RelativePath,
                    ConfigurationGeneration = x.ConfigurationGeneration,
                    SizeBytes = x.SizeBytes,
                    ModifiedValue = x.ModifiedValue,
                    ProviderVersion = x.ProviderVersion,
                    ProviderDigest = x.ProviderDigest,
                })
                    ? candidate : null;
            Guid? thumbnailVersion = x.Availability == PrinterFileAvailability.Available
                ? enrichment?.ArtifactId : null;
            return new
            {
                x.Id,
                x.printerId,
                x.printerName,
                x.StorageRoot,
                x.RelativePath,
                x.DisplayName,
                x.SizeBytes,
                x.Metadata,
                x.DetailsFetchedAtUtc,
                x.ModifiedValue,
                x.ObservationRevision,
                x.ConfigurationGeneration,
                x.Availability,
                x.LastSeenAtUtc,
                x.LastCompleteScanAtUtc,
                x.isStale,
                x.localCopies,
                metadataState = enrichment?.MetadataState,
                thumbnailState = enrichment?.ThumbnailState,
                metadataReason = enrichment?.MetadataReason,
                thumbnailReason = enrichment?.ThumbnailReason,
                thumbnailVersion,
                thumbnailUrl = thumbnailVersion is Guid version
                    ? PrinterFileContentService.ThumbnailUrl(x.Id, x.ObservationRevision, version)
                    : null,
                thumbnailIsStale = thumbnailVersion.HasValue &&
                    (PrinterFileEnrichmentSchedule.IsDue(enrichment, now) ||
                        enrichment!.ThumbnailState != PrinterFileEnrichmentState.Ready),
            };
        }).ToArray();
        var printerRows = await db.Printers.AsNoTracking().Where(x => allowed.Contains(x.Id))
            .OrderBy(x => x.Name).Select(x => new { x.Id, x.Name, x.Backend }).ToArrayAsync(ct);
        List<object> printers = [];
        foreach (var printer in printerRows)
        {
            object? backend = backendClients is not null &&
                backendClients.IsBackendSupported((PrinterBackend)printer.Backend)
                    ? backendClients.GetClient(printer.Backend) : null;
            bool supportsCopy = backend is ISupportsPrinterFileCopy;

            // Previews are served from locally persisted enrichment, so reading them needs only read authority.
            bool supportsPreview = backend is ISupportsPrinterFileEnrichment;
            bool supportsDownload = backend is ISupportsPrinterFileCopy and ISupportsFileInventory;
            printers.Add(new
            {
                printer.Id,
                printer.Name,
                canManage = authority.Manageable.Contains(printer.Id),
                supportsCopy,
                canCopy = supportsCopy && authority.Copyable.Contains(printer.Id),
                supportsPreview,
                canPreview = authority.Readable.Contains(printer.Id),
                supportsDownload,
                canDownload = supportsDownload && authority.Copyable.Contains(printer.Id),
                canDelete = authority.Deletable.Contains(printer.Id),
            });
        }

        var scans = await db.PrinterFileScanStates.AsNoTracking()
            .Where(x => x.RegisteredPrinterId.HasValue && allowed.Contains(x.RegisteredPrinterId.Value))
            .Select(x => new
            {
                printerId = x.SourcePrinterId,
                x.Enabled,
                x.Outcome,
                x.ReasonCode,
                x.LastAttemptAtUtc,
                x.LastCompleteScanAtUtc,
                x.NextScanAtUtc,
                x.ConfigurationGeneration,
                isScanning = x.LeaseOwner.HasValue && x.LeaseExpiresAtUtc > DateTime.UtcNow,
                isStale = !x.LastCompleteScanAtUtc.HasValue || x.LastCompleteScanAtUtc < staleBefore,
            }).ToArrayAsync(ct);
        return Ok(new
        {
            items,
            total,
            page,
            pageSize,
            printers,
            scans,
            automaticDiscoveryEnabled = policy.Enabled,
            maxConcurrentIo = policy.MaxConcurrentIo,
        });
    }

    /// <summary>
    /// Without <paramref name="force"/>, only moves the next background scan and per-file enrichment forward.
    /// With it, scans now and
    /// returns 200 only after a complete listing is published; deferrals and incomplete scans return 409.
    /// </summary>
    [HttpPost("printers/{printerId:guid}/refresh")]
    [Authorize(Policy = InteractiveSessionRequirement.PolicyName)]
    public async Task<IActionResult> RefreshAsync(
        Guid printerId, [FromQuery] bool force = false, CancellationToken ct = default)
    {
        if (!await CanManageAsync(printerId, ct))
        {
            return NotFound();
        }

        if (force)
        {
            PrinterFileCatalogService scanner = catalog ??
                throw new InvalidOperationException("Printer file catalog service is not registered.");
            PrinterFileForceRefreshResult outcome;
            try
            {
                outcome = await scanner.ForceRefreshAsync(printerId, ct);
            }
            catch (DbUpdateException)
            {
                return Conflict(new { code = "scan_state_changed" });
            }

            return outcome.Status switch
            {
                PrinterFileForceRefreshStatus.Completed => Ok(new { code = "refresh_completed", files = outcome.FileCount }),
                PrinterFileForceRefreshStatus.Incomplete => Conflict(new { code = "refresh_incomplete", reasonCode = outcome.ReasonCode }),
                _ => Conflict(new { code = outcome.ReasonCode, reasonCode = outcome.ReasonCode }),
            };
        }

        PrinterFileScanState? state = await db.PrinterFileScanStates.SingleOrDefaultAsync(
            x => x.SourcePrinterId == printerId, ct);
        if (state is null)
        {
            return Accepted(new { code = "enrollment_pending" });
        }

        DateTime requestedAt = DateTime.UtcNow;
        state.NextScanAtUtc = requestedAt;

        // Same-source rows are reacquired in the background; any stored image is served as stale meanwhile.
        List<PrinterFileEnrichment> rows = await db.PrinterFileEnrichments
            .Where(x => db.PrinterFileLocations.Any(location =>
                location.Id == x.LocationId && location.SourcePrinterId == printerId))
            .ToListAsync(ct);
        foreach (PrinterFileEnrichment row in rows)
        {
            row.NextAttemptAtUtc = requestedAt;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { code = "scan_state_changed" });
        }

        return Accepted(new { code = "refresh_requested" });
    }

    [HttpPut("printers/{printerId:guid}/index")]
    [Authorize(Policy = InteractiveSessionRequirement.PolicyName)]
    public async Task<IActionResult> SetEnabledAsync(
        Guid printerId, [FromBody] PrinterFileIndexControl request, CancellationToken ct)
    {
        if (!await CanManageAsync(printerId, ct))
        {
            return NotFound();
        }

        PrinterFileScanState? state = await db.PrinterFileScanStates.SingleOrDefaultAsync(
            x => x.SourcePrinterId == printerId, ct);
        if (state is null)
        {
            Printer printer = await db.Printers.AsNoTracking().SingleAsync(x => x.Id == printerId, ct);
            state = new PrinterFileScanState
            {
                Id = printerId,
                SourcePrinterId = printerId,
                RegisteredPrinterId = printerId,
                ConfigurationFingerprint = PrinterFileIndexPolicy.ConfigurationFingerprint(printer),
            };
            db.PrinterFileScanStates.Add(state);
        }

        state.Enabled = request.Enabled;
        state.NextScanAtUtc = DateTime.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { code = "scan_state_changed" });
        }

        return Ok(new { state.Enabled });
    }

    private async Task<bool> CanManageAsync(Guid printerId, CancellationToken ct) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid actor) &&
        await access.CanReadAsync(actor, printerId, ct) &&
        await resources.CanActorAccessPrinterAsync(actor.ToString(), printerId, PrinterGroupAccessLevel.Manage, ct);
}

public sealed record PrinterFileIndexControl(bool Enabled);
