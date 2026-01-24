using Farm.Infrastructure;
using Farm.Infrastructure.Telemetry;
using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Worker.Core;
using Microsoft.AspNetCore.Mvc;

namespace Farm.OrcaSlicer.Worker.Controllers;

/// <summary>
/// Exposes OrcaSlicer profiles organized by manufacturer.
/// Profiles are discovered from the system installation and organized by manufacturer hierarchy.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Tags("Slicer Profiles")]
public class ProfilesController(ISlicerProfilesService profileService, IUnifiedLoggingService logger) : ControllerBase
{
    private readonly ISlicerProfilesService _profileService = profileService;
    private readonly CachedOrcaProfilesService? _cachedService = profileService as CachedOrcaProfilesService;
    private readonly IUnifiedLoggingService _logger = logger;

    /// <summary>
    /// Get all available slicer profiles organized by manufacturer and model hierarchy.
    /// </summary>
    /// <remarks>
    /// Returns profiles organized as: Manufacturer -> Model -> (Machine Profiles + Filament Profiles + Process Profiles)
    /// Filament and process profiles are associated with machine profiles via the compatible_printers array.
    /// Compatible printers are resolved from both explicit compatible_printers arrays and compatible_printers_condition expressions.
    /// </remarks>
    /// <param name="ct">Cancellation token</param>
    /// <returns>All available profiles organized by manufacturer and model</returns>
    [HttpGet]
    [ProducesResponseType(typeof(AllProfilesResponseDto), 200)]
    public async Task<ActionResult<AllProfilesResponseDto>> GetAllProfilesAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Fetching all OrcaSlicer profiles organized by manufacturer and model hierarchy");

            // Load machine model profiles (base templates from machine_model_list)
            IList<MachineModelProfileDto> machineModelProfiles = await _profileService.ListAvailableMachineModelProfilesAsync(ct);

            // Load machine profiles (nozzle variants from machine_list)
            IList<MachineProfileDto> machineProfiles = await _profileService.ListAvailableMachineProfilesAsync(ct);
            IList<FilamentProfileDto> filamentProfiles = await _profileService.ListAvailableFilamentProfilesAsync(ct);
            IList<ProcessProfileDto> processProfiles = await _profileService.ListAvailableProcessProfilesAsync(ct);

            // Build the hierarchy organized by manufacturer and model
            Dictionary<string, ManufacturerProfilesDto> byHierarchy = new Dictionary<string, ManufacturerProfilesDto>();

            // Group machine profiles by manufacturer
            Dictionary<string, List<MachineProfileDto>> machinesByManufacturer = machineProfiles
                .GroupBy(p => p.Manufacturer ?? "Unknown")
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach ((string? manufacturer, List<MachineProfileDto>? machines) in machinesByManufacturer)
            {
                ManufacturerProfilesDto manufacturerProfiles = new ManufacturerProfilesDto { Name = manufacturer };
                Dictionary<string, PrinterModelProfilesDto> models = new Dictionary<string, PrinterModelProfilesDto>();

                // Group machine profiles by printer_model field (already parsed from JSON)
                // e.g., "Elegoo Centauri Carbon" groups all nozzle variants together
                Dictionary<string, List<MachineProfileDto>> machinesByModelName = machines
                    .GroupBy(m => m.PrinterModel ?? m.Name ?? "Unknown")
                    .ToDictionary(g => g.Key, g => g.ToList());

                // For each model, collect its machine, filament, and process profiles
                foreach ((string? modelName, List<MachineProfileDto>? modelMachines) in machinesByModelName)
                {
                    string modelId = GenerateModelId(manufacturer, modelName);
                    PrinterModelProfilesDto modelProfiles = new PrinterModelProfilesDto
                    {
                        Name = modelName,
                        ModelId = modelId
                    };

                    // Add all machine profiles for this model
                    modelProfiles.MachineProfiles = modelMachines.ToList();

                    // Find filament and process profiles compatible with any machine in this model
                    List<string> machineProfileNames = modelMachines.Select(m => m.Name ?? string.Empty).ToList();

                    // Filter filament profiles: include if:
                    // 1. compatible_printers contains any machine in this model, OR
                    // 2. from OrcaFilamentLibrary (universal gallery), OR
                    // 3. compatible_printers is empty/null (universally available)
                    modelProfiles.FilamentProfiles = filamentProfiles
                        .Where(f =>

                            // Explicitly compatible with a machine in this model
                            (f.CompatiblePrinters != null && f.CompatiblePrinters.Any(cp => machineProfileNames.Contains(cp))) ||

                            // From OrcaFilamentLibrary (universal)
                            (f.Manufacturer ?? string.Empty).Equals("OrcaFilamentLibrary", StringComparison.OrdinalIgnoreCase) ||

                            // No specific compatibility (universally available)
                            f.CompatiblePrinters == null || f.CompatiblePrinters.Count == 0)
                        .ToList();

                    // Filter process profiles: include if:
                    // 1. compatible_printers contains any machine in this model, OR
                    // 2. compatible_printers is empty/null (universally available)
                    modelProfiles.ProcessProfiles = processProfiles
                        .Where(p =>

                            // Explicitly compatible with a machine in this model
                            (p.CompatiblePrinters != null && p.CompatiblePrinters.Any(cp => machineProfileNames.Contains(cp))) ||

                            // No specific compatibility (universally available)
                            p.CompatiblePrinters == null || p.CompatiblePrinters.Count == 0)
                        .ToList();

                    models[modelId] = modelProfiles;
                }

                manufacturerProfiles.Models = models;
                byHierarchy[manufacturer] = manufacturerProfiles;
            }

