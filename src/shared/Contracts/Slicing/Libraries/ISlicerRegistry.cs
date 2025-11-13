namespace Farm.Web.Shared.Contracts.Slicing.Libraries;

/// <summary>
/// Registry that aggregates all registered slicer libraries.
/// Provides unified access to slicer profiles, assets, and UI metadata.
/// </summary>
public interface ISlicerRegistry
{
    /// <summary>
    /// Gets a specific slicer library by name and version.
    /// </summary>
    ISlicerLibrary? GetLibrary(string slicerName, string version);

    /// <summary>
    /// Gets all registered libraries for a slicer type.
    /// </summary>
    IEnumerable<ISlicerLibrary> GetLibraries(string slicerName);

    /// <summary>
    /// Gets UI metadata for a specific slicer library.
    /// </summary>
    ISlicerUIProvider? GetUIProvider(string slicerName, string version);

    /// <summary>
    /// Gets the latest/default version of a slicer.
    /// </summary>
    ISlicerLibrary? GetLatestLibrary(string slicerName);

    /// <summary>
    /// Lists all available slicer libraries across all registered slicers.
    /// </summary>
    IEnumerable<ISlicerLibrary> ListAllLibraries();

    /// <summary>
    /// Lists all available slicer types (e.g., ["OrcaSlicer", "PrusaSlicer"]).
    /// </summary>
    IEnumerable<string> ListAvailableSlicers();
}

/// <summary>
/// Implementation of ISlicerRegistry using dependency injection.
/// </summary>
public class SlicerRegistry : ISlicerRegistry
{
    private readonly Dictionary<string, List<ISlicerLibrary>> _librariesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ISlicerUIProvider> _uiProvidersByKey = new(StringComparer.OrdinalIgnoreCase);

    public SlicerRegistry(IEnumerable<ISlicerLibrary> libraries, IEnumerable<ISlicerUIProvider> uiProviders)
    {
        // Index libraries by slicer name
        foreach (var library in libraries)
        {
            if (!_librariesByName.TryGetValue(library.SlicerName, out var list))
            {
                list = [];
                _librariesByName[library.SlicerName] = list;
            }
            list.Add(library);
        }

        // Index UI providers by name+version key
        foreach (var provider in uiProviders)
        {
            var key = $"{provider.SlicerName}:{provider.SlicerVersion}";
            _uiProvidersByKey[key] = provider;
        }

        // Sort each slicer's libraries by version (descending) so latest is first
        foreach (var libs in _librariesByName.Values)
        {
            libs.Sort((a, b) => CompareVersions(b.SlicerVersion, a.SlicerVersion));
        }
    }

    public ISlicerLibrary? GetLibrary(string slicerName, string version)
    {
        if (_librariesByName.TryGetValue(slicerName, out var libs))
        {
            return libs.FirstOrDefault(l => l.SlicerVersion == version);
        }
        return null;
    }

    public IEnumerable<ISlicerLibrary> GetLibraries(string slicerName)
    {
        return _librariesByName.TryGetValue(slicerName, out var libs)
            ? libs
            : Enumerable.Empty<ISlicerLibrary>();
    }

    public ISlicerUIProvider? GetUIProvider(string slicerName, string version)
    {
        var key = $"{slicerName}:{version}";
        return _uiProvidersByKey.TryGetValue(key, out var provider) ? provider : null;
    }

    public ISlicerLibrary? GetLatestLibrary(string slicerName)
    {
        return _librariesByName.TryGetValue(slicerName, out var libs)
            ? libs.FirstOrDefault()
            : null;
    }

    public IEnumerable<ISlicerLibrary> ListAllLibraries()
    {
        return _librariesByName.Values.SelectMany(x => x);
    }

    public IEnumerable<string> ListAvailableSlicers()
    {
        return _librariesByName.Keys;
    }

    private static int CompareVersions(string version1, string version2)
    {
        var v1 = Version.TryParse(version1, out var parsed1) ? parsed1 : new Version(0, 0);
        var v2 = Version.TryParse(version2, out var parsed2) ? parsed2 : new Version(0, 0);
        return v1.CompareTo(v2);
    }
}
