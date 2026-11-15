using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Catalog;
using Farm.Infrastructure.Services.Tasks;
using Farm.Slicer.Module.Api.Repositories;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.HostedServices;

/// <summary>
/// Background service that checks for printers without slicer profiles and creates user tasks.
/// Runs on startup and periodically to detect printers that need profile imports.
/// Only active when slicer workers are registered - skips task creation if slicing is disabled.
/// </summary>
public sealed class ProfileTaskCheckService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProfileTaskCheckService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(5);
    private readonly bool _enabled;
    private readonly bool _enablePeriodicCheck;

    public ProfileTaskCheckService(
        IServiceScopeFactory scopeFactory,
        ILogger<ProfileTaskCheckService> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Master enable/disable switch - set to false when slicing is disabled
        _enabled = configuration.GetValue("ProfileTaskCheck:Enabled", true);

        // Allow disabling periodic checks (useful for testing)
        _enablePeriodicCheck = configuration.GetValue("ProfileTaskCheck:EnablePeriodicCheck", true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("[ProfileTaskCheck] Service is disabled via configuration (ProfileTaskCheck:Enabled=false)");
            return;
        }

        _logger.LogInformation("[ProfileTaskCheck] Service starting...");

        // Initial delay to let the application fully start
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        // Run initial check
        try
        {
            await CheckPrintersForMissingProfilesAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ProfileTaskCheck] Error during initial check");
        }

        // Periodic check loop
        while (!stoppingToken.IsCancellationRequested && _enablePeriodicCheck)
        {
            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
                await CheckPrintersForMissingProfilesAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ProfileTaskCheck] Error during periodic check");

                // Continue running even after errors
            }
        }

        _logger.LogInformation("[ProfileTaskCheck] Service stopped");
    }

    /// <summary>
    /// Checks all printers and creates tasks for any printer models without imported slicer profiles.
    /// Groups printers by model to create one task per model (not per printer).
    /// Skips task creation entirely if no slicer workers are available (slicing disabled).
    /// </summary>
    internal async Task CheckPrintersForMissingProfilesAsync(CancellationToken ct)
    {
        _logger.LogInformation("[ProfileTaskCheck] Starting check for printers without profiles...");
        using IServiceScope scope = _scopeFactory.CreateScope();

        // First check if slicing is available - if the slicer module isn't registered or no workers exist, skip
        Farm.Slicer.Module.Services.ISlicersService? slicersService = scope.ServiceProvider.GetService<Farm.Slicer.Module.Services.ISlicersService>();
        if (slicersService is null)
        {
            _logger.LogInformation("[ProfileTaskCheck] Slicer module not registered - skipping profile import task creation");
            return;
        }

        IReadOnlyList<SlicerService> slicerWorkers = await slicersService.ListAsync(ct);

        if (slicerWorkers.Count == 0)
        {
            _logger.LogInformation("[ProfileTaskCheck] No slicer workers registered - skipping profile import task creation (slicing is disabled)");
            return;
        }

        IPrinterProfileCheckRepository printersRepo = scope.ServiceProvider.GetRequiredService<IPrinterProfileCheckRepository>();
        IMachineModelProfileRepository machineModelProfileRepo = scope.ServiceProvider.GetRequiredService<IMachineModelProfileRepository>();
        IMachineProfileRepository machineProfileRepo = scope.ServiceProvider.GetRequiredService<IMachineProfileRepository>();
        IUserTaskService taskService = scope.ServiceProvider.GetRequiredService<IUserTaskService>();
        ICatalogService catalogService = scope.ServiceProvider.GetRequiredService<ICatalogService>();

        try
        {
            // Get all printers
            List<Printer> printers = (await printersRepo.GetAllAsync(ct)).ToList();

            if (printers.Count == 0)
            {
                _logger.LogInformation("[ProfileTaskCheck] No printers found, nothing to check");
                return;
            }

            // Group printers by ModelId (excluding null/empty and "Unknown" model)
            var printersByModel = printers
                .Where(p => p.ModelId != Guid.Empty)
                .GroupBy(p => p.ModelId)
                .ToList();

            if (printersByModel.Count == 0)
            {
                return;
            }

            Guid[] modelIds = printersByModel.Select(group => group.Key).ToArray();
            HashSet<Guid> coveredModels = await machineModelProfileRepo.GetPrinterModelIdsWithProfilesAsync(modelIds, ct);
            coveredModels.UnionWith(await machineProfileRepo.GetPrinterModelIdsWithProfilesAsync(modelIds, ct));
            Guid[] missingModelIds = modelIds.Where(id => !coveredModels.Contains(id)).ToArray();
            if (missingModelIds.Length == 0)
            {
                _logger.LogInformation("[ProfileTaskCheck] All printer models have profiles");
                return;
            }

            Dictionary<Guid, ProfileTaskModelDto> models = (await catalogService.GetProfileTaskModelsAsync(missingModelIds, ct))
                .ToDictionary(model => model.Id);
            int tasksChecked = 0;

            foreach (var group in printersByModel)
            {
                Guid modelId = group.Key;
                var modelPrinters = group.ToList();

                if (coveredModels.Contains(modelId))
                {
                    continue;
                }

                models.TryGetValue(modelId, out ProfileTaskModelDto? modelDto);
                string modelName = modelDto?.Name ?? "Unknown Model";
                string manufacturerName = modelDto?.ManufacturerName ?? "Unknown";

                // Skip "Unknown" model - these are placeholder assignments
                if (modelName.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                    modelName.Equals("Unknown Model", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("[ProfileTaskCheck] Skipping Unknown model {ModelId}", modelId);
                    continue;
                }

                CreateProfileImportTaskBatchDto dto = new(
                    PrinterModelId: modelId,
                    PrinterModelName: modelName,
                    ManufacturerName: manufacturerName,
                    PrinterIds: modelPrinters.Select(printer => printer.Id).Distinct().ToArray());
                await taskService.CreateOrUpdateProfileImportTaskAsync(dto, ct);
                tasksChecked++;
            }

            _logger.LogInformation("[ProfileTaskCheck] Check complete. Model task groups checked: {TasksChecked}", tasksChecked);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ProfileTaskCheck] Error checking printers for missing profiles");
            throw;
        }
    }
}
