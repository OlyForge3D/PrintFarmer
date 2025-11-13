using System.Reflection;
using System.Text.Json;
using Farm.Web.Shared.Contracts.Slicing.Libraries;

namespace Farm.Slicers.OrcaSlicer.v2_3_1;

/// <summary>
/// Provides access to OrcaSlicer v2.3.1 official profiles.
/// Profiles are organized as:
/// - index.json: Lists all available machine profiles
/// - machines/{id}.json: Individual machine profiles with machine/processes/filaments
/// </summary>
public class OrcaSlicerProfilesProvider : ISlicerProfilesProvider
{
    private readonly Dictionary<string, SlicerProfileMetadata> _profilesCache = [];
    private readonly Dictionary<string, string> _profileJsonCache = [];
    private string? _universalFilamentsJson = null;
    private bool _initialized = false;

    public async Task<IEnumerable<SlicerProfileMetadata>> ListOfficialProfilesAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return _profilesCache.Values;
    }

    public async Task<string?> GetProfileJsonAsync(string profileId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return _profileJsonCache.TryGetValue(profileId, out var json) ? json : null;
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

    public string GetProfilesVersion() => "2.3.1";

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            // Load the index
            var assembly = typeof(OrcaSlicerLibrary_v2_3_1).Assembly;
            const string indexResourceName = "OrcaSlicer_v2_3_1_Profiles_Index.json";

            var indexStream = assembly.GetManifestResourceStream(indexResourceName);
            if (indexStream == null)
            {
                return; // No profiles available
            }

            using var indexReader = new StreamReader(indexStream);
            var indexJson = await indexReader.ReadToEndAsync(ct);
            using var indexDoc = JsonDocument.Parse(indexJson);
            var indexRoot = indexDoc.RootElement;

            // Parse machine entries from index
            if (indexRoot.TryGetProperty("machines", out var machinesElement) &&
                machinesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var machineEntry in machinesElement.EnumerateArray())
                {
                    var id = machineEntry.GetProperty("id").GetString();
                    var name = machineEntry.GetProperty("name").GetString();
                    var manufacturer = machineEntry.GetProperty("manufacturer").GetString();

                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) ||
                        string.IsNullOrEmpty(manufacturer))
                    {
                        continue;
                    }

                    // Create metadata for the machine
                    var metadata = new SlicerProfileMetadata(
                        Id: id,
                        Name: name,
                        Type: "printer",
                        Manufacturer: manufacturer,
                        PrinterModel: name
                    );

                    _profilesCache[id] = metadata;

                    // Load the individual machine profile on demand
                    await LoadMachineProfileAsync(assembly, id, ct);
                }
            }

            // Load universal filaments
            await LoadUniversalFilamentsAsync(assembly, ct);
        }
        catch
        {
            // If parsing fails, just proceed with empty profile cache
        }
    }

    private async Task LoadMachineProfileAsync(Assembly assembly, string machineId, CancellationToken ct)
    {
        try
        {
            // Load individual machine profile
            var resourceName = $"OrcaSlicer_v2_3_1_Profiles_Machines_{machineId}.json";
            var profileStream = assembly.GetManifestResourceStream(resourceName);

            if (profileStream != null)
            {
                using var reader = new StreamReader(profileStream);
                var json = await reader.ReadToEndAsync(ct);
                _profileJsonCache[machineId] = json;
            }
        }
        catch
        {
            // Skip profiles that fail to load
        }
    }

    private async Task LoadUniversalFilamentsAsync(Assembly assembly, CancellationToken ct)
    {
        try
        {
            // Load universal filaments library
            const string filamentsResourceName = "OrcaSlicer_v2_3_1_Filaments_Universal.json";
            var filamentsStream = assembly.GetManifestResourceStream(filamentsResourceName);

            if (filamentsStream != null)
            {
                using var reader = new StreamReader(filamentsStream);
                _universalFilamentsJson = await reader.ReadToEndAsync(ct);
            }
        }
        catch
        {
            // Skip if filaments fail to load
        }
    }
}
