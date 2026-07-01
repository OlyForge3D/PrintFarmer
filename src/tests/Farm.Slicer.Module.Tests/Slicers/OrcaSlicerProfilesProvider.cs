using System.Text.Json;
using Farm.Slicer.Module.Contracts.Libraries;

namespace Farm.Slicer.Module.Tests.Slicers;

/// <summary>
/// Test double for loading OrcaSlicer v2.4.0 profiles from the file system.
/// 
/// This is used ONLY for testing profile loading logic with sample data.
/// Production code uses NullProfilesProvider and loads profiles from the OrcaSlicer worker service.
/// 
/// Loads profiles from the file system (real OrcaSlicer structure):
/// - Manufacturer.json: Bundle file listing all profiles for a manufacturer
/// - {manufacturer}/machine/*.json: Individual machine profiles
/// - {manufacturer}/filament/*.json: Individual filament profiles
/// - OrcaFilamentLibrary/filament/*.json: Universal filament profiles available to all printers
/// </summary>
internal class OrcaSlicerProfilesProvider : ISlicerProfilesProvider
{
    private readonly Dictionary<string, SlicerProfileMetadata> _profilesCache = [];
    private readonly Dictionary<string, string> _profileJsonCache = [];
    private string? _universalFilamentsJson = null;
    private bool _initialized = false;
    private readonly string _profilesPath;

    /// <summary>
    /// Creates a new OrcaSlicerProfilesProvider for testing.
    /// </summary>
    /// <param name="profilesPath">Path to profiles directory (typically sample_profiles/orcaslicer for testing)</param>
    internal OrcaSlicerProfilesProvider(string? profilesPath = null)
    {
        _profilesPath = profilesPath ??
            Environment.GetEnvironmentVariable("ORCA_PROFILES_PATH") ??
            "/opt/orcaslicer/resources/profiles";
    }

    public async Task<IEnumerable<SlicerProfileMetadata>> ListOfficialProfilesAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return _profilesCache.Values;
    }

    public async Task<string?> GetProfileJsonAsync(string profileId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return _profileJsonCache.TryGetValue(profileId, out string? json) ? json : null;
    }

    /// <summary>
    /// Gets the universal filaments library (Bambu, base, eSUN, Overture, Polymaker, SUNLU).
    /// These filaments are available to all printer models.
    /// </summary>
    public async Task<string?> GetUniversalFilamentsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return _universalFilamentsJson;
    }

    public string GetProfilesVersion() => "2.4.0";

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            if (!Directory.Exists(_profilesPath))
            {
                return; // Profiles directory not found
            }

            // Load all manufacturer bundles (e.g., Prusa.json, Elegoo.json, etc.)
            IEnumerable<string> bundleFiles = Directory.GetFiles(_profilesPath, "*.json")
                .Where(f => !Path.GetFileName(f).StartsWith("index", StringComparison.OrdinalIgnoreCase))
                .Where(f => !Path.GetFileName(f).StartsWith("official", StringComparison.OrdinalIgnoreCase));

            foreach (string bundleFile in bundleFiles)
            {
                try
                {
                    string bundleJson = await File.ReadAllTextAsync(bundleFile, ct);
                    using var bundleDoc = JsonDocument.Parse(bundleJson);
                    JsonElement bundleRoot = bundleDoc.RootElement;
                    string manufacturerName = Path.GetFileNameWithoutExtension(bundleFile);

                    // Parse machine_model_list from the bundle
                    if (bundleRoot.TryGetProperty("machine_model_list", out JsonElement machineModelsElement) &&
                        machineModelsElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement machineEntry in machineModelsElement.EnumerateArray())
                        {
                            // Bundle structure: { "name": "...", "sub_path": "..." }
                            string? name = machineEntry.GetProperty("name").GetString();
                            string? subPath = machineEntry.GetProperty("sub_path").GetString();

                            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(subPath))
                            {
                                continue;
                            }

                            // Use name as the ID (OrcaSlicer bundles don't have explicit IDs)
                            string id = name;

                            // Create metadata for the machine
                            var metadata = new SlicerProfileMetadata(
                                Id: id,
                                Name: name,
                                Type: "printer",
                                Manufacturer: manufacturerName,
                                PrinterModel: name
                            );

                            _profilesCache[id] = metadata;

                            // Load the actual profile JSON file
                            await LoadProfileJsonAsync(manufacturerName, subPath, id, ct);
                        }
                    }
                }
                catch
                {
                    // Skip bundles that fail to parse
                }
            }

            // Load universal filaments
            await LoadUniversalFilamentsAsync(ct);
        }
        catch
        {
            // If parsing fails, just proceed with empty profile cache
        }
    }

    private async Task LoadUniversalFilamentsAsync(CancellationToken ct)
    {
        try
        {
            // Load universal filaments from OrcaFilamentLibrary directory
            string universalFilamentsDir = Path.Combine(_profilesPath, "OrcaFilamentLibrary");

            if (!Directory.Exists(universalFilamentsDir))
            {
                return; // No universal filaments available
            }

            string bundlePath = Path.Combine(_profilesPath, "OrcaFilamentLibrary.json");
            if (!File.Exists(bundlePath))
            {
                return;
            }

            string bundleJson = await File.ReadAllTextAsync(bundlePath, ct);
            using var bundleDoc = JsonDocument.Parse(bundleJson);
            if (!bundleDoc.RootElement.TryGetProperty("filament_list", out JsonElement filamentList) ||
                filamentList.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var filaments = new List<object>();

            foreach (JsonElement filamentEntry in filamentList.EnumerateArray())
            {
                try
                {
                    string? subPath = filamentEntry.GetProperty("sub_path").GetString();
                    if (string.IsNullOrWhiteSpace(subPath))
                    {
                        continue;
                    }

                    string filamentsFile = Path.Combine(universalFilamentsDir, subPath);
                    if (!File.Exists(filamentsFile))
                    {
                        continue;
                    }

                    string filamentsJson = await File.ReadAllTextAsync(filamentsFile, ct);
                    using var filamentsDoc = JsonDocument.Parse(filamentsJson);
                    filaments.Add(filamentsDoc.RootElement.Clone());
                }
                catch
                {
                    // Skip individual filament files that fail to parse
                }
            }

            // Store as JSON array of filaments
            if (filaments.Count > 0)
            {
                _universalFilamentsJson = System.Text.Json.JsonSerializer.Serialize(filaments);
            }
        }
        catch
        {
            // Skip if universal filaments fail to load
        }
    }

    private async Task LoadProfileJsonAsync(string manufacturerName, string subPath, string profileId, CancellationToken ct)
    {
        try
        {
            // Construct the full path to the profile file
            // subPath is relative to the manufacturer directory, e.g., "machine/Prusa CORE One.json"
            string profilePath = Path.Combine(_profilesPath, manufacturerName, subPath);

            if (!File.Exists(profilePath))
            {
                return; // Profile file not found
            }

            // Load and cache the full profile JSON
            string profileJson = await File.ReadAllTextAsync(profilePath, ct);
            _profileJsonCache[profileId] = profileJson;
        }
        catch
        {
            // Skip profiles that fail to load
        }
    }
}
