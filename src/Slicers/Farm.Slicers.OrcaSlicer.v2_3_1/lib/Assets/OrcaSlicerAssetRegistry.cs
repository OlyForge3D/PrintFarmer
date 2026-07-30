using System.Text.Json;
using Farm.Slicer.Module.Contracts.Libraries;

namespace Farm.Slicers.OrcaSlicer.v2_3_1;

/// <summary>
/// OrcaSlicer v2.3.1 asset registry. Ships with an empty manifest; populate
/// <c>lib/Assets/manifest.json</c> and per-manufacturer folders if the previous
/// engine needs distinct bed models / textures / cover images.
/// </summary>
public class OrcaSlicerAssetRegistry : ISlicerAssetRegistry, IDisposable
{
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private IReadOnlyDictionary<string, SlicerAsset> _assetsCache = new Dictionary<string, SlicerAsset>();
    private bool _initialized;

    public async Task<SlicerAsset?> GetAssetAsync(
        string manufacturerName,
        string modelName,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        IReadOnlyDictionary<string, SlicerAsset> cache = Volatile.Read(ref _assetsCache);
        string key = $"{manufacturerName}:{modelName}".ToLowerInvariant();
        return cache.TryGetValue(key, out SlicerAsset? asset) ? asset : null;
    }

    public async Task<IEnumerable<SlicerAsset>> ListAssetsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return Volatile.Read(ref _assetsCache).Values;
    }

    public Stream? GetBedModelStream(string manufacturerName, string modelName)
        => GetEmbeddedResourceStream($"bed-models/{manufacturerName}/{modelName}.stl");

    public Stream? GetBedTextureStream(string manufacturerName, string modelName)
    {
        Stream? svg = GetEmbeddedResourceStream($"bed-textures/{manufacturerName}/{modelName}_texture.svg");
        return svg ?? GetEmbeddedResourceStream($"bed-textures/{manufacturerName}/{modelName}_texture.png");
    }

    public Stream? GetCoverImageStream(string manufacturerName, string modelName)
        => GetEmbeddedResourceStream($"cover-images/{manufacturerName}/{modelName}_cover.png");

    public void Dispose()
    {
        _initializationLock.Dispose();
        GC.SuppressFinalize(this);
    }

#pragma warning disable S1172
    private async Task EnsureInitializedAsync(CancellationToken ct = default)
#pragma warning restore S1172
    {
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

            System.Reflection.Assembly assembly = typeof(OrcaSlicerLibrary_v2_3_1).Assembly;
            const string manifestResource = "orcaslicer_v2_3_1_assets_manifest.json";
            Dictionary<string, SlicerAsset> cache = new();

            using Stream? manifestStream = assembly.GetManifestResourceStream(manifestResource);
            if (manifestStream is not null)
            {
                try
                {
                    using JsonDocument manifest = await JsonDocument.ParseAsync(manifestStream, cancellationToken: ct);
                    if (manifest.RootElement.TryGetProperty("assets", out JsonElement assets)
                        && assets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement asset in assets.EnumerateArray())
                        {
                            string? manufacturer = asset.TryGetProperty("manufacturerName", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                            string? model = asset.TryGetProperty("modelName", out JsonElement md) && md.ValueKind == JsonValueKind.String ? md.GetString() : null;
                            if (string.IsNullOrWhiteSpace(manufacturer) || string.IsNullOrWhiteSpace(model))
                            {
                                continue;
                            }

                            string key = $"{manufacturer}:{model}".ToLowerInvariant();
                            cache[key] = new SlicerAsset(manufacturer, model, false, false, null, false, "2.3.1");
                        }
                    }
                }
                catch (JsonException)
                {
                    cache.Clear();
                }
                catch (InvalidOperationException)
                {
                    cache.Clear();
                }
            }

            Volatile.Write(ref _assetsCache, cache);
            Volatile.Write(ref _initialized, true);
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private static Stream? GetEmbeddedResourceStream(string resourcePath)
    {
        System.Reflection.Assembly assembly = typeof(OrcaSlicerLibrary_v2_3_1).Assembly;
        string resourceName = $"OrcaSlicer_v2_3_1_Assets_{resourcePath}"
            .Replace('\\', '.')
            .Replace('/', '.')
            .ToLowerInvariant();
        return assembly.GetManifestResourceStream(resourceName);
    }
}
