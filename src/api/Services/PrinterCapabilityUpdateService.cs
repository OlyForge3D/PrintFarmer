using System.Diagnostics;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.PrinterCapabilities;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Services;

/// <summary>
/// Background service that periodically updates dynamic printer capabilities
/// </summary>
public class PrinterCapabilityUpdateService(
    IServiceScopeFactory scopeFactory,
    IUnifiedLoggingService logger,
    IPrintFarmerTelemetryService telemetry) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly TimeSpan _updateInterval = TimeSpan.FromMinutes(15); // Update every 15 minutes
    private readonly IPrintFarmerTelemetryService _telemetry = telemetry;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using Activity? activity = _telemetry.StartActivity("PrinterCapabilityUpdateService.ExecuteAsync");
        _logger.LogInformation($"PrinterCapabilityUpdateService started", null, null);

        // Add startup delay to allow server initialization to complete
        try
        {
            _logger.LogInformation($"PrinterCapabilityUpdateService waiting 30 seconds for server initialization", null, null);
            using Activity? startupActivity = _telemetry.StartActivity("PrinterCapabilityUpdateService.StartupDelay");
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation($"PrinterCapabilityUpdateService cancelled during startup delay", null, null);
            return;
        }

        _logger.LogInformation($"PrinterCapabilityUpdateService beginning update cycle", null, null);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using Activity? updateActivity = _telemetry.StartActivity("PrinterCapabilityUpdateService.UpdateCycle");
                _logger.LogDebug($"Starting printer capability update cycle", null, null);
                await UpdateCapabilitiesAsync(stoppingToken);
                _logger.LogDebug($"Completed printer capability update cycle, waiting {_updateInterval.TotalMinutes} minutes", null, null);
                await Task.Delay(_updateInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in PrinterCapabilityUpdateService", null, null);
                // Wait a bit before retrying on error
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation($"PrinterCapabilityUpdateService stopped", null, null);
    }

    private async Task UpdateCapabilitiesAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        using Activity? activity = _telemetry.StartActivity("PrinterCapabilityUpdateService.UpdateCapabilitiesAsync");

        try
        {
            _logger.LogDebug($"Getting repositories and services from DI", null, null);
            IPrinterCapabilitiesRepository capabilitiesRepo = scope.ServiceProvider.GetRequiredService<IPrinterCapabilitiesRepository>();
            IPrintersRepository printersRepo = scope.ServiceProvider.GetRequiredService<IPrintersRepository>();
            IPrinterCapabilityDiscoveryService discoveryService = scope.ServiceProvider.GetRequiredService<IPrinterCapabilityDiscoveryService>();
            _logger.LogDebug($"Successfully got services from DI", null, null);

            // Early return if no printers are registered to prevent hanging
            _logger.LogDebug($"Checking printer count", null, null);
            int printerCount = await printersRepo.CountAsync(cancellationToken);
            _logger.LogDebug($"Found {printerCount} registered printers", null, null);

            if (printerCount == 0)
            {
                _logger.LogDebug($"No printers registered, skipping capability updates", null, null);
                return;
            }

            // Get all printers with capabilities that haven't been updated recently
            _logger.LogDebug($"Querying stale printer capabilities", null, null);
            DateTime staleThreshold = DateTime.UtcNow.AddHours(-2); // Update if older than 2 hours
            List<Farm.Infrastructure.Domain.PrinterCapabilities> capabilities = await capabilitiesRepo.GetStaleCapabilitiesAsync(staleThreshold, 10, cancellationToken);

            _logger.LogDebug($"Found {capabilities.Count} stale printer capabilities to update", null, null);

            if (capabilities.Count == 0)
            {
                _logger.LogDebug($"No capabilities need updating", null, null);
                return;
            }

            _logger.LogInformation($"Updating capabilities for {capabilities.Count} printers", null, null);

            foreach (Farm.Infrastructure.Domain.PrinterCapabilities? capability in capabilities)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    // Load printer reference with Model and Manufacturer
                    Printer? printer = await capabilitiesRepo.GetPrinterWithModelAndManufacturerAsync(capability.PrinterId, cancellationToken);
                    if (printer == null)
                    {
                        _logger.LogWarning($"Printer not found for capability {capability.Id}", null, null);
                        continue;
                    }

                    _ = await discoveryService.RefreshCapabilitiesAsync(capability, printer, cancellationToken);
                    _logger.LogDebug($"Updated capabilities for printer {printer.Name}", null, null);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to update capabilities for printer", null, null);
                    // Update timestamp even on failure to avoid constant retries
                    capability.LastUpdated = DateTime.UtcNow;
                }
            }

            await capabilitiesRepo.SaveChangesAsync(cancellationToken);
            _logger.LogInformation($"Successfully updated capabilities for {capabilities.Count} printers", null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error updating printer capabilities", null, null);
        }
    }
}
