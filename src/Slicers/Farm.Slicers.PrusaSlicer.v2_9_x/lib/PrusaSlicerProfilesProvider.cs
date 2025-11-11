using System.Reflection;
using System.Text.Json;
using Farm.Web.Shared.Contracts.Slicing.Libraries;

namespace Farm.Slicers.PrusaSlicer.v2_9_x.lib;

/// <summary>
/// PrusaSlicer official profiles provider
/// Loads embedded PrusaSlicer official profiles from resources
/// </summary>
public class PrusaSlicerProfilesProvider : ISlicerProfilesProvider
{
    private static readonly Lazy<Dictionary<string, object>> OfficialProfiles = new(LoadOfficialProfiles);

    public Dictionary<string, object> GetOfficialProfiles() => OfficialProfiles.Value;

    private static Dictionary<string, object> LoadOfficialProfiles()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            const string resourceName = "Farm.Slicers.PrusaSlicer.v2_9_x.Resources.official-profiles.json";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                return new Dictionary<string, object>();
            }

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var profiles = JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new();
            return profiles;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load PrusaSlicer official profiles: {ex.Message}");
            return new Dictionary<string, object>();
        }
    }
}
