using Farm.Infrastructure.Telemetry;
using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Worker.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.OrcaSlicer.Worker.Controllers;

/// <summary>
/// Exposes OrcaSlicer profiles organized by manufacturer.
/// Profiles are discovered from the system installation and organized by manufacturer hierarchy.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Tags("Slicer Profiles")]
public class ProfilesController(ISlicerProfilesService profileService, ILogger<ProfilesController> logger) : ControllerBase
{
    private readonly ISlicerProfilesService _profileService = profileService;
    private readonly CachedOrcaProfilesService? _cachedService = profileService as CachedOrcaProfilesService;
    private readonly ILogger<ProfilesController> _logger = logger;

    /// <summary>
    /// Get all available slicer profiles organized by manufacturer and model hierarchy.
    /// </summary>
    /// <remarks>
    /// Returns profiles organized as: Manufacturer -> Model -> (Machine Profiles + Filament Profiles + Process Profiles)
    /// Filament and process profiles are associated with machine profiles via the compatible_printers array.
    /// Compatible printers are resolved from both explicit compatible_printers arrays and compatible_printers_condition expressions.
    /// </remarks>
    /// <param name="manufacturer">Optional manufacturer name to filter by</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>All available profiles organized by manufacturer and model</returns>
    [HttpGet]
    [ProducesResponseType(typeof(AllProfilesResponseDto), 200)]
    public async Task<ActionResult<AllProfilesResponseDto>> GetAllProfilesAsync([FromQuery] string? manufacturer, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Fetching all OrcaSlicer profiles organized by manufacturer and model hierarchy{ManufacturerFilter}", string.IsNullOrEmpty(manufacturer) ? string.Empty : $" for manufacturer '{manufacturer}'");

            // Load machine model profiles (base templates from machine_model_list)
            IList<MachineModelProfileDto> machineModelProfiles = await _profileService.ListAvailableMachineModelProfilesAsync(ct);

            // Load machine profiles (nozzle variants from machine_list)
            IList<MachineProfileDto> machineProfiles = await _profileService.ListAvailableMachineProfilesAsync(ct);
            IList<FilamentProfileDto> filamentProfiles = await _profileService.ListAvailableFilamentProfilesAsync(ct);
            IList<ProcessProfileDto> processProfiles = await _profileService.ListAvailableProcessProfilesAsync(ct);

            // Filter by manufacturer if specified
            if (!string.IsNullOrEmpty(manufacturer))
            {
                machineModelProfiles = machineModelProfiles
                    .Where(p => (p.Manufacturer ?? "Unknown").Equals(manufacturer, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                machineProfiles = machineProfiles
                    .Where(p => (p.Manufacturer ?? "Unknown").Equals(manufacturer, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // Note: Filament/Process profiles may not have Manufacturer set or may be from "OrcaFilamentLibrary" / Generic
                // So we don't filter them strictly by manufacturer property yet,
                // we filter them by compatibility with the filtered machines in the loop below.
            }

            // Build the hierarchy organized by manufacturer and model
            Dictionary<string, ManufacturerProfilesDto> byHierarchy = new();

            // Group machine profiles by manufacturer
            Dictionary<string, List<MachineProfileDto>> machinesByManufacturer = machineProfiles
                .GroupBy(p => p.Manufacturer ?? "Unknown")
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach ((string mfgName, List<MachineProfileDto> machines) in machinesByManufacturer)
            {
                ManufacturerProfilesDto manufacturerProfiles = new()
                { Name = mfgName };
                Dictionary<string, PrinterModelProfilesDto> models = new();

                // Group machine profiles by printer_model field (already parsed from JSON)
                // e.g., "Elegoo Centauri Carbon" groups all nozzle variants together
                Dictionary<string, List<MachineProfileDto>> machinesByModelName = machines
                    .GroupBy(m => m.PrinterModel ?? m.Name ?? "Unknown")
                    .ToDictionary(g => g.Key, g => g.ToList());

                // For each model, collect its machine, filament, and process profiles
                foreach ((string modelName, List<MachineProfileDto> modelMachines) in machinesByModelName)
                {
                    // modelName IS the printer_model (OrcaSlicer alias) - use it directly
                    PrinterModelProfilesDto modelProfiles = new()
                    {
                        Name = modelName,
                        ModelId = modelName
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

                    models[modelName] = modelProfiles;
                }

                manufacturerProfiles.Models = models;
                byHierarchy[mfgName] = manufacturerProfiles;
            }

            // Also provide legacy flat structure for backward compatibility
            AllProfilesResponseDto response = new()
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

            _logger.LogInformation("Returning {MachineModelProfilesCount} machine model, {MachineProfilesCount} machine, {FilamentProfilesCount} filament, {ProcessProfilesCount} process profiles in {ByHierarchyCount} manufacturers", machineModelProfiles.Count, machineProfiles.Count, filamentProfiles.Count, processProfiles.Count, byHierarchy.Count);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error fetching profiles: {Message}", ex.Message);
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
            _logger.LogError("Error fetching machine profiles: {Message}", ex.Message);
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
            _logger.LogError("Error fetching filament profiles: {Message}", ex.Message);
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
            _logger.LogError("Error fetching process profiles: {Message}", ex.Message);
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
            _logger.LogError("Error fetching profiles for manufacturer '{Manufacturer}': {Message}", manufacturer, ex.Message);
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
            _logger.LogInformation("Fetching printer models for manufacturer '{Manufacturer}'", manufacturer);

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

            _logger.LogInformation("Returning {ModelsCount} models for manufacturer '{Manufacturer}'", models.Count, manufacturer);
            return Ok(models);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error fetching models for manufacturer '{Manufacturer}': {Message}", manufacturer, ex.Message);
            return StatusCode(500, new { error = "Failed to fetch printer models", message = ex.Message });
        }
    }

    /// <summary>
    /// Get machine profiles by printer_model (OrcaSlicer alias).
    /// Pass the exact printer_model value from OrcaSlicer (e.g., "Thinker X400", "RatRig V-Core 4 HYBRID 400").
    /// </summary>
    /// <param name="printerModel">The exact printer_model value (OrcaSlicer alias)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Machine profiles matching the printer_model</returns>
    [HttpGet("machine/{printerModel}")]
    [ProducesResponseType(typeof(List<MachineProfileDto>), 200)]
    public async Task<ActionResult<List<MachineProfileDto>>> GetMachineProfilesAsync(
        string printerModel,
        CancellationToken ct)
    {
        try
        {
            // Normalize underscores to spaces (URL encoding)
            string normalizedModel = printerModel.Replace("_", " ", StringComparison.Ordinal);

            _logger.LogInformation("Fetching machine profiles for printer_model='{NormalizedModel}'", normalizedModel);

            List<MachineProfileDto> result;
            if (_cachedService != null)
            {
                result = await _cachedService.GetMachineProfilesByPrinterModelAsync(normalizedModel, ct);
            }
            else
            {
                // Fallback: filter in memory
                result = (await _profileService.ListAvailableMachineProfilesAsync(ct))
                    .Where(p => (p.PrinterModel ?? string.Empty).Equals(normalizedModel, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            _logger.LogInformation("Returning {Count} machine profiles for '{NormalizedModel}'", result.Count, normalizedModel);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error fetching machine profiles for '{PrinterModel}': {Message}", printerModel, ex.Message);
            return StatusCode(500, new { error = "Failed to fetch machine profiles", message = ex.Message });
        }
    }

    /// <summary>
    /// Get machine profiles by manufacturer name and printer model name.
    /// Used by slicer-host to fetch profiles for a specific printer model within a manufacturer.
    /// </summary>
    /// <param name="manufacturer">Manufacturer name (e.g., "Elegoo")</param>
    /// <param name="model">Printer model name (e.g., "Elegoo Centauri Carbon")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Machine profiles matching the manufacturer and model</returns>
    [HttpGet("machine/{manufacturer}/{model}")]
    [ProducesResponseType(typeof(List<MachineProfileDto>), 200)]
    public async Task<ActionResult<List<MachineProfileDto>>> GetMachineProfilesByManufacturerAndModelAsync(
        string manufacturer,
        string model,
        CancellationToken ct)
    {
        try
        {
            string normalizedModel = Uri.UnescapeDataString(model).Replace("_", " ", StringComparison.Ordinal);
            string normalizedMfg = Uri.UnescapeDataString(manufacturer);

            _logger.LogInformation("Fetching machine profiles for manufacturer='{Manufacturer}', model='{Model}'", normalizedMfg, normalizedModel);

            IList<MachineProfileDto> allProfiles = await _profileService.ListAvailableMachineProfilesAsync(ct);

            List<MachineProfileDto> result = allProfiles
                .Where(p =>
                    (p.Manufacturer ?? "Unknown").Equals(normalizedMfg, StringComparison.OrdinalIgnoreCase) &&
                    ((p.PrinterModel ?? string.Empty).Equals(normalizedModel, StringComparison.OrdinalIgnoreCase) ||
                     (p.Name ?? string.Empty).Equals(normalizedModel, StringComparison.OrdinalIgnoreCase) ||
                     (p.Name ?? string.Empty).StartsWith(normalizedModel, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            _logger.LogInformation("Returning {Count} machine profiles for manufacturer='{Manufacturer}', model='{Model}'", result.Count, normalizedMfg, normalizedModel);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error fetching machine profiles for manufacturer='{Manufacturer}', model='{Model}': {Message}", manufacturer, model, ex.Message);
            return StatusCode(500, new { error = "Failed to fetch machine profiles", message = ex.Message });
        }
    }

    /// <summary>
    /// Get process profiles compatible with a specific printer_model (OrcaSlicer alias).
    /// </summary>
    /// <param name="printerModel">The printer_model value (OrcaSlicer alias)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Process profiles compatible with machines matching the printer_model</returns>
    [HttpGet("process/{printerModel}")]
    [ProducesResponseType(typeof(List<ProcessProfileDto>), 200)]
    public async Task<ActionResult<List<ProcessProfileDto>>> GetProcessProfilesAsync(
        string printerModel,
        CancellationToken ct)
    {
        try
        {
            string normalizedModel = printerModel.Replace("_", " ", StringComparison.Ordinal);
            _logger.LogInformation("Fetching process profiles for printer_model='{NormalizedModel}'", normalizedModel);

            // Get machine profiles matching the printer_model
            List<MachineProfileDto> machineProfiles;
            if (_cachedService != null)
            {
                machineProfiles = await _cachedService.GetMachineProfilesByPrinterModelAsync(normalizedModel, ct);
            }
            else
            {
                machineProfiles = (await _profileService.ListAvailableMachineProfilesAsync(ct))
                    .Where(p => (p.PrinterModel ?? string.Empty).Equals(normalizedModel, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            HashSet<string> machineNames = machineProfiles
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

            _logger.LogInformation("Returning {Count} process profiles for '{NormalizedModel}'", result.Count, normalizedModel);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error fetching process profiles for '{PrinterModel}': {Message}", printerModel, ex.Message);
            return StatusCode(500, new { error = "Failed to fetch process profiles", message = ex.Message });
        }
    }

    /// <summary>
    /// Get filament profiles compatible with a specific printer_model (OrcaSlicer alias).
    /// </summary>
    /// <param name="printerModel">The printer_model value (OrcaSlicer alias)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Filament profiles compatible with machines matching the printer_model</returns>
    [HttpGet("filament/{printerModel}")]
    [ProducesResponseType(typeof(List<FilamentProfileDto>), 200)]
    public async Task<ActionResult<List<FilamentProfileDto>>> GetFilamentProfilesAsync(
        string printerModel,
        CancellationToken ct)
    {
        try
        {
            string normalizedModel = printerModel.Replace("_", " ", StringComparison.Ordinal);
            _logger.LogInformation("Fetching filament profiles for printer_model='{NormalizedModel}'", normalizedModel);

            // Get machine profiles matching the printer_model
            List<MachineProfileDto> machineProfiles;
            if (_cachedService != null)
            {
                machineProfiles = await _cachedService.GetMachineProfilesByPrinterModelAsync(normalizedModel, ct);
            }
            else
            {
                machineProfiles = (await _profileService.ListAvailableMachineProfilesAsync(ct))
                    .Where(p => (p.PrinterModel ?? string.Empty).Equals(normalizedModel, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            HashSet<string> machineNames = machineProfiles
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

            _logger.LogInformation("Returning {Count} filament profiles for '{NormalizedModel}'", result.Count, normalizedModel);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error fetching filament profiles for '{PrinterModel}': {Message}", printerModel, ex.Message);
            return StatusCode(500, new { error = "Failed to fetch filament profiles", message = ex.Message });
        }
    }

    /// <summary>
    /// Get all profiles (machine, filament, process) for a specific printer_model (OrcaSlicer alias).
    /// This is the recommended single-call endpoint for the profile import wizard.
    /// </summary>
    /// <param name="printerModel">The printer_model value (OrcaSlicer alias)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>All profiles for the specified printer_model</returns>
    [HttpGet("for-model/{printerModel}")]
    [ProducesResponseType(typeof(ModelProfilesResponseDto), 200)]
    public async Task<ActionResult<ModelProfilesResponseDto>> GetAllProfilesForModelAsync(
        string printerModel,
        CancellationToken ct)
    {
        try
        {
            string normalizedModel = printerModel.Replace("_", " ", StringComparison.Ordinal);
            _logger.LogInformation("Fetching all profiles for printer_model='{NormalizedModel}'", normalizedModel);

            // Get machine profiles matching the printer_model
            List<MachineProfileDto> machineProfiles;
            if (_cachedService != null)
            {
                machineProfiles = await _cachedService.GetMachineProfilesByPrinterModelAsync(normalizedModel, ct);
            }
            else
            {
                machineProfiles = (await _profileService.ListAvailableMachineProfilesAsync(ct))
                    .Where(p => (p.PrinterModel ?? string.Empty).Equals(normalizedModel, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            HashSet<string> machineNames = machineProfiles.Select(p => p.Name ?? string.Empty).ToHashSet();

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

            // Extract manufacturer from first machine profile (they all share the same printer_model)
            string manufacturer = machineProfiles.FirstOrDefault()?.Manufacturer ?? "Unknown";

            ModelProfilesResponseDto result = new()
            {
                Manufacturer = manufacturer,
                Model = normalizedModel,
                MachineProfiles = machineProfiles,
                ProcessProfiles = compatibleProcesses,
                FilamentProfiles = compatibleFilaments
            };

            _logger.LogInformation(
                "Returning profiles for '{NormalizedModel}': {MachineCount} machines, {ProcessCount} processes, {FilamentCount} filaments",
                normalizedModel, machineProfiles.Count, compatibleProcesses.Count, compatibleFilaments.Count);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error fetching all profiles for '{PrinterModel}': {Message}", printerModel, ex.Message);
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
            _logger.LogError("Error fetching manufacturers: {Message}", ex.Message);
            return StatusCode(500, new { error = "Failed to fetch manufacturers", message = ex.Message });
        }
    }

    /// <summary>
    /// Get process profiles compatible with specific machine profile names.
    /// This is the recommended endpoint after user selects machine profiles in the wizard.
    /// </summary>
    /// <param name="request">Request containing machine profile names</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Process profiles compatible with the specified machines</returns>
    [HttpPost("process/for-machines")]
    [ProducesResponseType(typeof(List<ProcessProfileDto>), 200)]
    public async Task<ActionResult<List<ProcessProfileDto>>> GetProcessProfilesForMachinesAsync(
        [FromBody] MachineNamesRequest request,
        CancellationToken ct)
    {
        try
        {
            if (request.MachineNames == null || request.MachineNames.Count == 0)
            {
                return BadRequest(new { error = "At least one machine name is required" });
            }

            _logger.LogInformation("Fetching process profiles for {Count} machine(s)", request.MachineNames.Count);

            HashSet<string> machineNames = request.MachineNames.ToHashSet();

            IList<ProcessProfileDto> allProcesses = await _profileService.ListAvailableProcessProfilesAsync(ct);

            // ONLY return profiles that explicitly list one of the selected machines
            // Do NOT include universal profiles (those with empty compatible_printers)
            List<ProcessProfileDto> result = allProcesses
                .Where(p => p.CompatiblePrinters != null &&
                            p.CompatiblePrinters.Count > 0 &&
                            p.CompatiblePrinters.Any(cp => machineNames.Contains(cp)))
                .ToList();

            _logger.LogInformation("Returning {Count} process profiles for machines: {StringJoin}", result.Count, string.Join(", ", request.MachineNames));
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error fetching process profiles for machines: {Message}", ex.Message);
            return StatusCode(500, new { error = "Failed to fetch process profiles", message = ex.Message });
        }
    }

    /// <summary>
    /// Get filament profiles compatible with specific machine profile names.
    /// This is the recommended endpoint after user selects machine profiles in the wizard.
    /// </summary>
    /// <param name="request">Request containing machine profile names</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Filament profiles compatible with the specified machines or universal</returns>
    [HttpPost("filament/for-machines")]
    [ProducesResponseType(typeof(List<FilamentProfileDto>), 200)]
    public async Task<ActionResult<List<FilamentProfileDto>>> GetFilamentProfilesForMachinesAsync(
        [FromBody] MachineNamesRequest request,
        CancellationToken ct)
    {
        try
        {
            if (request.MachineNames == null || request.MachineNames.Count == 0)
            {
                return BadRequest(new { error = "At least one machine name is required" });
            }

            _logger.LogInformation("Fetching filament profiles for {Count} machine(s)", request.MachineNames.Count);

            HashSet<string> machineNames = request.MachineNames.ToHashSet();

            IList<FilamentProfileDto> allFilaments = await _profileService.ListAvailableFilamentProfilesAsync(ct);

            // Return profiles that:
            // 1. Explicitly list one of the selected machines, OR
            // 2. Are from OrcaFilamentLibrary (universal), OR
            // 3. Have no compatible_printers (universal)
            List<FilamentProfileDto> result = allFilaments
                .Where(f =>
                    (f.CompatiblePrinters != null && f.CompatiblePrinters.Any(cp => machineNames.Contains(cp))) ||
                    (f.Manufacturer ?? string.Empty).Equals("OrcaFilamentLibrary", StringComparison.OrdinalIgnoreCase) ||
                    f.CompatiblePrinters == null || f.CompatiblePrinters.Count == 0)
                .ToList();

            _logger.LogInformation("Returning {Count} filament profiles for machines: {StringJoin}", result.Count, string.Join(", ", request.MachineNames));
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error fetching filament profiles for machines: {Message}", ex.Message);
            return StatusCode(500, new { error = "Failed to fetch filament profiles", message = ex.Message });
        }
    }

    /// <summary>
    /// Get template filament profiles from the OrcaFilamentLibrary.
    /// These are universal profiles not tied to specific printers.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Universal filament profiles from OrcaFilamentLibrary</returns>
    [HttpGet("filament/templates")]
    [ProducesResponseType(typeof(List<FilamentProfileDto>), 200)]
    public async Task<ActionResult<List<FilamentProfileDto>>> GetFilamentTemplatesAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Fetching OrcaFilamentLibrary template profiles");

            IList<FilamentProfileDto> allFilaments = await _profileService.ListAvailableFilamentProfilesAsync(ct);

            // Return only OrcaFilamentLibrary profiles
            List<FilamentProfileDto> result = allFilaments
                .Where(f => (f.Manufacturer ?? string.Empty).Equals("OrcaFilamentLibrary", StringComparison.OrdinalIgnoreCase))
                .ToList();

            _logger.LogInformation("Returning {Count} OrcaFilamentLibrary template profiles", result.Count);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error fetching filament templates: {Message}", ex.Message);
            return StatusCode(500, new { error = "Failed to fetch filament templates", message = ex.Message });
        }
    }
}

/// <summary>
/// Request DTO for fetching profiles by machine names.
/// </summary>
public class MachineNamesRequest
{
    /// <summary>
    /// List of machine profile names (e.g., "Elegoo Centauri Carbon 0.4 nozzle")
    /// </summary>
    public List<string> MachineNames { get; set; } = [];
}
