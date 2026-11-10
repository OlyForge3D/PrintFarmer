using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Modules.Printers.Controllers;

public partial class PrintersController
{
    /// <summary>
    /// Acknowledges an already-offline printer until its next proven reconnect, or restores monitoring.
    /// This does not control power, maintenance, job dispatch, or scheduling.
    /// </summary>
    [Farm.Infrastructure.Authorization.RequirePermission("printers", "admin")]
    [HttpPut("{id:guid}/expected-offline")]
    [ProducesResponseType(typeof(PrinterMonitoringDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> SetExpectedOfflineAsync(Guid id, [FromBody] bool expectedOffline, CancellationToken ct)
    {
        if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.Submit, ct).ConfigureAwait(false))
        {
            return Forbid();
        }

        DateTime acknowledgedAtUtc = DateTime.UtcNow;
        Printer? printer = await _printersService.FindByIdAsync(id, ct).ConfigureAwait(false);
        if (printer is null)
        {
            return NotFound();
        }

        if (BindPrinterIfMatch(printer) is { } precondition)
        {
            return precondition;
        }

        PrinterMonitoringService monitoring = HttpContext.RequestServices.GetRequiredService<PrinterMonitoringService>();
        DateTime? expectedSince = printer.ExpectedOfflineSinceUtc;
        if (expectedOffline)
        {
            IPrinterStatusCacheReader cache = HttpContext.RequestServices.GetRequiredService<IPrinterStatusCacheReader>();
            PrinterStatusCacheSnapshot? snapshot = cache.GetSnapshot(id);
            PrinterStatusDto? status = snapshot?.Status;
            if (status is null || status.IsOnline
                || snapshot?.HasActivePrintAtDisconnect == true
                || !PrinterMonitoringPolicy.IsOfflineState(status.State)
                || PrinterStateClassifier.IsActivePrintingJob(status.RawActivityState ?? status.State)
                || await monitoring.HasActiveJobAsync(id, ct).ConfigureAwait(false))
            {
                return Conflict(new
                {
                    error = "printer_not_safe_to_acknowledge",
                    detail = "Only a known offline printer without active or assigned work can be marked expected offline. Refresh its status and resolve active work first.",
                });
            }

            if (expectedSince is DateTime previous && PrinterMonitoringPolicy.HasReconnected(previous, snapshot))
            {
                expectedSince = null;
            }
        }

        printer.ExpectedOfflineSinceUtc = expectedOffline
            ? expectedSince ?? acknowledgedAtUtc
            : null;
        try
        {
            await _printersService.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return PrinterRevisionConflict();
        }

        WritePrinterEtag(printer);
        await monitoring.NotifyChangedAsync(id, ct).ConfigureAwait(false);
        return Ok(new PrinterMonitoringDto(printer.Id, printer.ExpectedOfflineSinceUtc,
            EncodeRowVersion(printer.RowVersion)));
    }
}
