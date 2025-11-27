using System.Reflection;
using Farm.Web.Shared.Contracts.Slicing.Libraries;

namespace Farm.Slicers.OrcaSlicer.v2_3_1;

/// <summary>
/// Provides access to OrcaSlicer v2.3.1 assets (bed models, textures, printer cover images).
/// Assets are embedded as resources in the library assembly.
/// </summary>
public class OrcaSlicerAssetRegistry : ISlicerAssetRegistry
{
    private readonly Dictionary<string, SlicerAsset> _assetsCache = [];
    private bool _initialized = false;

    public async Task<SlicerAsset?> GetAssetAsync(
        string manufacturerName,
        string modelName,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var key = $"{manufacturerName}:{modelName}".ToLowerInvariant();
        return _assetsCache.TryGetValue(key, out var asset) ? asset : null;
    }

    public async Task<IEnumerable<SlicerAsset>> ListAssetsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return _assetsCache.Values;
    }

    public Stream? GetBedModelStream(string manufacturerName, string modelName)
    {
        return GetEmbeddedResourceStream($"bed-models/{manufacturerName}/{modelName}.stl");
    }

    public Stream? GetBedTextureStream(string manufacturerName, string modelName)
    {
        // Try SVG first, then PNG
        var svgStream = GetEmbeddedResourceStream($"bed-textures/{manufacturerName}/{modelName}_texture.svg");
        if (svgStream != null)
        {
            return svgStream;
        }

        return GetEmbeddedResourceStream($"bed-textures/{manufacturerName}/{modelName}_texture.png");
    }

    public Stream? GetCoverImageStream(string manufacturerName, string modelName)
    {
        return GetEmbeddedResourceStream($"cover-images/{manufacturerName}/{modelName}_cover.png");
    }

#pragma warning disable S1172 // Unused parameters are allowed in private methods
    private async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
#pragma warning restore S1172
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        // Load asset manifest from embedded resources
        var assembly = typeof(OrcaSlicerLibrary_v2_3_1).Assembly;
        const string manifestResource = "OrcaSlicer_v2_3_1_Assets_manifest.json";

        var manifestStream = assembly.GetManifestResourceStream(manifestResource);
        if (manifestStream == null)
        {
            // No manifest embedded yet - assets can be added later
            return;
        }

        // TODO: Parse manifest and populate _assetsCache
        await Task.CompletedTask;
    }

    private static Stream? GetEmbeddedResourceStream(string resourcePath)
    {
        var assembly = typeof(OrcaSlicerLibrary_v2_3_1).Assembly;
        var resourceName = $"OrcaSlicer_v2_3_1_Assets_{resourcePath}".Replace('/', '_');

        return assembly.GetManifestResourceStream(resourceName);
    }
}
