using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Services.Maintenance;

/// <summary>
/// Background service that periodically evaluates maintenance schedules
/// and generates alerts when maintenance is due.
/// </summary>
public class MaintenanceAlertHostedService(
    IServiceProvider serviceProvider,
    ILogger<MaintenanceAlertHostedService> logger,
    IOptionsMonitor<MaintenanceAlertSettings> settingsMonitor) : BackgroundService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly ILogger<MaintenanceAlertHostedService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IOptionsMonitor<MaintenanceAlertSettings> _settingsMonitor = settingsMonitor ?? throw new ArgumentNullException(nameof(settingsMonitor));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        MaintenanceAlertSettings settings = _settingsMonitor.CurrentValue;

        if (!settings.Enabled)
        {
            _logger.LogInformation("Maintenance alert engine is disabled");
            return;
        }

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
                    continue;
                }

                await EvaluateMaintenanceAlertsAsync(settings, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Maintenance alert engine stopping");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during maintenance alert evaluation");
            }
        }
    }

    private async Task EvaluateMaintenanceAlertsAsync(MaintenanceAlertSettings settings, CancellationToken ct)
    {
        try
        {
            // Create a scope to get the scoped services
            using IServiceScope scope = _serviceProvider.CreateScope();
            IPrintersRepository printersRepo = scope.ServiceProvider.GetRequiredService<IPrintersRepository>();
            IMaintenanceAlertService alertService = scope.ServiceProvider.GetRequiredService<IMaintenanceAlertService>();

            // Get all printers
            List<Printer> printers = await printersRepo.GetAllAsync(ct);

            if (printers.Count == 0)
            {
                _logger.LogDebug("No printers found to evaluate maintenance");
                return;
            }

            // Limit printers per iteration to avoid overload
            int printersToEvaluate = Math.Min(printers.Count, settings.MaxPrintersPerIteration);

            _logger.LogInformation(
                "Evaluating maintenance for {EvaluateCount} of {TotalCount} printers",
                printersToEvaluate,
                printers.Count);

            int totalAlertsGenerated = 0;

            // Evaluate each printer
            for (int i = 0; i < printersToEvaluate; i++)
            {
                Printer printer = printers[i];

                try
                {
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