            // Also provide legacy flat structure for backward compatibility
            AllProfilesResponseDto response = new AllProfilesResponseDto
            {
                ByHierarchy = byHierarchy,

                MachineModelProfiles = machineModelProfiles
                    .GroupBy(p => p.Manufacturer ?? "Unknown")
                    .ToDictionary(g => g.Key, g => (IList<MachineModelProfileDto>)g.ToList()),

                MachineProfiles = machineProfiles
                    .GroupBy(p => p.Manufacturer ?? "Unknown")
                    .ToDictionary(g => g.Key, g => (IList<MachineProfileDto>)g.ToList()),

                FilamentProfiles = filamentProfiles
                    .GroupBy(p => p.Manufacturer ?? "Unknown")
                    .ToDictionary(g => g.Key, g => (IList<FilamentProfileDto>)g.ToList()),

                ProcessProfiles = processProfiles
                    .GroupBy(p => "Generic")
                    .ToDictionary(g => g.Key, g => (IList<ProcessProfileDto>)g.ToList())
            };

            _logger.LogInformation($"Returning {machineModelProfiles.Count} machine model, {machineProfiles.Count} machine, {filamentProfiles.Count} filament, {processProfiles.Count} process profiles in {byHierarchy.Count} manufacturers");
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching profiles: {ex.Message}");
            return StatusCode(500, new { error = "Failed to fetch profiles", message = ex.Message });
        }
    }

    /// <summary>
    /// Get only machine profiles organized by manufacturer.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Machine profiles grouped by manufacturer</returns>
    [HttpGet("machine")]
    [ProducesResponseType(typeof(Dictionary<string, IList<MachineProfileDto>>), 200)]
    public async Task<ActionResult<Dictionary<string, IList<MachineProfileDto>>>> GetMachineProfilesAsync(CancellationToken ct)
    {
        try
        {
            IList<MachineProfileDto> profiles = await _profileService.ListAvailableMachineProfilesAsync(ct);
            Dictionary<string, IList<MachineProfileDto>> grouped = profiles
                .GroupBy(p => p.Manufacturer ?? "Unknown")
                .ToDictionary(g => g.Key, g => (IList<MachineProfileDto>)g.ToList());

            return Ok(grouped);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching machine profiles: {ex.Message}");
            return StatusCode(500, new { error = "Failed to fetch machine profiles", message = ex.Message });
        }
    }

    /// <summary>
    /// Get only filament profiles organized by manufacturer.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Filament profiles grouped by manufacturer</returns>
    [HttpGet("filament")]
    [ProducesResponseType(typeof(Dictionary<string, IList<FilamentProfileDto>>), 200)]
    public async Task<ActionResult<Dictionary<string, IList<FilamentProfileDto>>>> GetFilamentProfilesAsync(CancellationToken ct)
    {
        try
        {
            IList<FilamentProfileDto> profiles = await _profileService.ListAvailableFilamentProfilesAsync(ct);
            Dictionary<string, IList<FilamentProfileDto>> grouped = profiles
                .GroupBy(p => p.Manufacturer ?? "Unknown")
                .ToDictionary(g => g.Key, g => (IList<FilamentProfileDto>)g.ToList());

            return Ok(grouped);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching filament profiles: {ex.Message}");
            return StatusCode(500, new { error = "Failed to fetch filament profiles", message = ex.Message });
        }
    }

    /// <summary>
    /// Get only process profiles.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Process profiles</returns>
    [HttpGet("process")]
    [ProducesResponseType(typeof(IList<ProcessProfileDto>), 200)]
    public async Task<ActionResult<IList<ProcessProfileDto>>> GetProcessProfilesAsync(CancellationToken ct)
    {
        try
        {
            IList<ProcessProfileDto> profiles = await _profileService.ListAvailableProcessProfilesAsync(ct);
            return Ok(profiles);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching process profiles: {ex.Message}");
            return StatusCode(500, new { error = "Failed to fetch process profiles", message = ex.Message });
        }
    }

    /// <summary>
    /// Get profiles for a specific manufacturer.
    /// </summary>
    /// <param name="manufacturer">Manufacturer name (e.g., "Prusa", "Creality")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Profiles for the specified manufacturer</returns>
    [HttpGet("manufacturer/{manufacturer}")]
    [ProducesResponseType(typeof(object), 200)]
    public async Task<ActionResult<object>> GetManufacturerProfilesAsync(string manufacturer, CancellationToken ct)
    {
        try
        {
            List<MachineProfileDto> machineProfiles = (await _profileService.ListAvailableMachineProfilesAsync(ct))
                .Where(p => (p.Manufacturer ?? "Unknown").Equals(manufacturer, StringComparison.OrdinalIgnoreCase))
                .ToList();

            List<FilamentProfileDto> filamentProfiles = (await _profileService.ListAvailableFilamentProfilesAsync(ct))
                .Where(p => (p.Manufacturer ?? "Unknown").Equals(manufacturer, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (machineProfiles.Count == 0 && filamentProfiles.Count == 0)
            {
                return NotFound(new { message = $"No profiles found for manufacturer '{manufacturer}'" });
            }

            return Ok(new
            {
                manufacturer,
                machineProfiles,
                filamentProfiles,
                profileCount = machineProfiles.Count + filamentProfiles.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching profiles for manufacturer '{manufacturer}': {ex.Message}");
            return StatusCode(500, new { error = "Failed to fetch manufacturer profiles", message = ex.Message });
        }
    }

    /// <summary>
    /// Get distinct printer models for a manufacturer (for UI dropdowns).
    /// </summary>
    /// <param name="manufacturer">Manufacturer name (e.g., "Elegoo", "Prusa")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of printer model names</returns>
    [HttpGet("models/{manufacturer}")]
    [ProducesResponseType(typeof(List<string>), 200)]
    public async Task<ActionResult<List<string>>> GetPrinterModelsAsync(string manufacturer, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation($"Fetching printer models for manufacturer '{manufacturer}'");

            List<string> models;
            if (_cachedService != null)
            {
                // Direct indexed query on printer_model column
                models = await _cachedService.GetPrinterModelsAsync(manufacturer, ct);
            }
            else
            {
                // Fallback: filter in memory and get distinct printer_model values
                models = (await _profileService.ListAvailableMachineProfilesAsync(ct))
                    .Where(p => (p.Manufacturer ?? "Unknown").Equals(manufacturer, StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.PrinterModel)
                    .Where(m => !string.IsNullOrEmpty(m))
                    .Distinct()
                    .OrderBy(m => m)
                    .ToList()!;
            }

            _logger.LogInformation($"Returning {models.Count} models for manufacturer '{manufacturer}'");
            return Ok(models);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching models for manufacturer '{manufacturer}': {ex.Message}");
            return StatusCode(500, new { error = "Failed to fetch printer models", message = ex.Message });
        }
    }

    /// <summary>
    /// Get machine profiles for a specific manufacturer and model.
    /// This is the recommended endpoint for fetching profiles for a specific printer.
    /// </summary>
    /// <param name="manufacturer">Manufacturer name (e.g., "Elegoo", "Prusa")</param>
    /// <param name="model">Model name (e.g., "Centauri Carbon", "CORE One")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Machine profiles matching the manufacturer and model</returns>
    [HttpGet("machine/{manufacturer}/{model}")]
    [ProducesResponseType(typeof(List<MachineProfileDto>), 200)]
    public async Task<ActionResult<List<MachineProfileDto>>> GetMachineProfilesForModelAsync(
        string manufacturer,
        string model,
        CancellationToken ct)
    {
        try
        {
            // printer_model in JSON is "{Manufacturer} {Model}" format, e.g., "Elegoo Centauri Carbon"
            string printerModel = $"{manufacturer} {model}".Replace("_", " ", StringComparison.Ordinal);
            _logger.LogInformation($"Fetching machine profiles for printer_model='{printerModel}'");

            // Use indexed SQLite query if cached service is available
            List<MachineProfileDto> result;
            if (_cachedService != null)
            {
                // Direct indexed query: manufacturer + printer_model columns
                result = await _cachedService.GetMachineProfilesByModelAsync(manufacturer, printerModel, ct);
            }
            else
            {
                // Fallback: filter in memory
                result = (await _profileService.ListAvailableMachineProfilesAsync(ct))
                    .Where(p => (p.PrinterModel ?? string.Empty).Equals(printerModel, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            _logger.LogInformation($"Returning {result.Count} machine profiles for {manufacturer}/{model}");
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching machine profiles for {manufacturer}/{model}: {ex.Message}");
            return StatusCode(500, new { error = "Failed to fetch machine profiles", message = ex.Message });
        }
    }

    /// <summary>
    /// Get process profiles compatible with a specific machine.
    /// </summary>
    /// <param name="manufacturer">Manufacturer name</param>
    /// <param name="model">Model name</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Process profiles compatible with machines matching the manufacturer and model</returns>
    [HttpGet("process/{manufacturer}/{model}")]
    [ProducesResponseType(typeof(List<ProcessProfileDto>), 200)]
    public async Task<ActionResult<List<ProcessProfileDto>>> GetProcessProfilesForModelAsync(
        string manufacturer,
        string model,
        CancellationToken ct)
    {
        try
        {
            _logger.LogInformation($"Fetching process profiles for {manufacturer}/{model}");

            // First, get machine profiles to find compatible process profiles
            List<MachineProfileDto> machineProfiles;
            if (_cachedService != null)
            {
                machineProfiles = await _cachedService.GetMachineProfilesByManufacturerAsync(manufacturer, ct);
            }
            else
            {
                machineProfiles = (await _profileService.ListAvailableMachineProfilesAsync(ct))
                    .Where(p => (p.Manufacturer ?? "Unknown").Equals(manufacturer, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // Filter by printer_model field directly
            string expectedPrinterModel = $"{manufacturer} {model}".Replace("_", " ", StringComparison.Ordinal);
            HashSet<string> machineNames = machineProfiles
                .Where(p => (p.PrinterModel ?? string.Empty).Equals(expectedPrinterModel, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Name ?? string.Empty)
                .ToHashSet();

            if (machineNames.Count == 0)
            {
                return Ok(new List<ProcessProfileDto>()); // No matching machines
            }

            // Get all process profiles and filter by compatibility
            IList<ProcessProfileDto> allProcesses = await _profileService.ListAvailableProcessProfilesAsync(ct);

            List<ProcessProfileDto> result = allProcesses
                .Where(p =>

                    // Compatible with a machine in this model
                    (p.CompatiblePrinters != null && p.CompatiblePrinters.Any(cp => machineNames.Contains(cp))) ||

                    // Or universal (no specific compatibility)
                    p.CompatiblePrinters == null || p.CompatiblePrinters.Count == 0)
                .ToList();

            _logger.LogInformation($"Returning {result.Count} process profiles compatible with {manufacturer}/{model}");
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching process profiles for {manufacturer}/{model}: {ex.Message}");
            return StatusCode(500, new { error = "Failed to fetch process profiles", message = ex.Message });
        }
    }

    /// <summary>
    /// Get filament profiles compatible with a specific machine.
    /// </summary>
    /// <param name="manufacturer">Manufacturer name</param>
    /// <param name="model">Model name</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Filament profiles compatible with machines matching the manufacturer and model</returns>
    [HttpGet("filament/{manufacturer}/{model}")]
    [ProducesResponseType(typeof(List<FilamentProfileDto>), 200)]
    public async Task<ActionResult<List<FilamentProfileDto>>> GetFilamentProfilesForModelAsync(
        string manufacturer,
        string model,
        CancellationToken ct)
    {
        try
        {
            _logger.LogInformation($"Fetching filament profiles for {manufacturer}/{model}");

            // First, get machine profiles to find compatible filament profiles
            List<MachineProfileDto> machineProfiles;
            if (_cachedService != null)
            {
                machineProfiles = await _cachedService.GetMachineProfilesByManufacturerAsync(manufacturer, ct);
            }
            else
            {
                machineProfiles = (await _profileService.ListAvailableMachineProfilesAsync(ct))
                    .Where(p => (p.Manufacturer ?? "Unknown").Equals(manufacturer, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // Filter by printer_model field directly
            string expectedPrinterModel = $"{manufacturer} {model}".Replace("_", " ", StringComparison.Ordinal);
            HashSet<string> machineNames = machineProfiles
                .Where(p => (p.PrinterModel ?? string.Empty).Equals(expectedPrinterModel, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Name ?? string.Empty)
                .ToHashSet();

            if (machineNames.Count == 0)
            {
                return Ok(new List<FilamentProfileDto>()); // No matching machines
            }

            // Get all filament profiles and filter by compatibility
            IList<FilamentProfileDto> allFilaments = await _profileService.ListAvailableFilamentProfilesAsync(ct);

            List<FilamentProfileDto> result = allFilaments
                .Where(f =>

                    // Explicitly compatible with a machine in this model
                    (f.CompatiblePrinters != null && f.CompatiblePrinters.Any(cp => machineNames.Contains(cp))) ||

                    // From OrcaFilamentLibrary (universal)
                    (f.Manufacturer ?? string.Empty).Equals("OrcaFilamentLibrary", StringComparison.OrdinalIgnoreCase) ||

                    // No specific compatibility (universally available)
                    f.CompatiblePrinters == null || f.CompatiblePrinters.Count == 0)
                .ToList();

            _logger.LogInformation($"Returning {result.Count} filament profiles compatible with {manufacturer}/{model}");
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching filament profiles for {manufacturer}/{model}: {ex.Message}");
            return StatusCode(500, new { error = "Failed to fetch filament profiles", message = ex.Message });
        }
    }

    /// <summary>
    /// Get all profiles (machine, filament, process) for a specific manufacturer and model.
    /// This is the recommended single-call endpoint for the profile import wizard.
    /// </summary>
    /// <param name="manufacturer">Manufacturer name</param>
    /// <param name="model">Model name</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>All profiles for the specified manufacturer and model</returns>
    [HttpGet("for-model/{manufacturer}/{model}")]
    [ProducesResponseType(typeof(ModelProfilesResponseDto), 200)]
    public async Task<ActionResult<ModelProfilesResponseDto>> GetAllProfilesForModelAsync(
        string manufacturer,
        string model,
        CancellationToken ct)
    {
        try
        {
            _logger.LogInformation($"Fetching all profiles for {manufacturer}/{model}");

            // Get machine profiles
            List<MachineProfileDto> machineProfiles;
            if (_cachedService != null)
            {
                machineProfiles = await _cachedService.GetMachineProfilesByManufacturerAsync(manufacturer, ct);
            }
            else
            {
                machineProfiles = (await _profileService.ListAvailableMachineProfilesAsync(ct))
                    .Where(p => (p.Manufacturer ?? "Unknown").Equals(manufacturer, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // Filter by printer_model field directly
            string expectedPrinterModel = $"{manufacturer} {model}".Replace("_", " ", StringComparison.Ordinal);
            List<MachineProfileDto> modelMachines = machineProfiles
                .Where(p => (p.PrinterModel ?? string.Empty).Equals(expectedPrinterModel, StringComparison.OrdinalIgnoreCase))
                .ToList();

            HashSet<string> machineNames = modelMachines.Select(p => p.Name ?? string.Empty).ToHashSet();

            // Get compatible process and filament profiles
            IList<ProcessProfileDto> allProcesses = await _profileService.ListAvailableProcessProfilesAsync(ct);
            IList<FilamentProfileDto> allFilaments = await _profileService.ListAvailableFilamentProfilesAsync(ct);

            List<ProcessProfileDto> compatibleProcesses = allProcesses
                .Where(p =>
                    (p.CompatiblePrinters != null && p.CompatiblePrinters.Any(cp => machineNames.Contains(cp))) ||
                    p.CompatiblePrinters == null || p.CompatiblePrinters.Count == 0)
                .ToList();

            List<FilamentProfileDto> compatibleFilaments = allFilaments
                .Where(f =>
                    (f.CompatiblePrinters != null && f.CompatiblePrinters.Any(cp => machineNames.Contains(cp))) ||
                    (f.Manufacturer ?? string.Empty).Equals("OrcaFilamentLibrary", StringComparison.OrdinalIgnoreCase) ||
                    f.CompatiblePrinters == null || f.CompatiblePrinters.Count == 0)
                .ToList();

            ModelProfilesResponseDto result = new()
            {
                Manufacturer = manufacturer,
                Model = model,
                MachineProfiles = modelMachines,
                ProcessProfiles = compatibleProcesses,
                FilamentProfiles = compatibleFilaments
            };

            _logger.LogInformation(
                $"Returning profiles for {manufacturer}/{model}: {modelMachines.Count} machines, " +
                $"{compatibleProcesses.Count} processes, {compatibleFilaments.Count} filaments");

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching all profiles for {manufacturer}/{model}: {ex.Message}");
            return StatusCode(500, new { error = "Failed to fetch profiles", message = ex.Message });
        }
    }

    /// <summary>
    /// Get a list of all available manufacturers.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of manufacturer names</returns>
    [HttpGet("manufacturers")]
    [ProducesResponseType(typeof(List<string>), 200)]
    public async Task<ActionResult<List<string>>> GetManufacturersAsync(CancellationToken ct)
    {
        try
        {
            if (_cachedService != null)
            {
                List<string> manufacturers = await _cachedService.GetManufacturersAsync(ct);
                return Ok(manufacturers);
            }

            // Fallback: get from all profiles
            IList<MachineProfileDto> machines = await _profileService.ListAvailableMachineProfilesAsync(ct);
            List<string> result = machines
                .Where(m => !string.IsNullOrEmpty(m.Manufacturer))
                .Select(m => m.Manufacturer!)
                .Distinct()
                .OrderBy(m => m)
                .ToList();

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching manufacturers: {ex.Message}");
            return StatusCode(500, new { error = "Failed to fetch manufacturers", message = ex.Message });
        }
    }

    /// <summary>
    /// Generate a model identifier from manufacturer and model name.
    /// e.g., "Prusa", "CORE One" -> "Prusa_CORE_One"
    /// </summary>
    private static string GenerateModelId(string manufacturer, string modelName)
    {
        string modelIdentifier = modelName.Replace(" ", "_", StringComparison.Ordinal);
        return $"{manufacturer}_{modelIdentifier}";
    }
}
