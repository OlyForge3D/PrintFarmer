using Farm.Infrastructure.Telemetry;
using Farm.Slicer.Worker.Core;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;

namespace Farm.OrcaSlicer.Worker.Controllers;

/// <summary>
/// Exposes OrcaSlicer profiles organized by manufacturer.
/// Profiles are discovered from the system installation and organized by manufacturer hierarchy.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Tags("Slicer Profiles")]
public class ProfilesController : ControllerBase
{
    private readonly ISlicerProfilesService _profileService;
    private readonly IUnifiedLoggingService _logger;

    public ProfilesController(ISlicerProfilesService profileService, IUnifiedLoggingService logger)
    {
        _profileService = profileService;
        _logger = logger;
    }

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
            
            var machineProfiles = await _profileService.ListAvailableMachineProfilesAsync(ct);
            var filamentProfiles = await _profileService.ListAvailableFilamentProfilesAsync(ct);
            var processProfiles = await _profileService.ListAvailableProcessProfilesAsync(ct);

            // Build the hierarchy organized by manufacturer and model
            var byHierarchy = new Dictionary<string, ManufacturerProfilesDto>();
            
            // Group machine profiles by manufacturer
            var machinesByManufacturer = machineProfiles
                .GroupBy(p => p.Manufacturer ?? "Unknown")
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var (manufacturer, machines) in machinesByManufacturer)
            {
                var manufacturerProfiles = new ManufacturerProfilesDto { Name = manufacturer };
                var models = new Dictionary<string, PrinterModelProfilesDto>();

                // Group machine profiles by model using name matching
                // Machine profile names are like "Prusa CORE One 0.4 nozzle"
                // Model identifiers can be extracted from these names
                var machinesByModelName = new Dictionary<string, List<MachineProfileDto>>();
                foreach (var machine in machines)
                {
                    var modelName = ExtractModelName(machine.Name ?? "");
                    if (!machinesByModelName.ContainsKey(modelName))
                        machinesByModelName[modelName] = new List<MachineProfileDto>();
                    machinesByModelName[modelName].Add(machine);
                }

                // For each model, collect its machine, filament, and process profiles
                foreach (var (modelName, modelMachines) in machinesByModelName)
                {
                    var modelId = GenerateModelId(manufacturer, modelName);
                    var modelProfiles = new PrinterModelProfilesDto
                    {
                        Name = modelName,
                        ModelId = modelId
                    };

                    // Add all machine profiles for this model
                    modelProfiles.MachineProfiles = modelMachines.Cast<MachineProfileDto>().ToList();

                    // Find filament and process profiles compatible with any machine in this model
                    var machineProfileNames = modelMachines.Select(m => m.Name ?? "").ToList();
                    
                    // Filter filament profiles: include if compatible_printers contains any machine in this model
                    modelProfiles.FilamentProfiles = filamentProfiles
                        .Where(f => f.CompatiblePrinters != null && f.CompatiblePrinters.Any(cp => machineProfileNames.Contains(cp)))
                        .ToList();

                    // Filter process profiles: include if compatible_printers contains any machine in this model
                    modelProfiles.ProcessProfiles = processProfiles
                        .Where(p => p.CompatiblePrinters != null && p.CompatiblePrinters.Any(cp => machineProfileNames.Contains(cp)))
                        .ToList();

                    models[modelId] = modelProfiles;
                }

                manufacturerProfiles.Models = models;
                byHierarchy[manufacturer] = manufacturerProfiles;
            }

            // Also provide legacy flat structure for backward compatibility
            var response = new AllProfilesResponseDto
            {
                ByHierarchy = byHierarchy,
                
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

            _logger.LogInformation($"Returning {machineProfiles.Count} machine, {filamentProfiles.Count} filament, {processProfiles.Count} process profiles in {byHierarchy.Count} manufacturers");
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
            var profiles = await _profileService.ListAvailableMachineProfilesAsync(ct);
            var grouped = profiles
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
            var profiles = await _profileService.ListAvailableFilamentProfilesAsync(ct);
            var grouped = profiles
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
            var profiles = await _profileService.ListAvailableProcessProfilesAsync(ct);
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
            var machineProfiles = (await _profileService.ListAvailableMachineProfilesAsync(ct))
                .Where(p => (p.Manufacturer ?? "Unknown").Equals(manufacturer, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var filamentProfiles = (await _profileService.ListAvailableFilamentProfilesAsync(ct))
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
    /// Extract the base model name from a machine profile name.
    /// e.g., "Prusa CORE One 0.4 nozzle" -> "Prusa CORE One"
    /// </summary>
    private string ExtractModelName(string machineName)
    {
        // Machine profile names follow pattern: "{Model} {Variant}" where variant is like "0.4 nozzle"
        // We need to remove nozzle size suffixes
        var parts = machineName.Split(new[] { " 0." }, StringSplitOptions.None);
        if (parts.Length > 1)
        {
            return parts[0].Trim();
        }
        return machineName;
    }

    /// <summary>
    /// Generate a model identifier from manufacturer and model name.
    /// e.g., "Prusa", "CORE One" -> "Prusa_CORE_One"
    /// </summary>
    private string GenerateModelId(string manufacturer, string modelName)
    {
        var modelIdentifier = modelName.Replace(" ", "_");
        return $"{manufacturer}_{modelIdentifier}";
    }
}
