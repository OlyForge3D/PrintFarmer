using System.Diagnostics;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services;

/// <summary>
/// Background service that periodically updates dynamic printer capabilities
/// </summary>
public class PrinterCapabilityUpdateService(
    IServiceProvider serviceProvider,
    ILogger<PrinterCapabilityUpdateService> logger,
    IPrintFarmerTelemetryService telemetry) : BackgroundService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ILogger<PrinterCapabilityUpdateService> _logger = logger;
    private readonly TimeSpan _updateInterval = TimeSpan.FromMinutes(15); // Update every 15 minutes
    private readonly IPrintFarmerTelemetryService _telemetry = telemetry;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using Activity? activity = _telemetry.StartActivity("PrinterCapabilityUpdateService.ExecuteAsync");
        _logger.LogInformation("PrinterCapabilityUpdateService started");

        // Add startup delay to allow server initialization to complete
        try
        {
            _logger.LogInformation("PrinterCapabilityUpdateService waiting 30 seconds for server initialization");
            using Activity? startupActivity = _telemetry.StartActivity("PrinterCapabilityUpdateService.StartupDelay");
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("PrinterCapabilityUpdateService cancelled during startup delay");
            return;
        }

        _logger.LogInformation("PrinterCapabilityUpdateService beginning update cycle");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using Activity? updateActivity = _telemetry.StartActivity("PrinterCapabilityUpdateService.UpdateCycle");
                _logger.LogDebug("Starting printer capability update cycle");
                await UpdateCapabilitiesAsync(stoppingToken);
                _logger.LogDebug("Completed printer capability update cycle, waiting {Minutes} minutes", _updateInterval.TotalMinutes);
                await Task.Delay(_updateInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in PrinterCapabilityUpdateService");
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

        _logger.LogInformation("PrinterCapabilityUpdateService stopped");
    }

    private async Task UpdateCapabilitiesAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = _serviceProvider.CreateScope();
        using Activity? activity = _telemetry.StartActivity("PrinterCapabilityUpdateService.UpdateCapabilitiesAsync");

        try
        {
            _logger.LogDebug("Getting AppDbContext and IPrinterCapabilityDiscoveryService from DI");
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            IPrinterCapabilityDiscoveryService discoveryService = scope.ServiceProvider.GetRequiredService<IPrinterCapabilityDiscoveryService>();
            _logger.LogDebug("Successfully got services from DI");

            // Early return if no printers are registered to prevent hanging
            _logger.LogDebug("Checking printer count");
            int printerCount = await context.Printers.CountAsync(cancellationToken);
            _logger.LogDebug("Found {PrinterCount} registered printers", printerCount);

            if (printerCount == 0)
            {
                _logger.LogDebug("No printers registered, skipping capability updates");
                return;
            }

            // Get all printers with capabilities that haven't been updated recently
            _logger.LogDebug("Querying stale printer capabilities");
            DateTime staleThreshold = DateTime.UtcNow.AddHours(-2); // Update if older than 2 hours
            List<PrinterCapabilities> capabilities = await context.PrinterCapabilities
                .Include(c => c.Printer)
                .ThenInclude(p => p.Model)
                .Include(c => c.Printer.Manufacturer)
                .Where(c => c.LastUpdated < staleThreshold && c.IsAvailable)
                .Take(10) // Limit to 10 printers per update cycle to avoid overload
                .ToListAsync(cancellationToken);

            _logger.LogDebug("Found {CapabilitiesCount} stale printer capabilities to update", capabilities.Count);

            if (capabilities.Count == 0)
            {
                _logger.LogDebug("No capabilities need updating");
                return;
            }

            _logger.LogInformation("Updating capabilities for {Count} printers", capabilities.Count);

            foreach (PrinterCapabilities? capability in capabilities)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await discoveryService.RefreshCapabilitiesAsync(capability, capability.Printer, cancellationToken);
                    _logger.LogDebug("Updated capabilities for printer {PrinterName}", capability.Printer.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to update capabilities for printer {PrinterName}", capability.Printer.Name);
                    // Update timestamp even on failure to avoid constant retries
                    capability.LastUpdated = DateTime.UtcNow;
                }
            }

            await context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Successfully updated capabilities for {Count} printers", capabilities.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating printer capabilities");
        }
    }
}
