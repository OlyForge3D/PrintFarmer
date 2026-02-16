using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Telemetry;
using Farm.Slicer.Module.Dtos;
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

    // Cache for profile name → full path lookups, built from manufacturer manifests
    // Key: manufacturer name, Value: dictionary of profile name → full file path
    private readonly Dictionary<string, Dictionary<string, string>> _profilePathLookupCache = new();
    private readonly Lock _pathLookupCacheLock = new();

    // Cache for fully loaded profile lists to avoid reparsing on subsequent calls
    private List<MachineModelProfileDto>? _allMachineModelProfilesCache;
    private List<MachineProfileDto>? _allMachineProfilesCache;
    private List<FilamentProfileDto>? _allFilamentProfilesCache;
    private List<ProcessProfileDto>? _allProcessProfilesCache;
    private readonly Lock _profilesCacheLock = new();

    /// <summary>
    /// Creates an OrcaProfilesService with the default profile path (from ORCA_PROFILES_PATH env var or /opt/orcaslicer/resources/profiles).
    /// </summary>
    public OrcaProfilesService(IUnifiedLoggingService logger)
        : this(logger, null)
    {
    }

    /// <summary>
    /// Creates an OrcaProfilesService with an explicit profile path.
    /// </summary>
    /// <param name="logger">Logging service for diagnostics.</param>
    /// <param name="profilesPath">Custom path to profiles directory. If null, uses ORCA_PROFILES_PATH env var or default.</param>
    public OrcaProfilesService(IUnifiedLoggingService logger, string? profilesPath)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (!string.IsNullOrWhiteSpace(profilesPath) && Directory.Exists(profilesPath))
        {
            _orcaProfilesPath = profilesPath;
        }
        else
        {
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
    }

