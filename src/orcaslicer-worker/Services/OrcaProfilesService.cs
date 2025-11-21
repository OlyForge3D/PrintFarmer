using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Telemetry;
using Farm.Slicer.Worker.Core;
using Farm.Web.Shared;

namespace Farm.OrcaSlicer.Worker.Services;

/// <summary>
/// Service for discovering and loading OrcaSlicer profiles from the local installation.
/// 
/// OrcaSlicer stores profiles organized by manufacturer:
/// - ~/.config/OrcaSlicer/profiles/{manufacturer}.json - Bundle file listing all profiles for that manufacturer
/// - ~/.config/OrcaSlicer/profiles/{manufacturer}/ - Directory containing actual profile JSON files
///   - machine/ - Machine/printer profiles
///   - filament/ - Filament/material profiles
///   - process/ - Process/quality profiles
/// 
/// This service parses manufacturer bundles and follows sub_path references to load profiles.
/// </summary>
public class OrcaProfilesService : ISlicerProfilesService
{
    private readonly IUnifiedLoggingService _logger;
    private readonly string _orcaConfigPath;
    private readonly string _orcaProfilesPath;

    public OrcaProfilesService(IUnifiedLoggingService logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // OrcaSlicer config path: ~/.config/OrcaSlicer/
        _orcaConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "OrcaSlicer");
        _orcaProfilesPath = Path.Combine(_orcaConfigPath, "profiles");
    }

    public async Task<IList<MachineProfileDto>> ListAvailableMachineProfilesAsync(CancellationToken ct = default)
    {
        var profiles = new List<MachineProfileDto>();
        
        try
        {
            _logger.LogInformation($"Loading OrcaSlicer machine profiles from bundles in: {_orcaProfilesPath}");
            
            if (!Directory.Exists(_orcaProfilesPath))
            {
                _logger.LogWarning($"OrcaSlicer profiles directory not found: {_orcaProfilesPath}");
                return profiles;
            }

            // Find all manufacturer bundle JSON files (e.g., Prusa.json, Voron.json, etc.)
            var bundleFiles = Directory.GetFiles(_orcaProfilesPath, "*.json", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith(".")) // Skip hidden files
                .ToList();

            _logger.LogInformation($"Found {bundleFiles.Count} manufacturer bundle files");

            int successCount = 0;
            int failureCount = 0;

            foreach (var bundleFile in bundleFiles)
            {
                try
                {
                    var bundle = ParseManufacturerBundle(bundleFile);
                    if (bundle?.MachineModelList != null)
                    {
                        foreach (var entry in bundle.MachineModelList)
                        {
                            try
                            {
                                var profilePath = Path.Combine(_orcaProfilesPath, bundle.Name, entry.SubPath);
                                if (!File.Exists(profilePath))
                                {
                                    _logger.LogWarning($"Machine profile referenced in bundle not found: {profilePath}");
                                    failureCount++;
                                    continue;
                                }

                                var profile = LoadProfileFromFile<MachineProfileDto>(profilePath);
                                if (profile != null)
                                {
                                    profiles.Add(profile);
                                    successCount++;
                                }
                                else
                                {
                                    failureCount++;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning($"Failed to load machine profile '{entry.Name}' from bundle '{bundle.Name}': {ex.Message}");
                                failureCount++;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to parse manufacturer bundle '{Path.GetFileName(bundleFile)}': {ex.Message}");
                    failureCount++;
                }
            }

            _logger.LogInformation($"Loaded {successCount} machine profiles ({failureCount} failures from {bundleFiles.Count} bundles)");
            return profiles;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error loading machine profiles: {ex.Message}");
            return profiles;
        }
    }

    public async Task<IList<FilamentProfileDto>> ListAvailableFilamentProfilesAsync(CancellationToken ct = default)
    {
        var profiles = new List<FilamentProfileDto>();
        
        try
        {
            _logger.LogInformation($"Loading OrcaSlicer filament profiles from bundles in: {_orcaProfilesPath}");
            
            if (!Directory.Exists(_orcaProfilesPath))
            {
                _logger.LogWarning($"OrcaSlicer profiles directory not found: {_orcaProfilesPath}");
                return profiles;
            }

            var bundleFiles = Directory.GetFiles(_orcaProfilesPath, "*.json", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith("."))
                .ToList();

            _logger.LogInformation($"Found {bundleFiles.Count} manufacturer bundle files");

            int successCount = 0;
            int failureCount = 0;

            foreach (var bundleFile in bundleFiles)
            {
                try
                {
                    var bundle = ParseManufacturerBundle(bundleFile);
                    if (bundle?.FilamentList != null)
                    {
                        foreach (var entry in bundle.FilamentList)
                        {
                            try
                            {
                                var profilePath = Path.Combine(_orcaProfilesPath, bundle.Name, entry.SubPath);
                                if (!File.Exists(profilePath))
                                {
                                    _logger.LogWarning($"Filament profile referenced in bundle not found: {profilePath}");
                                    failureCount++;
                                    continue;
                                }

                                var profile = LoadProfileFromFile<FilamentProfileDto>(profilePath);
                                if (profile != null)
                                {
                                    profiles.Add(profile);
                                    successCount++;
                                }
                                else
                                {
                                    failureCount++;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning($"Failed to load filament profile '{entry.Name}' from bundle '{bundle.Name}': {ex.Message}");
                                failureCount++;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to parse manufacturer bundle '{Path.GetFileName(bundleFile)}': {ex.Message}");
                    failureCount++;
                }
            }

            _logger.LogInformation($"Loaded {successCount} filament profiles ({failureCount} failures from {bundleFiles.Count} bundles)");
            return profiles;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error loading filament profiles: {ex.Message}");
            return profiles;
        }
    }

    public async Task<IList<ProcessProfileDto>> ListAvailableProcessProfilesAsync(CancellationToken ct = default)
    {
        var profiles = new List<ProcessProfileDto>();
        
        try
        {
            _logger.LogInformation($"Loading OrcaSlicer process profiles from bundles in: {_orcaProfilesPath}");
            
            if (!Directory.Exists(_orcaProfilesPath))
            {
                _logger.LogWarning($"OrcaSlicer profiles directory not found: {_orcaProfilesPath}");
                return profiles;
            }

            var bundleFiles = Directory.GetFiles(_orcaProfilesPath, "*.json", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith("."))
                .ToList();

            _logger.LogInformation($"Found {bundleFiles.Count} manufacturer bundle files");

            int successCount = 0;
            int failureCount = 0;

            foreach (var bundleFile in bundleFiles)
            {
                try
                {
                    var bundle = ParseManufacturerBundle(bundleFile);
                    if (bundle?.ProcessList != null)
                    {
                        foreach (var entry in bundle.ProcessList)
                        {
                            try
                            {
                                var profilePath = Path.Combine(_orcaProfilesPath, bundle.Name, entry.SubPath);
                                if (!File.Exists(profilePath))
                                {
                                    _logger.LogWarning($"Process profile referenced in bundle not found: {profilePath}");
                                    failureCount++;
                                    continue;
                                }

                                var profile = LoadProfileFromFile<ProcessProfileDto>(profilePath);
                                if (profile != null)
                                {
                                    profiles.Add(profile);
                                    successCount++;
                                }
                                else
                                {
                                    failureCount++;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning($"Failed to load process profile '{entry.Name}' from bundle '{bundle.Name}': {ex.Message}");
                                failureCount++;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to parse manufacturer bundle '{Path.GetFileName(bundleFile)}': {ex.Message}");
                    failureCount++;
                }
            }

            _logger.LogInformation($"Loaded {successCount} process profiles ({failureCount} failures from {bundleFiles.Count} bundles)");
            return profiles;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error loading process profiles: {ex.Message}");
            return profiles;
        }
    }

    private ManufacturerBundleDto? ParseManufacturerBundle(string bundleFilePath)
    {
        try
        {
            var json = File.ReadAllText(bundleFilePath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<ManufacturerBundleDto>(json, options);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to parse manufacturer bundle {Path.GetFileName(bundleFilePath)}: {ex.Message}");
            return null;
        }
    }

    private T? LoadProfileFromFile<T>(string filePath) where T : class, new()
    {
        try
        {
            var json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Skip profiles with "instantiation": false - these are not actual profiles
            if (root.TryGetProperty("instantiation", out var instantiationElem) && instantiationElem.ValueKind == JsonValueKind.False)
            {
                return null;
            }

            return typeof(T).Name switch
            {
                nameof(MachineProfileDto) => ParseMachineProfile(root, filePath) as T,
                nameof(FilamentProfileDto) => ParseFilamentProfile(root, filePath) as T,
                nameof(ProcessProfileDto) => ParseProcessProfile(root, filePath) as T,
                _ => null
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to load profile from {filePath}: {ex.Message}");
            return null;
        }
    }

    private MachineProfileDto? ParseMachineProfile(JsonElement root, string filePath)
    {
        var profile = new MachineProfileDto();

        if (root.TryGetProperty("name", out var nameElem))
            profile.Name = nameElem.GetString() ?? string.Empty;

        if (root.TryGetProperty("manufacturer", out var mfgElem))
            profile.Manufacturer = mfgElem.GetString() ?? string.Empty;

        // Extract nozzle diameter from settings - REQUIRED property
        // nozzle_diameter is typically an array like ["0.4"], get the first value
        if (root.TryGetProperty("nozzle_diameter", out var nozzleElem))
        {
            if (nozzleElem.ValueKind == JsonValueKind.Array)
            {
                var nozzleArray = nozzleElem.EnumerateArray().FirstOrDefault();
                profile.NozzleDiameter = ParseDoubleValue(nozzleArray);
            }
            else
            {
                profile.NozzleDiameter = ParseDoubleValue(nozzleElem);
            }
        }
        else
        {
            _logger.LogWarning($"Machine profile '{profile.Name}' missing required 'nozzle_diameter' property in {filePath}");
            return null; // Reject profiles without nozzle_diameter
        }

        // Store all settings as raw JSON for flexibility
        profile.Settings = SerializeElementToDict(root);

        return profile;
    }

    private FilamentProfileDto? ParseFilamentProfile(JsonElement root, string filePath)
    {
        var profile = new FilamentProfileDto();

        if (root.TryGetProperty("name", out var nameElem))
            profile.Name = nameElem.GetString() ?? string.Empty;

        if (root.TryGetProperty("filament_type", out var typeElem))
            profile.Material = typeElem.GetString() ?? "PLA";
        else if (root.TryGetProperty("material", out var matElem))
            profile.Material = matElem.GetString() ?? "PLA";

        if (root.TryGetProperty("nozzle_temperature", out var nozzleElem))
            profile.NozzleTemperature = ParseIntValue(nozzleElem) ?? 210;

        if (root.TryGetProperty("bed_temperature", out var bedElem))
            profile.BedTemperature = ParseIntValue(bedElem) ?? 60;

        if (root.TryGetProperty("travel_speed", out var speedElem))
            profile.PrintSpeed = ParseIntValue(speedElem) ?? 50;

        // Store all settings as raw JSON for flexibility
        profile.Settings = SerializeElementToDict(root);

        return profile;
    }

    private ProcessProfileDto? ParseProcessProfile(JsonElement root, string filePath)
    {
        var profile = new ProcessProfileDto();

        if (root.TryGetProperty("name", out var nameElem))
            profile.Name = nameElem.GetString() ?? string.Empty;

        if (root.TryGetProperty("layer_height", out var layerElem))
            profile.LayerHeight = ParseDoubleValue(layerElem) ?? 0.2;

        if (root.TryGetProperty("fill_density", out var infillElem))
            profile.InfillPercentage = ParseIntValue(infillElem) ?? 20;

        if (root.TryGetProperty("wall_loops", out var speedElem))
            profile.PrintSpeed = ParseIntValue(speedElem) ?? 50;

        if (root.TryGetProperty("enable_support", out var supportsElem))
            profile.Supports = ParseBoolValue(supportsElem);

        // Determine quality based on layer height
        if (profile.LayerHeight <= 0.15)
            profile.Quality = "fine";
        else if (profile.LayerHeight >= 0.28)
            profile.Quality = "draft";
        else
            profile.Quality = "standard";

        // Store all settings as raw JSON for flexibility
        profile.Settings = SerializeElementToDict(root);

        return profile;
    }

    private int? ParseIntValue(JsonElement elem)
    {
        if (elem.ValueKind == JsonValueKind.Number)
            return elem.TryGetInt32(out var val) ? val : null;
        else if (elem.ValueKind == JsonValueKind.String)
            return int.TryParse(elem.GetString(), out var val) ? val : null;
        return null;
    }

    private double? ParseDoubleValue(JsonElement elem)
    {
        if (elem.ValueKind == JsonValueKind.Number)
            return elem.TryGetDouble(out var val) ? val : null;
        else if (elem.ValueKind == JsonValueKind.String)
            return double.TryParse(elem.GetString(), out var val) ? val : null;
        return null;
    }

    private bool ParseBoolValue(JsonElement elem)
    {
        if (elem.ValueKind == JsonValueKind.True)
            return true;
        else if (elem.ValueKind == JsonValueKind.String)
            return elem.GetString() == "true" || elem.GetString() == "1";
        return false;
    }

    private Dictionary<string, object> SerializeElementToDict(JsonElement elem)
    {
        var dict = new Dictionary<string, object>();
        try
        {
            if (elem.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in elem.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.GetRawText();
                }
            }
        }
        catch
        {
            // If serialization fails, return empty dict
        }
        return dict;
    }
}
