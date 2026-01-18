using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Telemetry;
using Farm.Slicer.Worker.Core;

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
    private readonly string _orcaProfilesPath;

    // Cache for loaded profile JSON as strings to minimize disk I/O
    // Key: full file path, Value: JSON string
    private readonly Dictionary<string, string> _profileJsonCache = new();
    private readonly Lock _cacheLock = new();

    // Cache for machines by manufacturer to support compatible_printers_condition evaluation
    private Dictionary<string, List<MachineProfileDto>>? _machinesByManufacturerCache;
    private readonly Lock _machineCacheLock = new();

    // Cache for fully loaded profile lists to avoid reparsing on subsequent calls
    private List<MachineProfileDto>? _allMachineProfilesCache;
    private List<FilamentProfileDto>? _allFilamentProfilesCache;
    private List<ProcessProfileDto>? _allProcessProfilesCache;
    private readonly Lock _profilesCacheLock = new();

    public OrcaProfilesService(IUnifiedLoggingService logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // Check for environment variable override (useful for testing with sample profiles)
        string? envPath = Environment.GetEnvironmentVariable("ORCA_PROFILES_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && Directory.Exists(envPath))
        {
            _orcaProfilesPath = envPath;
        }
        else
        {
            // In container environment, use the system installation profiles directly
            // OrcaSlicer AppImage extracts to /opt/orcaslicer/resources/profiles
            _orcaProfilesPath = "/opt/orcaslicer/resources/profiles";
        }
    }

#pragma warning disable CS1998
    public async Task<IList<MachineProfileDto>> ListAvailableMachineProfilesAsync(CancellationToken ct = default)
    {
        // Return from cache if available
        lock (_profilesCacheLock)
        {
            if (_allMachineProfilesCache != null)
            {
                _logger.LogInformation($"Returning {_allMachineProfilesCache.Count} machine profiles from cache");
                return _allMachineProfilesCache;
            }
        }

        List<MachineProfileDto> profiles = new List<MachineProfileDto>();

        try
        {
            _logger.LogInformation($"Loading OrcaSlicer machine profiles from bundles in: {_orcaProfilesPath}");

            if (!Directory.Exists(_orcaProfilesPath))
            {
                _logger.LogWarning($"OrcaSlicer profiles directory not found: {_orcaProfilesPath}");
                return profiles;
            }

            // Find all manufacturer bundle JSON files (e.g., Prusa.json, Voron.json, etc.)
            List<string> bundleFiles = Directory.GetFiles(_orcaProfilesPath, "*.json", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith('.')) // Skip hidden files
                .ToList();

            _logger.LogInformation($"Found {bundleFiles.Count} manufacturer bundle files");

            int successCount = 0;
            int failureCount = 0;

            foreach (string? bundleFile in bundleFiles)
            {
                try
                {
                    ManufacturerBundleDto? bundle = ParseManufacturerBundle(bundleFile);
                    if (bundle != null)
                    {
                        string manufacturerName = bundle.Name; // Extract from bundle

                        // Load from both machine_model_list and machine_list
                        List<ManufacturerBundleProfileEntry> allMachineEntries = new List<ManufacturerBundleProfileEntry>();
                        if (bundle.MachineModelList != null)
                        {
                            allMachineEntries.AddRange(bundle.MachineModelList);
                        }

                        if (bundle.MachineList != null)
                        {
                            allMachineEntries.AddRange(bundle.MachineList);
                        }

                        foreach (ManufacturerBundleProfileEntry entry in allMachineEntries)
                        {
                            try
                            {
                                string profilePath = Path.Combine(_orcaProfilesPath, bundle.Name, entry.SubPath);
                                if (!File.Exists(profilePath))
                                {
                                    _logger.LogWarning($"Machine profile referenced in bundle not found: {profilePath}");
                                    failureCount++;
                                    continue;
                                }

                                MachineProfileDto? profile = LoadProfileFromFile<MachineProfileDto>(profilePath);
                                if (profile != null)
                                {
                                    // Ensure manufacturer name is set from bundle
                                    profile.Manufacturer = manufacturerName;
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

            // Cache the results
            lock (_profilesCacheLock)
            {
                _allMachineProfilesCache = profiles;
            }

            return profiles;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error loading machine profiles: {ex.Message}");
            return profiles;
        }
    }
#pragma warning restore CS1998

    public async Task<IList<FilamentProfileDto>> ListAvailableFilamentProfilesAsync(CancellationToken ct = default)
    {
        // Return from cache if available
        lock (_profilesCacheLock)
        {
            if (_allFilamentProfilesCache != null)
            {
                _logger.LogInformation($"Returning {_allFilamentProfilesCache.Count} filament profiles from cache");
                return _allFilamentProfilesCache;
            }
        }

        List<FilamentProfileDto> profiles = new List<FilamentProfileDto>();

        try
        {
            _logger.LogInformation($"Loading OrcaSlicer filament profiles from bundles in: {_orcaProfilesPath}");

            if (!Directory.Exists(_orcaProfilesPath))
            {
                _logger.LogWarning($"OrcaSlicer profiles directory not found: {_orcaProfilesPath}");
                return profiles;
            }

            // Ensure machines are cached first so we can evaluate compatible_printers_condition
            await EnsureMachinesCachedAsync();

            List<string> bundleFiles = Directory.GetFiles(_orcaProfilesPath, "*.json", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
                .ToList();

            _logger.LogInformation($"Found {bundleFiles.Count} manufacturer bundle files");

            int successCount = 0;
            int failureCount = 0;

            foreach (string? bundleFile in bundleFiles)
            {
                try
                {
                    ManufacturerBundleDto? bundle = ParseManufacturerBundle(bundleFile);
                    if (bundle?.FilamentList != null)
                    {
                        // Get machines for this manufacturer to evaluate conditions
                        List<MachineProfileDto>? manufacturerMachines = GetCachedMachinesForManufacturer(bundle.Name);

                        foreach (ManufacturerBundleProfileEntry entry in bundle.FilamentList)
                        {
                            try
                            {
                                string profilePath = Path.Combine(_orcaProfilesPath, bundle.Name, entry.SubPath);
                                if (!File.Exists(profilePath))
                                {
                                    _logger.LogWarning($"Filament profile referenced in bundle not found: {profilePath}");
                                    failureCount++;
                                    continue;
                                }

                                FilamentProfileDto? profile = LoadProfileFromFile<FilamentProfileDto>(profilePath);
                                if (profile != null)
                                {
                                    profile.Manufacturer = bundle.Name;

                                    // If no explicit compatible_printers, try to evaluate the condition
                                    if ((profile.CompatiblePrinters == null || profile.CompatiblePrinters.Count == 0) &&
                                        !string.IsNullOrEmpty(profile.CompatiblePrintersCondition) &&
                                        manufacturerMachines?.Count > 0)
                                    {
                                        List<string>? matchedMachines = PrinterExpressionParser.EvaluateCondition(
                                            profile.CompatiblePrintersCondition,
                                            manufacturerMachines);
                                        if (matchedMachines?.Count > 0)
                                        {
                                            profile.CompatiblePrinters = matchedMachines;
                                        }
                                    }

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

            // Cache the results
            lock (_profilesCacheLock)
            {
                _allFilamentProfilesCache = profiles;
            }

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
        // Return from cache if available
        lock (_profilesCacheLock)
        {
            if (_allProcessProfilesCache != null)
            {
                _logger.LogInformation($"Returning {_allProcessProfilesCache.Count} process profiles from cache");
                return _allProcessProfilesCache;
            }
        }

        List<ProcessProfileDto> profiles = new List<ProcessProfileDto>();

        try
        {
            _logger.LogInformation($"Loading OrcaSlicer process profiles from bundles in: {_orcaProfilesPath}");

            if (!Directory.Exists(_orcaProfilesPath))
            {
                _logger.LogWarning($"OrcaSlicer profiles directory not found: {_orcaProfilesPath}");
                return profiles;
            }

            // Ensure machines are cached first so we can evaluate compatible_printers_condition
            await EnsureMachinesCachedAsync();

            List<string> bundleFiles = Directory.GetFiles(_orcaProfilesPath, "*.json", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
                .ToList();

            _logger.LogInformation($"Found {bundleFiles.Count} manufacturer bundle files");

            int successCount = 0;
            int failureCount = 0;

            foreach (string? bundleFile in bundleFiles)
            {
                try
                {
                    ManufacturerBundleDto? bundle = ParseManufacturerBundle(bundleFile);
                    if (bundle?.ProcessList != null)
                    {
                        // Get machines for this manufacturer to evaluate conditions
                        List<MachineProfileDto>? manufacturerMachines = GetCachedMachinesForManufacturer(bundle.Name);

                        foreach (ManufacturerBundleProfileEntry entry in bundle.ProcessList)
                        {
                            try
                            {
                                string profilePath = Path.Combine(_orcaProfilesPath, bundle.Name, entry.SubPath);
                                if (!File.Exists(profilePath))
                                {
                                    _logger.LogWarning($"Process profile referenced in bundle not found: {profilePath}");
                                    failureCount++;
                                    continue;
                                }

                                ProcessProfileDto? profile = LoadProfileFromFile<ProcessProfileDto>(profilePath);
                                if (profile != null)
                                {
                                    // If no explicit compatible_printers, try to evaluate the condition
                                    if ((profile.CompatiblePrinters == null || profile.CompatiblePrinters.Count == 0) &&
                                        !string.IsNullOrEmpty(profile.CompatiblePrintersCondition) &&
                                        manufacturerMachines?.Count > 0)
                                    {
                                        List<string>? matchedMachines = PrinterExpressionParser.EvaluateCondition(
                                            profile.CompatiblePrintersCondition,
                                            manufacturerMachines);
                                        if (matchedMachines?.Count > 0)
                                        {
                                            profile.CompatiblePrinters = matchedMachines;
                                        }
                                    }

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

            // Cache the results
            lock (_profilesCacheLock)
            {
                _allProcessProfilesCache = profiles;
            }

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
            string json = File.ReadAllText(bundleFilePath);
            JsonSerializerOptions options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
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
            // Build the fully resolved profile JSON by loading and merging the entire inheritance chain
            string? resolvedProfileJson = BuildResolvedProfileJson(filePath);
            if (resolvedProfileJson == null)
            {
                return null;
            }

            // Parse the resolved profile
            using JsonDocument doc = JsonDocument.Parse(resolvedProfileJson);
            JsonElement resolvedProfile = doc.RootElement;

            // Check instantiation AFTER resolving (in case it's inherited)
            if (resolvedProfile.TryGetProperty("instantiation", out JsonElement instantiationElem))
            {
                bool isInstantiatable = instantiationElem.ValueKind == JsonValueKind.True ||
                    (instantiationElem.ValueKind == JsonValueKind.String && instantiationElem.GetString() == "true");

                if (!isInstantiatable)
                {
                    return null;
                }
            }

            return typeof(T).Name switch
            {
                nameof(MachineProfileDto) => ParseMachineProfile(resolvedProfile, filePath) as T,
                nameof(FilamentProfileDto) => ParseFilamentProfile(resolvedProfile, filePath) as T,
                nameof(ProcessProfileDto) => ParseProcessProfile(resolvedProfile, filePath) as T,
                _ => null
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to load profile from {filePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Build a fully resolved profile by loading and merging all profiles in the inheritance chain.
    /// Returns the merged profile JSON string with all inherited properties + overrides from the current profile.
    /// </summary>
    private string? BuildResolvedProfileJson(string filePath)
    {
        try
        {
            // Collect all profiles in the inheritance chain (parent -> child order)
            List<string> inheritanceChain = new List<string>();
            HashSet<string> visited = new HashSet<string>();

            if (!CollectInheritanceChainAsJson(filePath, inheritanceChain, visited))
            {
                return null;
            }

            // Now merge all profiles in the chain
            return MergeProfilesJson(inheritanceChain);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to build resolved profile for {filePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Collect all profiles in the inheritance chain from top-level parent to the current profile.
    /// Returns false if profile can't be loaded.
    /// </summary>
    private bool CollectInheritanceChainAsJson(string filePath, List<string> chain, HashSet<string> visited)
    {
        // Prevent infinite loops
        if (visited.Contains(filePath))
        {
            return true;
        }

        _ = visited.Add(filePath);

        // Load this profile JSON (from cache or disk)
        string? profileJson = LoadProfileJsonFromDisk(filePath);
        if (profileJson == null)
        {
            return false;
        }

        // Parse to check for inherits property
        try
        {
            using JsonDocument doc = JsonDocument.Parse(profileJson);
            JsonElement root = doc.RootElement;

            // Check if this profile has a parent (inherits property)
            if (root.TryGetProperty("inherits", out JsonElement inheritsElem) &&
                inheritsElem.ValueKind == JsonValueKind.String)
            {
                string? inheritedProfileName = inheritsElem.GetString();
                if (!string.IsNullOrWhiteSpace(inheritedProfileName))
                {
                    // Find the parent profile in the same directory
                    string? profileDir = Path.GetDirectoryName(filePath);
                    string parentProfilePath = Path.Combine(profileDir ?? "", $"{inheritedProfileName}.json");

                    if (File.Exists(parentProfilePath))
                    {
                        // Recursively load parent chain first (so parents are added before children)
                        if (!CollectInheritanceChainAsJson(parentProfilePath, chain, visited))
                        {
                            _logger.LogWarning($"Failed to load parent profile '{inheritedProfileName}' for '{filePath}'");
                            // Don't fail - continue with what we have
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to parse profile {filePath}: {ex.Message}");
            return false;
        }

        // Add this profile JSON to the chain (after parents, so it can override)
        chain.Add(profileJson);
        return true;
    }

    /// <summary>
    /// Load a single profile JSON from disk or cache. Returns null if file doesn't exist or can't be read.
    /// </summary>
    private string? LoadProfileJsonFromDisk(string filePath)
    {
        string? cachedJson = null;

        lock (_cacheLock)
        {
            // Check cache first
            if (_profileJsonCache.TryGetValue(filePath, out string? cached))
            {
                cachedJson = cached;
            }
        }

        // Not in cache - load from disk
        if (cachedJson == null)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return null;
                }

                cachedJson = File.ReadAllText(filePath);

                // Store in cache
                lock (_cacheLock)
                {
                    _profileJsonCache[filePath] = cachedJson;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to load profile from disk {filePath}: {ex.Message}");
                return null;
            }
        }

        return cachedJson;
    }

    /// <summary>
    /// Merge a list of profile JSON strings (from parent to child order) into a single resolved profile JSON.
    /// Child profiles override parent properties.
    /// </summary>
    private string? MergeProfilesJson(List<string> profileJsons)
    {
        if (profileJsons.Count == 0)
        {
            return null;
        }

        if (profileJsons.Count == 1)
        {
            return profileJsons[0];
        }

        try
        {
            // Accumulate all properties from all profiles
            Dictionary<string, string> allProps = new Dictionary<string, string>();

            foreach (string profileJson in profileJsons)
            {
                using JsonDocument doc = JsonDocument.Parse(profileJson);
                JsonElement root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (JsonProperty prop in root.EnumerateObject())
                {
                    // Store all properties as raw JSON text (will override previous values)
                    allProps[prop.Name] = prop.Value.GetRawText();
                }
            }

            // Reconstruct as JSON string
            StringBuilder sb = new StringBuilder("{");
            bool first = true;
            foreach (KeyValuePair<string, string> kvp in allProps.OrderBy(x => x.Key)) // Order for consistency
            {
                if (!first)
                {
                    _ = sb.Append(',');
                }

                _ = sb.Append('"').Append(EscapeJsonKey(kvp.Key)).Append("\":");
                _ = sb.Append(kvp.Value);
                first = false;
            }
            _ = sb.Append('}');

            // Validate by parsing
            using JsonDocument validationDoc = JsonDocument.Parse(sb.ToString());
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to merge profile JSONs: {ex.Message}");
            return null;
        }
    }

    private static string EscapeJsonKey(string key)
    {
        return key.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private MachineProfileDto? ParseMachineProfile(JsonElement root, string filePath)
    {
        MachineProfileDto profile = new MachineProfileDto();

        if (root.TryGetProperty("name", out JsonElement nameElem))
        {
            profile.Name = nameElem.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("manufacturer", out JsonElement mfgElem))
        {
            profile.Manufacturer = mfgElem.GetString() ?? string.Empty;
        }

        // Extract nozzle diameter from settings - REQUIRED property
        // nozzle_diameter is typically an array like ["0.4"], get the first value
        if (root.TryGetProperty("nozzle_diameter", out JsonElement nozzleElem))
        {
            if (nozzleElem.ValueKind == JsonValueKind.Array)
            {
                JsonElement nozzleArray = nozzleElem.EnumerateArray().FirstOrDefault();
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

#pragma warning disable S1172 // Unused parameters are required by interface
    private FilamentProfileDto? ParseFilamentProfile(JsonElement root, string filePath)
    {
        FilamentProfileDto profile = new FilamentProfileDto();

        if (root.TryGetProperty("name", out JsonElement nameElem))
        {
            profile.Name = nameElem.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("filament_type", out JsonElement typeElem))
        {
            profile.Material = typeElem.GetString() ?? "PLA";
        }
        else if (root.TryGetProperty("material", out JsonElement matElem))
        {
            profile.Material = matElem.GetString() ?? "PLA";
        }

        if (root.TryGetProperty("nozzle_temperature", out JsonElement nozzleElem))
        {
            profile.NozzleTemperature = ParseIntValue(nozzleElem) ?? 210;
        }

        if (root.TryGetProperty("bed_temperature", out JsonElement bedElem))
        {
            profile.BedTemperature = ParseIntValue(bedElem) ?? 60;
        }

        if (root.TryGetProperty("travel_speed", out JsonElement speedElem))
        {
            profile.PrintSpeed = ParseIntValue(speedElem) ?? 50;
        }

        // Profile is now fully resolved - check for compatible_printers first
        if (root.TryGetProperty("compatible_printers", out JsonElement compatibleElem))
        {
            ParseCompatiblePrinters(compatibleElem, profile.CompatiblePrinters);
        }

        // Store compatible_printers_condition for later evaluation
        if (root.TryGetProperty("compatible_printers_condition", out JsonElement conditionElem))
        {
            string? condition = conditionElem.GetString();
            if (!string.IsNullOrEmpty(condition))
            {
                profile.CompatiblePrintersCondition = condition;
            }
        }

        // Store all settings as raw JSON for flexibility
        profile.Settings = SerializeElementToDict(root);

        return profile;
    }
#pragma warning restore S1172

#pragma warning disable S1172 // Unused parameters are required by interface
    private ProcessProfileDto? ParseProcessProfile(JsonElement root, string filePath)
    {
        ProcessProfileDto profile = new ProcessProfileDto();

        if (root.TryGetProperty("name", out JsonElement nameElem))
        {
            profile.Name = nameElem.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("layer_height", out JsonElement layerElem))
        {
            profile.LayerHeight = ParseDoubleValue(layerElem) ?? 0.2;
        }

        if (root.TryGetProperty("fill_density", out JsonElement infillElem))
        {
            profile.InfillPercentage = ParseIntValue(infillElem) ?? 20;
        }

        if (root.TryGetProperty("wall_loops", out JsonElement speedElem))
        {
            profile.PrintSpeed = ParseIntValue(speedElem) ?? 50;
        }

        if (root.TryGetProperty("enable_support", out JsonElement supportsElem))
        {
            profile.Supports = ParseBoolValue(supportsElem);
        }

        // Profile is now fully resolved - check for compatible_printers first
        if (root.TryGetProperty("compatible_printers", out JsonElement compatibleElem))
        {
            ParseCompatiblePrinters(compatibleElem, profile.CompatiblePrinters);
        }

        // Store compatible_printers_condition for later evaluation
        if (root.TryGetProperty("compatible_printers_condition", out JsonElement conditionElem))
        {
            string? condition = conditionElem.GetString();
            if (!string.IsNullOrEmpty(condition))
            {
                profile.CompatiblePrintersCondition = condition;
            }
        }

        // Determine quality based on layer height
        if (profile.LayerHeight <= 0.15)
        {
            profile.Quality = "fine";
        }
        else if (profile.LayerHeight >= 0.28)
        {
            profile.Quality = "draft";
        }
        else
        {
            profile.Quality = "standard";
        }

        // Store all settings as raw JSON for flexibility
        profile.Settings = SerializeElementToDict(root);

        return profile;
    }
#pragma warning restore S1172

    private static int? ParseIntValue(JsonElement elem)
    {
        if (elem.ValueKind == JsonValueKind.Number)
        {
            return elem.TryGetInt32(out int val) ? val : null;
        }
        else if (elem.ValueKind == JsonValueKind.String)
        {
            return int.TryParse(elem.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int val) ? val : null;
        }

        return null;
    }

    private static double? ParseDoubleValue(JsonElement elem)
    {
        if (elem.ValueKind == JsonValueKind.Number)
        {
            return elem.TryGetDouble(out double val) ? val : null;
        }
        else if (elem.ValueKind == JsonValueKind.String)
        {
            return double.TryParse(elem.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double val) ? val : null;
        }

        return null;
    }

    private static bool ParseBoolValue(JsonElement elem)
    {
        if (elem.ValueKind == JsonValueKind.True)
        {
            return true;
        }
        else if (elem.ValueKind == JsonValueKind.String)
        {
            string? val = elem.GetString();
            return string.Equals(val, "true", StringComparison.OrdinalIgnoreCase) || val == "1";
        }

        return false;
    }

    private static Dictionary<string, object> SerializeElementToDict(JsonElement elem)
    {
        Dictionary<string, object> dict = new Dictionary<string, object>();
        try
        {
            if (elem.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in elem.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.GetRawText();
                }
            }
        }
        catch (JsonException)
        {
            // If serialization fails, return empty dict
        }
        return dict;
    }

    private static void ParseCompatiblePrinters(JsonElement compatibleElem, IList<string> targetList)
    {
        if (compatibleElem.ValueKind == JsonValueKind.Array)
        {
            // Direct array format
            foreach (JsonElement printer in compatibleElem.EnumerateArray())
            {
                if (printer.ValueKind == JsonValueKind.String)
                {
                    string printerName = printer.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(printerName))
                    {
                        targetList.Add(printerName);
                    }
                }
            }
        }
        else if (compatibleElem.ValueKind == JsonValueKind.String)
        {
            // String format - need to parse as JSON array
            string? jsonString = compatibleElem.GetString();
            if (!string.IsNullOrWhiteSpace(jsonString))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(jsonString);
                    JsonElement root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in root.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                            {
                                string printerName = item.GetString() ?? "";
                                if (!string.IsNullOrWhiteSpace(printerName))
                                {
                                    targetList.Add(printerName);
                                }
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // If parsing fails, skip
                }
            }
        }
    }

    /// <summary>
    /// Gets available machines for expression evaluation with caching.
    /// Used by compatible_printers_condition expressions to match against machine properties.
    /// </summary>
    private async Task EnsureMachinesCachedAsync()
    {
        lock (_machineCacheLock)
        {
            if (_machinesByManufacturerCache != null)
            {
                return;
            }
        }

        // Load all machines and group by manufacturer
        IList<MachineProfileDto> allMachines = await ListAvailableMachineProfilesAsync();
        Dictionary<string, List<MachineProfileDto>> grouped = allMachines
            .GroupBy(m => m.Manufacturer ?? "Unknown")
            .ToDictionary(g => g.Key, g => g.ToList());

        lock (_machineCacheLock)
        {
            _machinesByManufacturerCache = grouped;
        }
    }

    private List<MachineProfileDto>? GetCachedMachinesForManufacturer(string manufacturerName)
    {
        lock (_machineCacheLock)
        {
            if (_machinesByManufacturerCache?.TryGetValue(manufacturerName, out List<MachineProfileDto>? machines) == true)
            {
                return machines;
            }
        }
        return null;
    }
}