#pragma warning disable CS1998
    /// <summary>
    /// Lists machine model profiles from machine_model_list entries.
    /// These are base printer model templates like "Sovol SV08" that are NOT directly instantiatable.
    /// </summary>
    public async Task<IList<MachineModelProfileDto>> ListAvailableMachineModelProfilesAsync(CancellationToken ct = default)
    {
        // Return from cache if available
        lock (_profilesCacheLock)
        {
            if (_allMachineModelProfilesCache != null)
            {
                _logger.LogInformation($"Returning {_allMachineModelProfilesCache.Count} machine model profiles from cache");
                return _allMachineModelProfilesCache;
            }
        }

        List<MachineModelProfileDto> profiles = [];

        try
        {
            _logger.LogInformation($"Loading OrcaSlicer machine MODEL profiles (base templates) from bundles in: {_orcaProfilesPath}");

            if (!Directory.Exists(_orcaProfilesPath))
            {
                _logger.LogWarning($"OrcaSlicer profiles directory not found: {_orcaProfilesPath}");
                return profiles;
            }

            // Find all manufacturer bundle JSON files (e.g., Prusa.json, Voron.json, etc.)
            List<string> bundleFiles = Directory.GetFiles(_orcaProfilesPath, "*.json", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith('.')) // Skip hidden files
                .ToList();

            _logger.LogInformation($"Found {bundleFiles.Count} manufacturer bundle files for machine model profiles");

            int successCount = 0;
            int failureCount = 0;

            foreach (string? bundleFile in bundleFiles)
            {
                try
                {
                    ManufacturerBundleDto? bundle = ParseManufacturerBundle(bundleFile);
                    if (bundle != null)
                    {
                        // Use folder name from JSON filename (e.g., "Ratrig" from "Ratrig.json")
                        // NOT bundle.Name which may differ in case or be completely different
                        // (e.g., Eryone.json has name="Thinker X400" but manufacturer should be "Eryone")
                        string folderName = Path.GetFileNameWithoutExtension(bundleFile);
                        string manufacturerName = folderName;

                        // ONLY load from machine_model_list - these are base printer models
                        if (bundle.MachineModelList != null)
                        {
                            foreach (ManufacturerBundleProfileEntry entry in bundle.MachineModelList)
                            {
                                try
                                {
                                    string profilePath = Path.Combine(_orcaProfilesPath, folderName, entry.SubPath);
                                    if (!File.Exists(profilePath))
                                    {
                                        _logger.LogWarning($"Machine model profile referenced in bundle not found: {profilePath}");
                                        failureCount++;
                                        continue;
                                    }

                                    MachineModelProfileDto? profile = LoadProfileFromFile<MachineModelProfileDto>(profilePath);
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
                                    _logger.LogWarning($"Failed to load machine model profile '{entry.Name}' from bundle '{bundle.Name}': {ex.Message}");
                                    failureCount++;
                                }
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

            _logger.LogInformation($"Loaded {successCount} machine MODEL profiles ({failureCount} failures from {bundleFiles.Count} bundles)");

            // Cache the results
            lock (_profilesCacheLock)
            {
                _allMachineModelProfilesCache = profiles;
            }

            return profiles;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error loading machine model profiles: {ex.Message}");
            return profiles;
        }
    }

    /// <summary>
    /// Lists machine profiles from machine_list entries.
    /// These are the actual selectable profiles with nozzle sizes like "Sovol SV08 0.4 nozzle".
    /// </summary>
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

        List<MachineProfileDto> profiles = [];

        try
        {
            _logger.LogInformation($"Loading OrcaSlicer machine profiles (nozzle variants) from bundles in: {_orcaProfilesPath}");

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
                        // Use folder name from JSON filename (e.g., "Ratrig" from "Ratrig.json")
                        // NOT bundle.Name which may differ in case or be completely different
                        // (e.g., Eryone.json has name="Thinker X400" but manufacturer should be "Eryone")
                        string folderName = Path.GetFileNameWithoutExtension(bundleFile);
                        string manufacturerName = folderName;

                        // ONLY load from machine_list - these are actual selectable profiles with nozzle sizes
                        if (bundle.MachineList != null)
                        {
                            foreach (ManufacturerBundleProfileEntry entry in bundle.MachineList)
                            {
                                try
                                {
                                    string profilePath = Path.Combine(_orcaProfilesPath, folderName, entry.SubPath);
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

        List<FilamentProfileDto> profiles = [];

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
                    // Use folder name from JSON filename (e.g., "Ratrig" from "Ratrig.json")
                    // NOT bundle.Name which may differ in case or be completely different
                    string folderName = Path.GetFileNameWithoutExtension(bundleFile);
                    ManufacturerBundleDto? bundle = ParseManufacturerBundle(bundleFile);
                    if (bundle?.FilamentList != null)
                    {
                        // Get machines for this manufacturer to evaluate conditions
                        // Use folderName for consistency - bundle.Name may differ (e.g., Eryone.json has name="Thinker X400")
                        List<MachineProfileDto>? manufacturerMachines = GetCachedMachinesForManufacturer(folderName);

                        foreach (ManufacturerBundleProfileEntry entry in bundle.FilamentList)
                        {
                            try
                            {
                                string profilePath = Path.Combine(_orcaProfilesPath, folderName, entry.SubPath);
                                if (!File.Exists(profilePath))
                                {
                                    _logger.LogWarning($"Filament profile referenced in bundle not found: {profilePath}");
                                    failureCount++;
                                    continue;
                                }

                                FilamentProfileDto? profile = LoadProfileFromFile<FilamentProfileDto>(profilePath);
                                if (profile != null)
                                {
                                    profile.Manufacturer = folderName;

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

        List<ProcessProfileDto> profiles = [];

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
                    // Use folder name from JSON filename (e.g., "Ratrig" from "Ratrig.json")
                    // NOT bundle.Name which may differ in case or be completely different
                    string folderName = Path.GetFileNameWithoutExtension(bundleFile);
                    ManufacturerBundleDto? bundle = ParseManufacturerBundle(bundleFile);
                    if (bundle?.ProcessList != null)
                    {
                        // Get machines for this manufacturer to evaluate conditions
                        // Use folderName for consistency - bundle.Name may differ (e.g., Eryone.json has name="Thinker X400")
                        List<MachineProfileDto>? manufacturerMachines = GetCachedMachinesForManufacturer(folderName);

                        foreach (ManufacturerBundleProfileEntry entry in bundle.ProcessList)
                        {
                            try
                            {
                                string profilePath = Path.Combine(_orcaProfilesPath, folderName, entry.SubPath);
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

    private T? LoadProfileFromFile<T>(string filePath)
        where T : class, new()
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
            List<string> inheritanceChain = [];
            HashSet<string> visited = [];

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
                    // Search for parent profile - OrcaSlicer stores profiles in nested subdirectories
                    // (e.g., filament/P1P/child.json) but base profiles are in parent folder (filament/base.json)
                    string? parentProfilePath = FindParentProfile(filePath, inheritedProfileName);

                    if (parentProfilePath != null && File.Exists(parentProfilePath))
                    {
                        _logger.LogInformation($"Resolving inheritance: '{inheritedProfileName}' → '{parentProfilePath}'");

                        // Recursively load parent chain first (so parents are added before children)
                        if (!CollectInheritanceChainAsJson(parentProfilePath, chain, visited))
                        {
                            _logger.LogWarning($"Failed to load parent profile '{inheritedProfileName}' for '{filePath}'");

                            // Don't fail - continue with what we have
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"Parent profile '{inheritedProfileName}' not found for '{filePath}' (resolved to: {parentProfilePath ?? "null"})");
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
            Dictionary<string, string> allProps = [];

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

            // Order for consistency
            foreach (KeyValuePair<string, string> kvp in allProps.OrderBy(x => x.Key))
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

    /// <summary>
    /// Find a parent profile by looking up its name in the manufacturer's manifest file.
    /// The {Manufacturer}.json file contains name → sub_path mappings for all profiles.
    /// </summary>
    private string? FindParentProfile(string childFilePath, string parentProfileName)
    {
        // Extract manufacturer from path: /profiles/{Manufacturer}/filament/child.json → {Manufacturer}
        string? manufacturerDir = GetManufacturerDirectory(childFilePath);
        if (manufacturerDir == null)
        {
            _logger.LogDebug($"Could not determine manufacturer directory for '{childFilePath}'");
            return null;
        }

        string manufacturerName = Path.GetFileName(manufacturerDir);

        // Build or retrieve the profile path lookup for this manufacturer
        Dictionary<string, string>? lookup = GetOrBuildProfilePathLookup(manufacturerName, manufacturerDir);
        if (lookup == null)
        {
            _logger.LogDebug($"No manifest found for manufacturer '{manufacturerName}'");
            return null;
        }

        // Look up the parent profile name in the manifest
        if (lookup.TryGetValue(parentProfileName, out string? parentPath))
        {
            return parentPath;
        }

        // Not found in manifest - this is expected for some base profiles that may be shared
        _logger.LogDebug($"Parent profile '{parentProfileName}' not found in {manufacturerName} manifest");
        return null;
    }

    /// <summary>
    /// Extract the manufacturer directory from a profile file path.
    /// Example: /profiles/BBL/filament/P1P/child.json → /profiles/BBL
    /// </summary>
    private string? GetManufacturerDirectory(string filePath)
    {
        // Walk up from the file to find the manufacturer directory (direct child of _orcaProfilesPath)
        string? currentDir = Path.GetDirectoryName(filePath);

        while (!string.IsNullOrEmpty(currentDir))
        {
            string? parentDir = Path.GetDirectoryName(currentDir);
            if (parentDir != null && Path.GetFullPath(parentDir) == Path.GetFullPath(_orcaProfilesPath))
            {
                // currentDir is a direct child of profiles root - this is the manufacturer directory
                return currentDir;
            }

            if (parentDir == currentDir)
            {
                break; // Reached root
            }

            currentDir = parentDir;
        }

        return null;
    }

    /// <summary>
    /// Build or retrieve the profile name → full path lookup dictionary for a manufacturer.
    /// Parses the {Manufacturer}.json manifest and builds mappings from all profile lists.
    /// </summary>
    private Dictionary<string, string>? GetOrBuildProfilePathLookup(string manufacturerName, string manufacturerDir)
    {
        lock (_pathLookupCacheLock)
        {
            if (_profilePathLookupCache.TryGetValue(manufacturerName, out Dictionary<string, string>? cached))
            {
                return cached;
            }
        }

        // Build the lookup from the manifest file
        string manifestPath = Path.Combine(_orcaProfilesPath, $"{manufacturerName}.json");
        if (!File.Exists(manifestPath))
        {
            _logger.LogDebug($"Manifest file not found: {manifestPath}");
            return null;
        }

        ManufacturerBundleDto? bundle = ParseManufacturerBundle(manifestPath);
        if (bundle == null)
        {
            return null;
        }

        Dictionary<string, string> lookup = new(StringComparer.Ordinal);

        // Add all profile types to the lookup
        AddProfileEntriesToLookup(bundle.MachineModelList, manufacturerDir, lookup);
        AddProfileEntriesToLookup(bundle.MachineList, manufacturerDir, lookup);
        AddProfileEntriesToLookup(bundle.FilamentList, manufacturerDir, lookup);
        AddProfileEntriesToLookup(bundle.ProcessList, manufacturerDir, lookup);

        lock (_pathLookupCacheLock)
        {
            _profilePathLookupCache[manufacturerName] = lookup;
        }

        _logger.LogDebug($"Built profile path lookup for {manufacturerName}: {lookup.Count} entries");
        return lookup;
    }

    /// <summary>
    /// Add profile entries from a manifest list to the lookup dictionary.
    /// </summary>
    private static void AddProfileEntriesToLookup(
        IList<ManufacturerBundleProfileEntry> entries,
        string manufacturerDir,
        Dictionary<string, string> lookup)
    {
        foreach (ManufacturerBundleProfileEntry entry in entries)
        {
            if (!string.IsNullOrEmpty(entry.Name) && !string.IsNullOrEmpty(entry.SubPath))
            {
                // sub_path is relative to the manufacturer directory
                string fullPath = Path.Combine(manufacturerDir, entry.SubPath);
                lookup[entry.Name] = fullPath;
            }
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

        // Extract printer_model - base model name used for catalog alias lookup
        if (root.TryGetProperty("printer_model", out JsonElement printerModelElem))
        {
            profile.PrinterModel = printerModelElem.GetString();
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

        // Extract material type from multiple sources:
        // 1. filament_type property (direct)
        // 2. material property (direct)
        // 3. inherits field (e.g., "fdm_filament_abs" -> "ABS")
        // 4. Profile name parsing (e.g., "Generic ABS @System" -> "ABS")
        if (root.TryGetProperty("filament_type", out JsonElement typeElem))
        {
            profile.Material = ParseStringValue(typeElem) ?? "PLA";
        }
        else if (root.TryGetProperty("material", out JsonElement matElem))
        {
            profile.Material = ParseStringValue(matElem) ?? "PLA";
        }
        else if (root.TryGetProperty("inherits", out JsonElement inheritsElem))
        {
            // Parse material from inherits like "fdm_filament_abs", "fdm_filament_petg", etc.
            string? inherits = ParseStringValue(inheritsElem);
            profile.Material = ExtractMaterialFromInherits(inherits) ?? ExtractMaterialFromName(profile.Name) ?? "Other";
        }
        else
        {
            // Try to extract from profile name
            profile.Material = ExtractMaterialFromName(profile.Name) ?? "Other";
        }

        if (root.TryGetProperty("nozzle_temperature", out JsonElement nozzleElem))
        {
            profile.NozzleTemperature = ParseIntValue(nozzleElem) ?? 210;
        }

        // OrcaSlicer uses hot_plate_temp for bed temperature, fall back to bed_temperature
        if (root.TryGetProperty("hot_plate_temp", out JsonElement hotPlateElem))
        {
            profile.BedTemperature = ParseIntValue(hotPlateElem) ?? 60;
        }
        else if (root.TryGetProperty("bed_temperature", out JsonElement bedElem))
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
            string? condition = ParseStringValue(conditionElem);
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
            string? condition = ParseStringValue(conditionElem);
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
        else if (elem.ValueKind == JsonValueKind.Array && elem.GetArrayLength() > 0)
        {
            // OrcaSlicer stores many values as single-element arrays like ["260"]
            JsonElement firstElem = elem[0];
            return ParseIntValue(firstElem);
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
        else if (elem.ValueKind == JsonValueKind.Array && elem.GetArrayLength() > 0)
        {
            // OrcaSlicer stores many values as single-element arrays like ["0.2"]
            JsonElement firstElem = elem[0];
            return ParseDoubleValue(firstElem);
        }

        return null;
    }

    /// <summary>
    /// Safely parse a string value from a JsonElement that could be a string or array.
    /// OrcaSlicer stores many values as single-element arrays like ["PLA"].
    /// </summary>
    private static string? ParseStringValue(JsonElement elem)
    {
        if (elem.ValueKind == JsonValueKind.String)
        {
            return elem.GetString();
        }
        else if (elem.ValueKind == JsonValueKind.Array && elem.GetArrayLength() > 0)
        {
            JsonElement firstElem = elem[0];
            return ParseStringValue(firstElem);
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
        Dictionary<string, object> dict = [];
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
                    string printerName = printer.GetString() ?? string.Empty;
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
                                string printerName = item.GetString() ?? string.Empty;
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

    /// <summary>
    /// Extracts material type from inherits field like "fdm_filament_abs" -> "ABS".
    /// </summary>
    private static string? ExtractMaterialFromInherits(string? inherits)
    {
        if (string.IsNullOrWhiteSpace(inherits))
        {
            return null;
        }

        // Common patterns: fdm_filament_abs, fdm_filament_petg, filament_pla, etc.
        string lower = inherits.ToLowerInvariant();

        // Map of known suffixes to material names
        Dictionary<string, string> materialMap = new()
        {
            { "abs", "ABS" },
            { "asa", "ASA" },
            { "pla", "PLA" },
            { "petg", "PETG" },
            { "pet", "PET" },
            { "tpu", "TPU" },
            { "tpe", "TPE" },
            { "flex", "FLEX" },
            { "pa", "PA" },
            { "nylon", "PA" },
            { "pc", "PC" },
            { "pctg", "PCTG" },
            { "pva", "PVA" },
            { "bvoh", "BVOH" },
            { "hips", "HIPS" },
            { "pp", "PP" },
            { "cpe", "CPE" },
            { "peba", "PEBA" },
            { "pvb", "PVB" },
            { "pha", "PHA" },
            { "cf", "CF" },  // Carbon Fiber
            { "gf", "GF" },  // Glass Fiber
            { "wood", "Wood" },
            { "metal", "Metal" },
            { "silk", "Silk" },
            { "marble", "Marble" },
            { "eva", "EVA" },
        };

        foreach (KeyValuePair<string, string> kvp in materialMap)
        {
            // Check if the inherits field ends with or contains the material
            if (lower.EndsWith("_" + kvp.Key) || lower.EndsWith(kvp.Key) || lower.Contains("_" + kvp.Key + "_"))
            {
                return kvp.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts material type from profile name like "Generic ABS @System" -> "ABS".
    /// </summary>
    private static string? ExtractMaterialFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // Common material names to look for in the profile name
        // Ordered by specificity - composite materials first, then base materials
        string[] materials = [

            // Composite materials (check these first - they contain base material names)
            "ABS-GF", "ABS-CF", "ASA-GF", "ASA-CF", "ASA-Aero",
            "PA12-CF", "PA6-CF", "PA6-GF", "PA-CF", "PA-GF",
            "PAHT-CF", "PAHT-GF", "PPA-CF", "PPA-GF",
            "PETG-CF", "PETG-GF", "PET-CF",
            "PC-CF", "PC-GF", "PC-ABS",
            "PE-CF", "PP-CF", "PP-GF",
            "PPS-CF", "PPS-GF", "PPS",

            // Base materials
            "ABS", "ASA", "PLA", "PETG", "PET", "TPU", "TPE", "FLEX",
            "PA", "Nylon", "PC", "PCTG", "PVA", "BVOH", "HIPS", "PP", "CPE", "PEBA",
            "PVB", "PHA", "Wood", "Metal", "Silk", "Marble", "EVA"
        ];

        string upper = name.ToUpperInvariant();

        foreach (string material in materials)
        {
            string upperMat = material.ToUpperInvariant();

            // Check for word boundary matches
            if (upper.Contains(" " + upperMat + " ") ||
                upper.Contains(" " + upperMat + "@") ||
                upper.Contains(" " + upperMat + "-") ||
                upper.StartsWith(upperMat + " ") ||
                upper.StartsWith("GENERIC " + upperMat) ||
                upper.Contains("/" + upperMat) ||
                upper.Contains(upperMat + "/"))
            {
                return material;
            }
        }

        return null;
    }
}
