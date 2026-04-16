using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Worker.Core;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger _logger;
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

    // Cache for filesystem-based profile lookups (fallback when manifest doesn't contain the parent)
    // Key: manufacturer directory path, Value: dictionary of profile name → full file path
    private readonly Dictionary<string, Dictionary<string, string>> _filesystemLookupCache = new();
    private readonly Lock _filesystemLookupCacheLock = new();

    // Cache for fully loaded profile lists to avoid reparsing on subsequent calls
    private List<MachineModelProfileDto>? _allMachineModelProfilesCache;
    private List<MachineProfileDto>? _allMachineProfilesCache;
    private List<FilamentProfileDto>? _allFilamentProfilesCache;
    private List<ProcessProfileDto>? _allProcessProfilesCache;
    private readonly Lock _profilesCacheLock = new();

    /// <summary>
    /// Creates an OrcaProfilesService with the default profile path (from ORCA_PROFILES_PATH env var or /opt/orcaslicer/resources/profiles).
    /// </summary>
    public OrcaProfilesService(ILogger logger)
        : this(logger, null)
    {
    }

    /// <summary>
    /// Creates an OrcaProfilesService with an explicit profile path.
    /// </summary>
    /// <param name="logger">Logging service for diagnostics.</param>
    /// <param name="profilesPath">Custom path to profiles directory. If null, uses ORCA_PROFILES_PATH env var or default.</param>
    public OrcaProfilesService(ILogger logger, string? profilesPath)
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
                _logger.LogInformation("Returning {Count} machine model profiles from cache", _allMachineModelProfilesCache.Count);
                return _allMachineModelProfilesCache;
            }
        }

        List<MachineModelProfileDto> profiles = [];

        try
        {
            _logger.LogInformation("Loading OrcaSlicer machine MODEL profiles (base templates) from bundles in: {OrcaProfilesPath}", _orcaProfilesPath);

            if (!Directory.Exists(_orcaProfilesPath))
            {
                _logger.LogWarning("OrcaSlicer profiles directory not found: {OrcaProfilesPath}", _orcaProfilesPath);
                return profiles;
            }

            // Find all manufacturer bundle JSON files (e.g., Prusa.json, Voron.json, etc.)
            List<string> bundleFiles = Directory.GetFiles(_orcaProfilesPath, "*.json", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith('.')) // Skip hidden files
                .ToList();

            _logger.LogInformation("Found {BundleFilesCount} manufacturer bundle files for machine model profiles", bundleFiles.Count);

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
                                        _logger.LogWarning("Machine model profile referenced in bundle not found: {ProfilePath}", profilePath);
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
                                    _logger.LogWarning("Failed to load machine model profile '{EntryName}' from bundle '{BundleName}': {Message}", entry.Name, bundle.Name, ex.Message);
                                    failureCount++;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to parse manufacturer bundle '{PathGetFileName}': {Message}", Path.GetFileName(bundleFile), ex.Message);
                    failureCount++;
                }
            }

            _logger.LogInformation("Loaded {SuccessCount} machine MODEL profiles ({FailureCount} failures from {BundleFilesCount} bundles)", successCount, failureCount, bundleFiles.Count);

            // Cache the results
            lock (_profilesCacheLock)
            {
                _allMachineModelProfilesCache = profiles;
            }

            return profiles;
        }
        catch (Exception ex)
        {
            _logger.LogError("Error loading machine model profiles: {Message}", ex.Message);
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
                _logger.LogInformation("Returning {Count} machine profiles from cache", _allMachineProfilesCache.Count);
                return _allMachineProfilesCache;
            }
        }

        List<MachineProfileDto> profiles = [];

        try
        {
            _logger.LogInformation("Loading OrcaSlicer machine profiles (nozzle variants) from bundles in: {OrcaProfilesPath}", _orcaProfilesPath);

            if (!Directory.Exists(_orcaProfilesPath))
            {
                _logger.LogWarning("OrcaSlicer profiles directory not found: {OrcaProfilesPath}", _orcaProfilesPath);
                return profiles;
            }

            // Find all manufacturer bundle JSON files (e.g., Prusa.json, Voron.json, etc.)
            List<string> bundleFiles = Directory.GetFiles(_orcaProfilesPath, "*.json", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith('.')) // Skip hidden files
                .ToList();

            _logger.LogInformation("Found {BundleFilesCount} manufacturer bundle files", bundleFiles.Count);

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
                                        _logger.LogWarning("Machine profile referenced in bundle not found: {ProfilePath}", profilePath);
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
                                    _logger.LogWarning("Failed to load machine profile '{EntryName}' from bundle '{BundleName}': {Message}", entry.Name, bundle.Name, ex.Message);
                                    failureCount++;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to parse manufacturer bundle '{PathGetFileName}': {Message}", Path.GetFileName(bundleFile), ex.Message);
                    failureCount++;
                }
            }

            _logger.LogInformation("Loaded {SuccessCount} machine profiles ({FailureCount} failures from {BundleFilesCount} bundles)", successCount, failureCount, bundleFiles.Count);

            // Cache the results
            lock (_profilesCacheLock)
            {
                _allMachineProfilesCache = profiles;
            }

            return profiles;
        }
        catch (Exception ex)
        {
            _logger.LogError("Error loading machine profiles: {Message}", ex.Message);
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
                _logger.LogInformation("Returning {Count} filament profiles from cache", _allFilamentProfilesCache.Count);
                return _allFilamentProfilesCache;
            }
        }

        List<FilamentProfileDto> profiles = [];

        try
        {
            _logger.LogInformation("Loading OrcaSlicer filament profiles from bundles in: {OrcaProfilesPath}", _orcaProfilesPath);

            if (!Directory.Exists(_orcaProfilesPath))
            {
                _logger.LogWarning("OrcaSlicer profiles directory not found: {OrcaProfilesPath}", _orcaProfilesPath);
                return profiles;
            }

            // Ensure machines are cached first so we can evaluate compatible_printers_condition
            await EnsureMachinesCachedAsync();

            List<string> bundleFiles = Directory.GetFiles(_orcaProfilesPath, "*.json", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
                .ToList();

            _logger.LogInformation("Found {BundleFilesCount} manufacturer bundle files", bundleFiles.Count);

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
                                    _logger.LogWarning("Filament profile referenced in bundle not found: {ProfilePath}", profilePath);
                                    failureCount++;
                                    continue;
                                }

                                FilamentProfileDto? profile = LoadProfileFromFile<FilamentProfileDto>(profilePath);
                                if (profile != null)
                                {
                                    // Use folder name only if manufacturer wasn't extracted from filament_vendor
                                    if (string.IsNullOrEmpty(profile.Manufacturer))
                                    {
                                        profile.Manufacturer = folderName;
                                    }

                                    // Normalize vendor names to match OrcaSlicer UI conventions
                                    profile.Manufacturer = NormalizeFilamentVendor(profile.Manufacturer);

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
                                _logger.LogWarning("Failed to load filament profile '{EntryName}' from bundle '{BundleName}': {Message}", entry.Name, bundle.Name, ex.Message);
                                failureCount++;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to parse manufacturer bundle '{PathGetFileName}': {Message}", Path.GetFileName(bundleFile), ex.Message);
                    failureCount++;
                }
            }

            _logger.LogInformation("Loaded {SuccessCount} filament profiles ({FailureCount} failures from {BundleFilesCount} bundles)", successCount, failureCount, bundleFiles.Count);

            // Cache the results
            lock (_profilesCacheLock)
            {
                _allFilamentProfilesCache = profiles;
            }

            return profiles;
        }
        catch (Exception ex)
        {
            _logger.LogError("Error loading filament profiles: {Message}", ex.Message);
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
                _logger.LogInformation("Returning {Count} process profiles from cache", _allProcessProfilesCache.Count);
                return _allProcessProfilesCache;
            }
        }

        List<ProcessProfileDto> profiles = [];

        try
        {
            _logger.LogInformation("Loading OrcaSlicer process profiles from bundles in: {OrcaProfilesPath}", _orcaProfilesPath);

            if (!Directory.Exists(_orcaProfilesPath))
            {
                _logger.LogWarning("OrcaSlicer profiles directory not found: {OrcaProfilesPath}", _orcaProfilesPath);
                return profiles;
            }

            // Ensure machines are cached first so we can evaluate compatible_printers_condition
            await EnsureMachinesCachedAsync();

            List<string> bundleFiles = Directory.GetFiles(_orcaProfilesPath, "*.json", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
                .ToList();

            _logger.LogInformation("Found {BundleFilesCount} manufacturer bundle files", bundleFiles.Count);

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
                                    _logger.LogWarning("Process profile referenced in bundle not found: {ProfilePath}", profilePath);
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
                                _logger.LogWarning("Failed to load process profile '{EntryName}' from bundle '{BundleName}': {Message}", entry.Name, bundle.Name, ex.Message);
                                failureCount++;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to parse manufacturer bundle '{PathGetFileName}': {Message}", Path.GetFileName(bundleFile), ex.Message);
                    failureCount++;
                }
            }

            _logger.LogInformation("Loaded {SuccessCount} process profiles ({FailureCount} failures from {BundleFilesCount} bundles)", successCount, failureCount, bundleFiles.Count);

            // Cache the results
            lock (_profilesCacheLock)
            {
                _allProcessProfilesCache = profiles;
            }

            return profiles;
        }
        catch (Exception ex)
        {
            _logger.LogError("Error loading process profiles: {Message}", ex.Message);
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
            _logger.LogWarning("Failed to parse manufacturer bundle {PathGetFileName}: {Message}", Path.GetFileName(bundleFilePath), ex.Message);
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
            _logger.LogWarning("Failed to load profile from {FilePath}: {Message}", filePath, ex.Message);
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
            _logger.LogWarning("Failed to build resolved profile for {FilePath}: {Message}", filePath, ex.Message);
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
                        _logger.LogInformation("Resolving inheritance: '{InheritedProfileName}' → '{ParentProfilePath}'", inheritedProfileName, parentProfilePath);

                        // Recursively load parent chain first (so parents are added before children)
                        if (!CollectInheritanceChainAsJson(parentProfilePath, chain, visited))
                        {
                            _logger.LogWarning("Failed to load parent profile '{InheritedProfileName}' for '{FilePath}'", inheritedProfileName, filePath);

                            // Don't fail - continue with what we have
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Parent profile '{InheritedProfileName}' not found for '{FilePath}' (resolved to: {ParentProfilePath})", inheritedProfileName, filePath, parentProfilePath ?? "null");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to parse profile {FilePath}: {Message}", filePath, ex.Message);
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
                _logger.LogWarning("Failed to load profile from disk {FilePath}: {Message}", filePath, ex.Message);
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
            _logger.LogWarning("Failed to merge profile JSONs: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Find a parent profile by looking up its name in the manufacturer's manifest file.
    /// Falls back to filesystem scan within the manufacturer directory, then across all manufacturers.
    /// </summary>
    private string? FindParentProfile(string childFilePath, string parentProfileName)
    {
        // Extract manufacturer from path: /profiles/{Manufacturer}/filament/child.json → {Manufacturer}
        string? manufacturerDir = GetManufacturerDirectory(childFilePath);
        if (manufacturerDir == null)
        {
            _logger.LogDebug("Could not determine manufacturer directory for '{ChildFilePath}'", childFilePath);
            return null;
        }

        string manufacturerName = Path.GetFileName(manufacturerDir);

        // 1. Look up in the manufacturer's manifest (fast path - most profiles are here)
        Dictionary<string, string>? lookup = GetOrBuildProfilePathLookup(manufacturerName, manufacturerDir);
        if (lookup != null && lookup.TryGetValue(parentProfileName, out string? parentPath))
        {
            return parentPath;
        }

        // 2. Fallback: scan the manufacturer's directory recursively for a matching .json file
        //    Handles profiles on disk but not listed in the manifest
        Dictionary<string, string> fsLookup = GetOrBuildFilesystemLookup(manufacturerDir);
        if (fsLookup.TryGetValue(parentProfileName, out string? fsPath))
        {
            _logger.LogDebug("Found parent profile '{ParentProfileName}' via filesystem scan in {ManufacturerName}", parentProfileName, manufacturerName);
            return fsPath;
        }

        // 3. Fallback: search across ALL manufacturer directories
        //    Handles cross-manufacturer inheritance (e.g., Elegoo inheriting fdm_filament_tpu from BBL)
        string? crossMfgPath = FindParentProfileAcrossManufacturers(parentProfileName, manufacturerDir);
        if (crossMfgPath != null)
        {
            _logger.LogDebug("Found parent profile '{ParentProfileName}' in different manufacturer directory: {Path}", parentProfileName, crossMfgPath);
            return crossMfgPath;
        }

        _logger.LogDebug("Parent profile '{ParentProfileName}' not found in {ManufacturerName} or any other manufacturer", parentProfileName, manufacturerName);
        return null;
    }

    /// <summary>
    /// Build or retrieve a filesystem-based lookup for a manufacturer directory.
    /// Scans all .json files recursively and maps filename (without extension) → full path.
    /// </summary>
    private Dictionary<string, string> GetOrBuildFilesystemLookup(string manufacturerDir)
    {
        lock (_filesystemLookupCacheLock)
        {
            if (_filesystemLookupCache.TryGetValue(manufacturerDir, out Dictionary<string, string>? cached))
            {
                return cached;
            }
        }

        Dictionary<string, string> lookup = new(StringComparer.Ordinal);

        try
        {
            foreach (string filePath in Directory.EnumerateFiles(manufacturerDir, "*.json", SearchOption.AllDirectories))
            {
                string profileName = Path.GetFileNameWithoutExtension(filePath);

                // First occurrence wins (avoids overwriting with duplicates)
                lookup.TryAdd(profileName, filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to scan manufacturer directory {Dir}: {Message}", manufacturerDir, ex.Message);
        }

        lock (_filesystemLookupCacheLock)
        {
            _filesystemLookupCache[manufacturerDir] = lookup;
        }

        return lookup;
    }

    /// <summary>
    /// Search across all manufacturer directories for a parent profile by filename.
    /// Skips the specified manufacturer directory (already searched).
    /// </summary>
    private string? FindParentProfileAcrossManufacturers(string parentProfileName, string excludeManufacturerDir)
    {
        try
        {
            foreach (string mfgDir in Directory.EnumerateDirectories(_orcaProfilesPath))
            {
                if (string.Equals(Path.GetFullPath(mfgDir), Path.GetFullPath(excludeManufacturerDir), StringComparison.Ordinal))
                {
                    continue;
                }

                Dictionary<string, string> fsLookup = GetOrBuildFilesystemLookup(mfgDir);
                if (fsLookup.TryGetValue(parentProfileName, out string? path))
                {
                    return path;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to search across manufacturer directories: {Message}", ex.Message);
        }

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
            _logger.LogDebug("Manifest file not found: {ManifestPath}", manifestPath);
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

        _logger.LogDebug("Built profile path lookup for {ManufacturerName}: {LookupCount} entries", manufacturerName, lookup.Count);
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

        if (root.TryGetProperty("printer_variant", out JsonElement variantElem))
        {
            profile.PrinterVariant = ParseStringValue(variantElem);
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
            _logger.LogWarning("Machine profile '{ProfileName}' missing required 'nozzle_diameter' property in {FilePath}", profile.Name, filePath);
            return null; // Reject profiles without nozzle_diameter
        }

        // ── Nozzle type ────────────────────────────────────────────────────
        if (root.TryGetProperty("nozzle_type", out JsonElement nozzleTypeElem))
        {
            profile.NozzleType = ParseStringValue(nozzleTypeElem);
        }

        // ── Build volume ───────────────────────────────────────────────────
        ExtractBuildVolume(root, profile);

        // ── Capabilities ───────────────────────────────────────────────────
        profile.MaxPrintSpeed = ParseOptionalInt(root, "machine_max_speed_z")
            ?? ParseOptionalInt(root, "max_print_speed");

        if (root.TryGetProperty("printer_type", out JsonElement typeElem))
        {
            profile.MotionType = ParseStringValue(typeElem);
        }
        else if (root.TryGetProperty("machine_type", out JsonElement machTypeElem))
        {
            profile.MotionType = ParseStringValue(machTypeElem);
        }

        if (root.TryGetProperty("gcode_flavor", out JsonElement gcodeElem))
        {
            profile.GcodeDialect = ParseStringValue(gcodeElem);
        }

        profile.HasHeatedBed = ParseOptionalBool(root, "has_heated_bed")
            ?? (root.TryGetProperty("bed_custom_texture", out _) ? true : null);
        profile.HasHeatedChamber = ParseOptionalBool(root, "has_heated_chamber");
        profile.MaxBedTemperature = ParseOptionalInt(root, "max_bed_temp")
            ?? ParseOptionalInt(root, "bed_temperature_limit");
        profile.MaxHotendTemperature = ParseOptionalInt(root, "max_hotend_temp")
            ?? ParseOptionalInt(root, "nozzle_temperature_range_high");
        profile.SupportMultiMaterial = ParseOptionalBool(root, "single_extruder_multi_material");

        // Extruder count from extruder arrays or explicit property
        if (root.TryGetProperty("extruder_count", out JsonElement extCountElem))
        {
            profile.ExtruderCount = ParseIntValue(extCountElem) ?? 1;
        }
        else if (root.TryGetProperty("nozzle_diameter", out JsonElement nozzleArrayElem)
            && nozzleArrayElem.ValueKind == JsonValueKind.Array)
        {
            profile.ExtruderCount = nozzleArrayElem.GetArrayLength();
        }

        // ── Retraction ─────────────────────────────────────────────────────
        profile.RetractionLength = ParseOptionalDouble(root, "retraction_length")
            ?? ParseOptionalDouble(root, "retract_length");
        profile.RetractionSpeed = ParseOptionalDouble(root, "retraction_speed")
            ?? ParseOptionalDouble(root, "retract_speed");
        profile.RetractionLiftZ = ParseOptionalDouble(root, "retract_lift_above")
            ?? ParseOptionalDouble(root, "retraction_minimum_travel");
        profile.DetractionSpeed = ParseOptionalDouble(root, "deretraction_speed")
            ?? ParseOptionalDouble(root, "deretract_speed");

        // ── Bed ────────────────────────────────────────────────────────────
        if (root.TryGetProperty("curr_bed_type", out JsonElement bedTypeElem))
        {
            profile.BedType = ParseStringValue(bedTypeElem);
        }

        if (root.TryGetProperty("bed_shape", out JsonElement bedShapeElem))
        {
            profile.BedShape = ParseStringValue(bedShapeElem);
        }

        // ── G-code ─────────────────────────────────────────────────────────
        if (root.TryGetProperty("machine_start_gcode", out JsonElement startGcodeElem))
        {
            profile.StartGcode = ParseStringValue(startGcodeElem);
        }

        if (root.TryGetProperty("machine_end_gcode", out JsonElement endGcodeElem))
        {
            profile.EndGcode = ParseStringValue(endGcodeElem);
        }

        // ── Motion limits ──────────────────────────────────────────────────
        profile.MaxAccelerationX = ParseOptionalDouble(root, "machine_max_acceleration_x");
        profile.MaxAccelerationY = ParseOptionalDouble(root, "machine_max_acceleration_y");
        profile.MaxFeedrateX = ParseOptionalDouble(root, "machine_max_speed_x");
        profile.MaxFeedrateY = ParseOptionalDouble(root, "machine_max_speed_y");

        // Store all settings as raw JSON for flexibility
        profile.Settings = SerializeElementToDict(root);

        return profile;
    }

    private static void ExtractBuildVolume(JsonElement root, MachineProfileDto profile)
    {
        // OrcaSlicer stores build volume as printable_area polygon or explicit dimensions
        if (root.TryGetProperty("printable_area", out JsonElement areaElem))
        {
            profile.PrintableArea = ParseStringValue(areaElem);

            // Parse dimensions from printable_area like "0x0,220x0,220x220,0x220"
            string? area = profile.PrintableArea;
            if (!string.IsNullOrEmpty(area))
            {
                string[] points = area.Split(',');
                if (points.Length >= 3)
                {
                    // Third point typically has max X and Y
                    string[] maxPoint = points[2].Split('x');
                    if (maxPoint.Length == 2)
                    {
                        if (double.TryParse(maxPoint[0], System.Globalization.CultureInfo.InvariantCulture, out double x))
                        {
                            profile.BuildVolumeX = x;
                        }

                        if (double.TryParse(maxPoint[1], System.Globalization.CultureInfo.InvariantCulture, out double y))
                        {
                            profile.BuildVolumeY = y;
                        }
                    }
                }
            }
        }

        if (root.TryGetProperty("printable_height", out JsonElement heightElem))
        {
            profile.BuildVolumeZ = ParseDoubleValue(heightElem);
        }
        else if (root.TryGetProperty("max_print_height", out JsonElement maxHeightElem))
        {
            profile.BuildVolumeZ = ParseDoubleValue(maxHeightElem);
        }
    }

#pragma warning disable S1172 // Unused parameters are required by interface
    private FilamentProfileDto? ParseFilamentProfile(JsonElement root, string filePath)
    {
        FilamentProfileDto profile = new FilamentProfileDto();

        if (root.TryGetProperty("name", out JsonElement nameElem))
        {
            profile.Name = nameElem.GetString() ?? string.Empty;
        }

        // Extract manufacturer from filament_vendor JSON field (OrcaSlicer stores this as a string array)
        // e.g., "filament_vendor": ["Bambu Lab"] → "Bambu Lab"
        if (root.TryGetProperty("filament_vendor", out JsonElement vendorElem))
        {
            if (vendorElem.ValueKind == JsonValueKind.Array && vendorElem.GetArrayLength() > 0)
            {
                profile.Manufacturer = vendorElem[0].GetString();
            }
            else if (vendorElem.ValueKind == JsonValueKind.String)
            {
                profile.Manufacturer = vendorElem.GetString();
            }
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

        // ── Extended temperature ───────────────────────────────────────────
        profile.FirstLayerNozzleTemperature = ParseOptionalInt(root, "nozzle_temperature_initial_layer")
            ?? ParseOptionalInt(root, "first_layer_temperature");
        profile.FirstLayerBedTemperature = ParseOptionalInt(root, "hot_plate_temp_initial_layer")
            ?? ParseOptionalInt(root, "first_layer_bed_temperature");
        profile.ChamberTemperature = ParseOptionalInt(root, "chamber_temperature")
            ?? ParseOptionalInt(root, "chamber_temp");
        profile.MaxVolumetricSpeed = ParseOptionalDouble(root, "filament_max_volumetric_speed");

        // ── Flow ───────────────────────────────────────────────────────────
        profile.FlowRatio = ParseOptionalDouble(root, "filament_flow_ratio");
        profile.EnablePressureAdvance = ParseOptionalBool(root, "enable_pressure_advance");
        profile.PressureAdvance = ParseOptionalDouble(root, "pressure_advance");

        // ── Retraction ─────────────────────────────────────────────────────
        profile.RetractionLength = ParseOptionalDouble(root, "filament_retraction_length");
        profile.RetractionSpeed = ParseOptionalDouble(root, "filament_retraction_speed")
            ?? ParseOptionalDouble(root, "filament_retract_speed");
        profile.DetractionSpeed = ParseOptionalDouble(root, "filament_deretraction_speed")
            ?? ParseOptionalDouble(root, "filament_deretract_speed");

        // ── Cooling ────────────────────────────────────────────────────────
        profile.EnableFanCooling = ParseOptionalBool(root, "fan_cooling");
        profile.MinFanSpeed = ParseOptionalInt(root, "fan_min_speed");
        profile.MaxFanSpeed = ParseOptionalInt(root, "fan_max_speed");
        profile.BridgeFanSpeed = ParseOptionalInt(root, "overhang_fan_speed");

        // ── Physical properties ────────────────────────────────────────────
        profile.Density = ParseOptionalDouble(root, "filament_density");
        profile.Cost = ParseOptionalDouble(root, "filament_cost");

        if (root.TryGetProperty("default_filament_colour", out JsonElement colorElem))
        {
            profile.Color = ParseStringValue(colorElem);
        }
        else if (root.TryGetProperty("filament_colour", out JsonElement colorElem2))
        {
            profile.Color = ParseStringValue(colorElem2);
        }

        // ── G-code ─────────────────────────────────────────────────────────
        if (root.TryGetProperty("filament_start_gcode", out JsonElement startGcodeElem))
        {
            profile.StartGcode = ParseStringValue(startGcodeElem);
        }

        if (root.TryGetProperty("filament_end_gcode", out JsonElement endGcodeElem))
        {
            profile.EndGcode = ParseStringValue(endGcodeElem);
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

        // Try sparse_infill_density first (primary key), fallback to fill_density
        profile.InfillPercentage = ParseOptionalInt(root, "sparse_infill_density")
            ?? ParseOptionalInt(root, "fill_density")
            ?? 20;

        profile.PrintSpeed = ParseProcessPrintSpeed(root, 50);
        profile.FirstLayerHeight = ParseFirstLayerHeight(root, profile.LayerHeight);
        profile.FirstLayerPrintSpeed = ParseFirstLayerSpeed(root, profile.PrintSpeed);

        if (root.TryGetProperty("enable_support", out JsonElement supportsElem))
        {
            profile.Supports = ParseBoolValue(supportsElem);
        }

        // ── Layers ─────────────────────────────────────────────────────────
        profile.TopLayers = ParseOptionalInt(root, "top_shell_layers") ?? 4;
        profile.BottomLayers = ParseOptionalInt(root, "bottom_shell_layers") ?? 3;

        // ── Walls ──────────────────────────────────────────────────────────
        profile.WallCount = ParseOptionalInt(root, "wall_loops") ?? 3;

        // ── Infill ─────────────────────────────────────────────────────────
        // Try sparse_infill_pattern first (primary key), fallback to fill_pattern
        profile.InfillPattern = ParseOptionalString(root, "sparse_infill_pattern")
            ?? ParseOptionalString(root, "fill_pattern");

        // ── Speed (per-feature) ────────────────────────────────────────────
        // OrcaSlicer uses snake_case property names
        profile.OuterWallSpeed = ParseOptionalInt(root, "outer_wall_speed")
            ?? ParseOptionalInt(root, "external_perimeter_speed");
        profile.InnerWallSpeed = ParseOptionalInt(root, "inner_wall_speed")
            ?? ParseOptionalInt(root, "perimeter_speed");
        profile.InfillSpeed = ParseOptionalInt(root, "sparse_infill_speed")
            ?? ParseOptionalInt(root, "infill_speed");
        profile.TopSurfaceSpeed = ParseOptionalInt(root, "top_surface_speed");
        profile.TravelSpeed = ParseOptionalInt(root, "travel_speed");

        // ── Adhesion ───────────────────────────────────────────────────────
        if (root.TryGetProperty("skirt_type", out JsonElement adhesionElem))
        {
            profile.BedAdhesion = ParseStringValue(adhesionElem);
        }
        else if (root.TryGetProperty("brim_type", out JsonElement brimElem))
        {
            string? brimVal = ParseStringValue(brimElem);
            if (!string.IsNullOrEmpty(brimVal) && brimVal != "no_brim")
            {
                profile.BedAdhesion = "brim";
            }
        }

        // ── Support details ────────────────────────────────────────────────
        if (root.TryGetProperty("support_type", out JsonElement supportTypeElem))
        {
            profile.SupportType = ParseStringValue(supportTypeElem);
        }

        profile.SupportDensity = ParseOptionalInt(root, "support_base_pattern_spacing")
            is not null ? ParseOptionalInt(root, "support_threshold_angle") : null;
        profile.SupportAngle = ParseOptionalInt(root, "support_threshold_angle");

        // ── Seam ───────────────────────────────────────────────────────────
        if (root.TryGetProperty("seam_position", out JsonElement seamElem))
        {
            profile.SeamPosition = ParseStringValue(seamElem);
        }

        // ── Ironing ────────────────────────────────────────────────────────
        profile.EnableIroning = ParseOptionalBool(root, "ironing_type") is not null
            ? ParseOptionalBool(root, "ironing_type") : ParseOptionalBool(root, "enable_ironing");

        // ── Temperature ────────────────────────────────────────────────────
        profile.NozzleTemp = ParseOptionalInt(root, "nozzle_temperature");
        profile.BedTemp = ParseOptionalInt(root, "bed_temperature")
            ?? ParseOptionalInt(root, "hot_plate_temp");
        profile.FirstLayerNozzleTemp = ParseOptionalInt(root, "nozzle_temperature_initial_layer");
        profile.FirstLayerBedTemp = ParseOptionalInt(root, "hot_plate_temp_initial_layer")
            ?? ParseOptionalInt(root, "first_layer_bed_temperature");

        // ── Retraction ─────────────────────────────────────────────────────
        profile.RetractionLength = ParseOptionalDouble(root, "retraction_length")
            ?? ParseOptionalDouble(root, "retract_length");
        profile.RetractionSpeed = ParseOptionalDouble(root, "retraction_speed")
            ?? ParseOptionalDouble(root, "retract_speed");

        // ── Line widths ────────────────────────────────────────────────────
        profile.LineWidthDefault = ParseOptionalDouble(root, "line_width");
        profile.LineWidthOuterWall = ParseOptionalDouble(root, "outer_wall_line_width");
        profile.LineWidthInnerWall = ParseOptionalDouble(root, "inner_wall_line_width");

        // ── Acceleration ───────────────────────────────────────────────────
        profile.DefaultAcceleration = ParseOptionalInt(root, "default_acceleration");
        profile.OuterWallAcceleration = ParseOptionalInt(root, "outer_wall_acceleration");

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

    private static int ParseProcessPrintSpeed(JsonElement root, int fallback)
    {
        string[] speedKeys = ["print_speed", "inner_wall_speed", "outer_wall_speed", "sparse_infill_speed"];

        foreach (string key in speedKeys)
        {
            if (root.TryGetProperty(key, out JsonElement speedElem))
            {
                int? parsed = ParseIntValue(speedElem);
                if (parsed.HasValue)
                {
                    return parsed.Value;
                }
            }
        }

        return fallback;
    }

    private static double ParseFirstLayerHeight(JsonElement root, double fallback)
    {
        string[] firstLayerHeightKeys = ["initial_layer_print_height", "first_layer_height"];

        foreach (string key in firstLayerHeightKeys)
        {
            if (root.TryGetProperty(key, out JsonElement valueElem))
            {
                double? parsed = ParseDoubleValue(valueElem);
                if (parsed.HasValue)
                {
                    return parsed.Value;
                }
            }
        }

        return fallback;
    }

    private static int ParseFirstLayerSpeed(JsonElement root, int fallback)
    {
        string[] firstLayerSpeedKeys = ["initial_layer_speed", "first_layer_speed", "initial_layer_print_speed"];

        foreach (string key in firstLayerSpeedKeys)
        {
            if (root.TryGetProperty(key, out JsonElement valueElem))
            {
                int? parsed = ParseIntValue(valueElem);
                if (parsed.HasValue)
                {
                    return parsed.Value;
                }
            }
        }

        return fallback;
    }

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

    private static int? ParseOptionalInt(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out JsonElement elem))
        {
            return ParseIntValue(elem);
        }

        return null;
    }

    private static double? ParseOptionalDouble(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out JsonElement elem))
        {
            return ParseDoubleValue(elem);
        }

        return null;
    }

    private static bool? ParseOptionalBool(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out JsonElement elem))
        {
            return ParseBoolValue(elem);
        }

        return null;
    }

    private static string? ParseOptionalString(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out JsonElement elem))
        {
            return ParseStringValue(elem);
        }

        return null;
    }

    internal static Dictionary<string, object> SerializeElementToDict(JsonElement elem)
    {
        Dictionary<string, object> dict = [];
        try
        {
            if (elem.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in elem.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                        JsonValueKind.True => "1",
                        JsonValueKind.False => "0",
                        JsonValueKind.Number => prop.Value.GetRawText(),

                        // Arrays: deserialize to List<string> so they serialize as proper JSON arrays
                        JsonValueKind.Array => DeserializeJsonArray(prop.Value),

                        // Objects: store as raw JSON text
                        _ => prop.Value.GetRawText()
                    };
                }
            }
        }
        catch (JsonException)
        {
            // If serialization fails, return empty dict
        }

        return dict;
    }

    /// <summary>
    /// Converts a JSON array element to a <see cref="List{T}"/> of strings.
    /// OrcaSlicer profile arrays always contain string or numeric elements.
    /// </summary>
    private static List<string> DeserializeJsonArray(JsonElement arrayElem)
    {
        var list = new List<string>();
        foreach (JsonElement item in arrayElem.EnumerateArray())
        {
            list.Add(item.ValueKind switch
            {
                JsonValueKind.String => item.GetString() ?? string.Empty,
                _ => item.GetRawText()
            });
        }
        return list;
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
    /// <summary>
    /// Extracts the manufacturer name from a filament profile name.
    /// OrcaSlicer filament names follow the pattern: "Manufacturer Material @Variant"
    /// </summary>
    /// <summary>
    /// Normalizes filament vendor names to match OrcaSlicer UI conventions.
    /// For example, "Bambu Lab" becomes "Bambu", "OrcaFilamentLibrary" becomes "Generic".
    /// </summary>
    private static string NormalizeFilamentVendor(string? vendor)
    {
        if (string.IsNullOrWhiteSpace(vendor))
        {
            return "Generic";
        }

        return vendor.Trim() switch
        {
            "Bambu Lab" => "Bambu",
            "OrcaFilamentLibrary" => "Generic",
            _ => vendor.Trim()
        };
    }

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
