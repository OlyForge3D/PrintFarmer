using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.Background;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Infrastructure.Services.Maintenance;

/// <summary>
/// Background service that periodically evaluates maintenance schedules
/// and generates alerts when maintenance is due.
/// </summary>
public class MaintenanceAlertHostedService(
    IServiceProvider serviceProvider,
    ILogger<MaintenanceAlertHostedService> logger,
    IOptionsMonitor<MaintenanceAlertSettings> settingsMonitor,
    IBackgroundServiceMonitor serviceMonitor) : BackgroundService
{
    private const string ServiceId = "MaintenanceAlertService";
    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly ILogger<MaintenanceAlertHostedService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IOptionsMonitor<MaintenanceAlertSettings> _settingsMonitor = settingsMonitor ?? throw new ArgumentNullException(nameof(settingsMonitor));
    private readonly IBackgroundServiceMonitor _serviceMonitor = serviceMonitor ?? throw new ArgumentNullException(nameof(serviceMonitor));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        MaintenanceAlertSettings settings = _settingsMonitor.CurrentValue;

        // Register with the service monitor
        _serviceMonitor.Register(
            ServiceId,
            "Maintenance Alerts",
            "Evaluates maintenance schedules and generates alerts when maintenance is due",
            "Maintenance",
            "pf-icon-alert",
            settings.IntervalSeconds);
        _serviceMonitor.ReportStarted(ServiceId);

        if (!settings.Enabled)
        {
            _logger.LogInformation("Maintenance alert engine is disabled");
            _serviceMonitor.ReportEnabled(ServiceId, false);
            return;
        }

        _serviceMonitor.ReportEnabled(ServiceId, true);
        _logger.LogInformation(
            "Maintenance alert engine started. Interval: {Interval}s, Max printers per iteration: {MaxPrinters}",
            settings.IntervalSeconds,
            settings.MaxPrintersPerIteration);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.IntervalSeconds), stoppingToken);

                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                settings = _settingsMonitor.CurrentValue; // Reload settings each iteration
                if (!settings.Enabled)
                {
                    _logger.LogInformation("Maintenance alert engine disabled, pausing service");
                    _serviceMonitor.ReportEnabled(ServiceId, false);
                    continue;
                }

                _serviceMonitor.ReportEnabled(ServiceId, true);
                await EvaluateMaintenanceAlertsAsync(settings, stoppingToken);
                _serviceMonitor.ReportSuccess(ServiceId, settings.IntervalSeconds);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Maintenance alert engine stopping");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during maintenance alert evaluation");
                _serviceMonitor.ReportError(ServiceId, ex.Message);
            }
        }

        _serviceMonitor.ReportStopped(ServiceId);
    }

    private async Task EvaluateMaintenanceAlertsAsync(MaintenanceAlertSettings settings, CancellationToken ct)
    {
        try
        {
            // Create a scope to fetch the rotation page. Deliberately does NOT resolve
            // IMaintenanceAlertService here — that is resolved per-printer below so each
            // evaluation gets its own AppDbContext/unit of work (see the per-printer loop).
            List<PrinterRotationCandidate> printers;
            using (IServiceScope listScope = _serviceProvider.CreateScope())
            {
                IPrintersRepository printersRepo = listScope.ServiceProvider.GetRequiredService<IPrintersRepository>();

                // Keyset rotation query (issue #2061): materializes at most MaxPrintersPerIteration
                // rows, ordered by staleness (PrinterServiceState.LastMaintenanceAlertEvaluatedAt
                // ascending, never-evaluated first) with Id as a tiebreaker, so every printer is
                // evaluated within a bounded number of intervals instead of only the first N ever
                // advancing. Deliberately does NOT use GetAllAsync — other callers depend on its
                // full-table, unordered semantics.
                printers = await printersRepo.GetForMaintenanceAlertRotationAsync(settings.MaxPrintersPerIteration, ct);
            }

            if (printers.Count == 0)
            {
                _logger.LogDebug("No printers found to evaluate maintenance");
                return;
            }

            _logger.LogInformation(
                "Evaluating maintenance for {EvaluateCount} printers this iteration",
                printers.Count);

            int totalAlertsGenerated = 0;

            // Evaluate each printer
            foreach (PrinterRotationCandidate printer in printers)
            {
                try
                {
                    // A printer owns one scoped AppDbContext/unit of work, mirroring
                    // PrintStatsSyncHostedService's per-printer scoping. If evaluation fails after
                    // tracking (but not saving) a partial alert batch, disposing this scope
                    // discards those pending changes instead of risking their accidental
                    // persistence via an unrelated SaveChangesAsync later (issue #2061 review
                    // finding: the previous single outer-scope design let the cursor update below
                    // commit another printer's aborted, not-yet-saved alert additions).
                    using IServiceScope printerScope = _serviceProvider.CreateScope();
                    IMaintenanceAlertService alertService =
                        printerScope.ServiceProvider.GetRequiredService<IMaintenanceAlertService>();

                    int alertsGenerated = await alertService.EvaluatePrinterMaintenanceAsync(
                        printer.Id,
                        ct);

                    totalAlertsGenerated += alertsGenerated;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to evaluate maintenance for printer '{Name}' (ID: {Id})",
                        printer.Name,
                        printer.Id);
                }
                finally
                {
                    // Advance the rotation cursor even on failure: a printer whose evaluation
                    // throws should not permanently monopolize the front of the queue and starve
                    // every other printer behind it (issue #2061). This uses its own isolated
                    // scope/DbContext, separate from the printer-evaluation scope above, so it can
                    // never be affected by (or accidentally persist) whatever that scope left
                    // tracked-but-unsaved after a failure.
                    try
                    {
                        using IServiceScope cursorScope = _serviceProvider.CreateScope();
                        IPrintersRepository cursorPrintersRepo =
                            cursorScope.ServiceProvider.GetRequiredService<IPrintersRepository>();
                        await cursorPrintersRepo.MarkMaintenanceAlertEvaluatedAsync(printer.Id, DateTime.UtcNow, ct);
                    }
                    catch (Exception markEx)
                    {
                        _logger.LogWarning(
                            markEx,
                            "Failed to advance maintenance alert rotation cursor for printer '{Name}' (ID: {Id})",
                            printer.Name,
                            printer.Id);
                    }
                }
            }

            if (totalAlertsGenerated > 0)
            {
                _logger.LogInformation(
                    "Maintenance alert evaluation completed: {Count} new alerts generated",
                    totalAlertsGenerated);
            }
            else
            {
                _logger.LogDebug("Maintenance alert evaluation completed: no new alerts");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during maintenance alert evaluation scan");
        }
    }
}
