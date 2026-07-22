using System.Reflection;
using System.Text.Json;
using Farm.Slicer.Module.Contracts.Libraries;

namespace Farm.Slicers.OrcaSlicer.v2_4_0;

/// <summary>
/// Provides access to OrcaSlicer v2.4.0 assets (bed models, textures, printer cover images).
/// Assets are embedded as resources in the library assembly.
/// </summary>
public class OrcaSlicerAssetRegistry : ISlicerAssetRegistry, IDisposable
{
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private IReadOnlyDictionary<string, SlicerAsset> _assetsCache = new Dictionary<string, SlicerAsset>();
    private bool _initialized = false;

    public async Task<SlicerAsset?> GetAssetAsync(
        string manufacturerName,
        string modelName,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        IReadOnlyDictionary<string, SlicerAsset> assetsCache = Volatile.Read(ref _assetsCache);
        var key = $"{manufacturerName}:{modelName}".ToLowerInvariant();
        return assetsCache.TryGetValue(key, out var asset) ? asset : null;
    }

    public async Task<IEnumerable<SlicerAsset>> ListAssetsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return Volatile.Read(ref _assetsCache).Values;
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

    public void Dispose()
    {
        _initializationLock.Dispose();
        GC.SuppressFinalize(this);
    }

#pragma warning disable S1172 // Unused parameters are allowed in private methods
    private async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
#pragma warning restore S1172
        if (Volatile.Read(ref _initialized))
        {
            return;
        }

        await _initializationLock.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _initialized))
            {
                return;
            }

            // Load asset manifest from embedded resources
            var assembly = typeof(OrcaSlicerLibrary_v2_4_0).Assembly;
            const string manifestResource = "orcaslicer_v2_4_0_assets_manifest.json";
            var assetsCache = new Dictionary<string, SlicerAsset>();

            using var manifestStream = assembly.GetManifestResourceStream(manifestResource);
            if (manifestStream is not null)
            {
                try
                {
                    using var manifest = await JsonDocument.ParseAsync(manifestStream, cancellationToken: ct);
                    foreach (SlicerAsset asset in ParseManifest(manifest.RootElement))
                    {
                        var key = $"{asset.ManufacturerName}:{asset.ModelName}".ToLowerInvariant();
                        assetsCache[key] = asset;
                    }
                }
                catch (JsonException)
                {
                    assetsCache.Clear();
                }
                catch (InvalidOperationException)
                {
                    assetsCache.Clear();
                }
            }

            Volatile.Write(ref _assetsCache, assetsCache);
            Volatile.Write(ref _initialized, true);
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private static Stream? GetEmbeddedResourceStream(string resourcePath)
    {
        var assembly = typeof(OrcaSlicerLibrary_v2_4_0).Assembly;
        var resourceName = $"OrcaSlicer_v2_4_0_Assets_{resourcePath}"
            .Replace('\\', '.')
            .Replace('/', '.')
            .ToLowerInvariant();

        return assembly.GetManifestResourceStream(resourceName);
    }

    private static IEnumerable<SlicerAsset> ParseManifest(JsonElement root)
    {
        if (TryGetProperty(root, "assets", out JsonElement assets) &&
            assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                SlicerAsset? parsedAsset = ParseAssetEntry(asset);
                if (parsedAsset is not null)
                {
                    yield return parsedAsset;
                }
            }

            yield break;
        }

        if (!TryGetProperty(root, "manufacturers", out JsonElement manufacturers) ||
            manufacturers.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement manufacturer in manufacturers.EnumerateArray())
        {
            string? manufacturerName = GetString(manufacturer, "name");
            if (string.IsNullOrWhiteSpace(manufacturerName) ||
                !TryGetProperty(manufacturer, "printers", out JsonElement printers) ||
                printers.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement printer in printers.EnumerateArray())
            {
                string? modelName = GetString(printer, "name");
                if (string.IsNullOrWhiteSpace(modelName))
                {
                    continue;
                }

                yield return new SlicerAsset(
                    manufacturerName,
                    modelName,
                    HasAssetPath(printer, "bedModel"),
                    HasAssetPath(printer, "bedTexture"),
                    GetString(printer, "bedTextureFormat") ?? GetTextureFormat(GetString(printer, "bedTexture")),
                    HasAssetPath(printer, "cover"),
                    "2.4.0");
            }
        }
    }

    private static SlicerAsset? ParseAssetEntry(JsonElement asset)
    {
        string? manufacturerName = GetString(asset, "manufacturerName") ?? GetString(asset, "manufacturer");
        string? modelName = GetString(asset, "modelName") ?? GetString(asset, "model") ?? GetString(asset, "name");
        if (string.IsNullOrWhiteSpace(manufacturerName) || string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        string? bedTexture = GetString(asset, "bedTexture");
        bool hasCoverImage = GetBool(asset, "hasCoverImage") ??
            (HasAssetPath(asset, "coverImage") || HasAssetPath(asset, "cover"));

        return new SlicerAsset(
            manufacturerName,
            modelName,
            GetBool(asset, "hasBedModel") ?? HasAssetPath(asset, "bedModel"),
            GetBool(asset, "hasBedTexture") ?? !string.IsNullOrWhiteSpace(bedTexture),
            GetString(asset, "bedTextureFormat") ?? GetTextureFormat(bedTexture),
            hasCoverImage,
            "2.4.0");
    }

    private static bool HasAssetPath(JsonElement element, string propertyName)
    {
        return !string.IsNullOrWhiteSpace(GetString(element, propertyName));
    }

    private static bool? GetBool(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out JsonElement property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        foreach (JsonProperty candidate in element.EnumerateObject())
        {
            if (candidate.NameEquals(propertyName))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static string? GetTextureFormat(string? bedTexture)
    {
        return string.IsNullOrWhiteSpace(bedTexture)
            ? null
            : Path.GetExtension(bedTexture).TrimStart('.').ToLowerInvariant();
    }
}
