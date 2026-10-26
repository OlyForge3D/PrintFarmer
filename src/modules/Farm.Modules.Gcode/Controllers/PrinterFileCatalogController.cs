using System.Security.Claims;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
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
    IBackendClientFactory? backendClients = null) : ControllerBase
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

        var items = await query.OrderBy(x => x.DisplayName).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.Id,
                printerId = x.SourcePrinterId,
                printerName = x.RegisteredPrinter!.Name,
                x.StorageRoot,
                x.RelativePath,
                x.DisplayName,
                x.SizeBytes,
                x.ModifiedValue,
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
            }).ToArrayAsync(ct);
        var printerRows = await db.Printers.AsNoTracking().Where(x => allowed.Contains(x.Id))
            .OrderBy(x => x.Name).Select(x => new { x.Id, x.Name, x.Backend }).ToArrayAsync(ct);
        List<object> printers = [];
        foreach (var printer in printerRows)
        {
            object? backend = backendClients is not null &&
                backendClients.IsBackendSupported((PrinterBackend)printer.Backend)
                    ? backendClients.GetClient(printer.Backend) : null;
            bool supportsCopy = backend is ISupportsPrinterFileCopy;
            bool supportsPreview = backend is ISupportsPrinterFilePreview;
            bool supportsDownload = backend is ISupportsPrinterFileCopy and ISupportsFileInventory;
            printers.Add(new
            {
                printer.Id,
                printer.Name,
                canManage = authority.Manageable.Contains(printer.Id),
                supportsCopy,
                canCopy = supportsCopy && authority.Copyable.Contains(printer.Id),
                supportsPreview,
                canPreview = supportsPreview && authority.Readable.Contains(printer.Id),
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
        return Ok(new { items, total, page, pageSize, printers, scans, automaticDiscoveryEnabled = policy.Enabled });
    }

    [HttpPost("printers/{printerId:guid}/refresh")]
    [Authorize(Policy = InteractiveSessionRequirement.PolicyName)]
    public async Task<IActionResult> RefreshAsync(Guid printerId, CancellationToken ct)
    {
        if (!await CanManageAsync(printerId, ct))
        {
            return NotFound();
        }

        PrinterFileScanState? state = await db.PrinterFileScanStates.SingleOrDefaultAsync(
            x => x.SourcePrinterId == printerId, ct);
        if (state is null)
        {
            return Accepted(new { code = "enrollment_pending" });
        }

        state.NextScanAtUtc = DateTime.UtcNow;
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
