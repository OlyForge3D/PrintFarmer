using System.Reflection;
using System.Text.Json;
using Farm.Web.Shared.Contracts.Slicing.Libraries;

namespace Farm.Slicers.OrcaSlicer.v2_3_x;

/// <summary>
/// Provides access to OrcaSlicer v2.3.1 official profiles.
/// Profiles are embedded as resources in the library assembly.
/// </summary>
public class OrcaSlicerProfilesProvider : ISlicerProfilesProvider
{
    private readonly Dictionary<string, SlicerProfileMetadata> _profilesCache = [];
    private readonly Dictionary<string, string> _profileJsonCache = [];
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

    public string GetProfilesVersion() => "2.3.1";

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        // Load embedded profiles resource
        var assembly = typeof(OrcaSlicerLibrary_v2_3_x).Assembly;
        const string resourceName = "OrcaSlicer_v2_3_x_Profiles.json";

        var resourceStream = assembly.GetManifestResourceStream(resourceName);
        if (resourceStream == null)
        {
            // No profiles embedded yet - this is OK for now, profiles can be imported
            return;
        }

        try
        {
            using var reader = new StreamReader(resourceStream);
            var json = await reader.ReadToEndAsync(ct);

            // Parse profiles from embedded JSON
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // TODO: Parse profiles based on actual OrcaSlicer profile format
            // For now, this is a placeholder
            _profilesCache.Clear();
            _profileJsonCache.Clear();
        }
        catch
        {
            // If parsing fails, just proceed with empty profile cache
            // Profiles can be imported manually
        }
    }
}
