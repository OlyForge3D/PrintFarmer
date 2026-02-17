using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Tasks;
using Farm.Infrastructure.Telemetry;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Web.Api.Services.Slicing;

namespace Farm.Web.Api.Services;

/// <summary>
/// Background service that checks for printers without slicer profiles and creates user tasks.
/// Runs on startup and periodically to detect printers that need profile imports.
/// Only active when slicer workers are registered - skips task creation if slicing is disabled.
/// </summary>
public sealed class ProfileTaskCheckService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUnifiedLoggingService _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(5);
    private readonly bool _enabled;
    private readonly bool _enablePeriodicCheck;

    public ProfileTaskCheckService(
        IServiceScopeFactory scopeFactory,
        IUnifiedLoggingService logger,
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
        await CheckPrintersForMissingProfilesAsync(stoppingToken);

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
    public async Task CheckPrintersForMissingProfilesAsync(CancellationToken ct)
    {
        _logger.LogInformation("[ProfileTaskCheck] Starting check for printers without profiles...");

        using IServiceScope scope = _scopeFactory.CreateScope();

        // First check if slicing is available - if no slicer workers, skip task creation entirely
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        IReadOnlyList<SlicerService> slicerWorkers = await slicersService.ListAsync(ct);

        if (slicerWorkers.Count == 0)
        {
            _logger.LogInformation("[ProfileTaskCheck] No slicer workers registered - skipping profile import task creation (slicing is disabled)");
            return;
        }

        IPrintersService printersService = scope.ServiceProvider.GetRequiredService<IPrintersService>();
        IMachineModelProfileRepository machineModelProfileRepo = scope.ServiceProvider.GetRequiredService<IMachineModelProfileRepository>();
        IMachineProfileRepository machineProfileRepo = scope.ServiceProvider.GetRequiredService<IMachineProfileRepository>();
        IUserTaskService taskService = scope.ServiceProvider.GetRequiredService<IUserTaskService>();
        Catalog.ICatalogService catalogService = scope.ServiceProvider.GetRequiredService<Catalog.ICatalogService>();

        try
        {
            // Get all printers
            List<Printer> printers = (await printersService.GetAllAsync(ct)).ToList();

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

            int tasksCreated = 0;
            int tasksUpdated = 0;

            foreach (var group in printersByModel)
            {
                Guid modelId = group.Key;
                var modelPrinters = group.ToList();

                // Check if this model has imported slicer profiles (either MachineModelProfile OR MachineProfile)
                // MachineModelProfile = base printer model profile (e.g., "Sovol SV08")
                // MachineProfile = nozzle variant profiles (e.g., "Sovol SV08 0.4 nozzle")
                // The wizard imports MachineProfile entries, so we need to check both
                MachineModelProfile? modelProfile = await machineModelProfileRepo.GetByPrinterModelIdAsync(modelId, ct);
                bool hasMachineProfiles = await machineProfileRepo.HasAnyForPrinterModelAsync(modelId, ct);

                if (modelProfile != null || hasMachineProfiles)
                {
                    // Model already has profiles imported, skip
                    _logger.LogDebug($"[ProfileTaskCheck] Model {modelId} already has profiles (modelProfile: {modelProfile != null}, machineProfiles: {hasMachineProfiles}), skipping");
                    continue;
                }

                // Check if task already exists for this model
                bool hasExistingTask = await taskService.HasPendingProfileImportTaskAsync(modelId, ct);

                // Get model details for task title
                PrinterModelDto? modelDto = await catalogService.GetModelByIdAsync(modelId, ct);
                ManufacturerDto? manufacturerDto = modelDto != null
                    ? await catalogService.GetManufacturerByIdAsync(modelDto.ManufacturerId, ct)
                    : null;

                string modelName = modelDto?.Name ?? "Unknown Model";
                string manufacturerName = manufacturerDto?.Name ?? "Unknown";

                // Skip "Unknown" model - these are placeholder assignments
                if (modelName.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                    modelName.Equals("Unknown Model", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug($"[ProfileTaskCheck] Skipping Unknown model {modelId}");
                    continue;
                }

                // Create or update task for each printer in this model group
                foreach (Printer printer in modelPrinters)
                {
                    CreateProfileImportTaskDto dto = new(
                        PrinterModelId: modelId,
                        PrinterModelName: modelName,
                        ManufacturerName: manufacturerName,
                        PrinterId: printer.Id);

                    await taskService.CreateOrUpdateProfileImportTaskAsync(dto, ct);
                }

                if (hasExistingTask)
                {
                    tasksUpdated++;
                    _logger.LogDebug($"[ProfileTaskCheck] Updated task for {manufacturerName} {modelName} with {modelPrinters.Count} printers");
                }
                else
                {
                    tasksCreated++;
                    _logger.LogInformation($"[ProfileTaskCheck] Created task: Import profiles for {manufacturerName} {modelName} ({modelPrinters.Count} printers waiting)");
                }
            }

            _logger.LogInformation($"[ProfileTaskCheck] Check complete. Tasks created: {tasksCreated}, Tasks updated: {tasksUpdated}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ProfileTaskCheck] Error checking printers for missing profiles");
            throw;
        }
    }
}
