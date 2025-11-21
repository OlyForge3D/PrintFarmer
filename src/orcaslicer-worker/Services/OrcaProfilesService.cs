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
/// Service for discovering and exporting OrcaSlicer profiles from the local installation.
/// OrcaSlicer stores profiles in:
/// - ~/.config/OrcaSlicer/profiles/printer/ (machine profiles)
/// - ~/.config/OrcaSlicer/profiles/filament/ (filament/material profiles)
/// - ~/.config/OrcaSlicer/profiles/process/ (process/quality profiles)
/// </summary>
public class OrcaProfilesService : ISlicerProfilesService
{
    private readonly IUnifiedLoggingService _logger;
    private readonly string _orcaConfigPath;

    public OrcaProfilesService(IUnifiedLoggingService logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // OrcaSlicer config path: ~/.config/OrcaSlicer/
        _orcaConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "OrcaSlicer");
    }

    public async Task<IList<MachineProfileDto>> ListAvailableMachineProfilesAsync(CancellationToken ct = default)
    {
        return await LoadProfilesOfTypeAsync<MachineProfileDto>("printer", ct);
    }

    public async Task<IList<FilamentProfileDto>> ListAvailableFilamentProfilesAsync(CancellationToken ct = default)
    {
        return await LoadProfilesOfTypeAsync<FilamentProfileDto>("filament", ct);
    }

    public async Task<IList<ProcessProfileDto>> ListAvailableProcessProfilesAsync(CancellationToken ct = default)
    {
        return await LoadProfilesOfTypeAsync<ProcessProfileDto>("process", ct);
    }

    private async Task<IList<T>> LoadProfilesOfTypeAsync<T>(string profileType, CancellationToken ct) where T : class, new()
    {
        var profiles = new List<T>();

        try
        {
            _logger.LogInformation($"Loading {profileType} profiles from: {_orcaConfigPath}");
            
            if (!Directory.Exists(_orcaConfigPath))
            {
                _logger.LogWarning($"OrcaSlicer config directory not found: {_orcaConfigPath}");
                return profiles;
            }

            var typePath = Path.Combine(_orcaConfigPath, "profiles", profileType);
            if (!Directory.Exists(typePath))
            {
                _logger.LogInformation($"Profile type directory not found: {typePath}");
                return profiles;
            }

            var jsonFiles = Directory.GetFiles(typePath, "*.json", SearchOption.TopDirectoryOnly);
            _logger.LogInformation($"Found {jsonFiles.Length} JSON files in {profileType} directory");
            
            int successCount = 0;
            int failureCount = 0;

            foreach (var filePath in jsonFiles)
            {
                try
                {
                    var profile = ParseProfile(filePath, profileType);
                    if (profile is T typedProfile && typedProfile != null)
                    {
                        profiles.Add(typedProfile);
                        successCount++;
                    }
                    else
                    {
                        _logger.LogWarning($"Failed to parse {profileType} profile {Path.GetFileName(filePath)} into correct type");
                        failureCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Exception parsing {profileType} profile {Path.GetFileName(filePath)}: {ex.Message}");
                    failureCount++;
                }
            }

            _logger.LogInformation($"Loaded {successCount} {profileType} profiles ({failureCount} failures from {jsonFiles.Length} files)");
            return profiles;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error loading {profileType} profiles: {ex.Message}");
            return profiles;
        }
    }

    private object? ParseProfile(string filePath, string profileType)
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

            return profileType switch
            {
                "printer" => ParseMachineProfile(root, filePath),
                "filament" => ParseFilamentProfile(root, filePath),
                "process" => ParseProcessProfile(root, filePath),
                _ => null
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to parse {profileType} profile {filePath}: {ex.Message}");
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
