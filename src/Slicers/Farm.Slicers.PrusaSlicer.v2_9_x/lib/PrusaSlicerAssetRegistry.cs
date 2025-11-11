using System.Reflection;
using System.Text.Json;
using Farm.Web.Shared.Contracts.Slicing.Libraries;

namespace Farm.Slicers.PrusaSlicer.v2_9_x.lib;

/// <summary>
/// PrusaSlicer asset registry for bed models and textures
/// </summary>
public class PrusaSlicerAssetRegistry : ISlicerAssetRegistry
{
    private static readonly Lazy<Dictionary<string, object>> AssetManifest = new(LoadAssetManifest);

    public Dictionary<string, object> GetAssetManifest() => AssetManifest.Value;

    private static Dictionary<string, object> LoadAssetManifest()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            const string resourceName = "Farm.Slicers.PrusaSlicer.v2_9_x.Resources.manifest.json";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                return new Dictionary<string, object>();
            }

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var manifest = JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new();
            return manifest;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load PrusaSlicer asset manifest: {ex.Message}");
            return new Dictionary<string, object>();
        }
    }
}
