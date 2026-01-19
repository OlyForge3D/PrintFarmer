namespace Farm.Infrastructure.Contracts.Slicing.Libraries;

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
        foreach (ISlicerLibrary library in libraries)
        {
            if (!_librariesByName.TryGetValue(library.SlicerName, out List<ISlicerLibrary>? list))
            {
                list = [];
                _librariesByName[library.SlicerName] = list;
            }

            list.Add(library);
        }

        // Index UI providers by name+version key
        foreach (ISlicerUIProvider provider in uiProviders)
        {
            string key = $"{provider.SlicerName}:{provider.SlicerVersion}";
            _uiProvidersByKey[key] = provider;
        }

        // Sort each slicer's libraries by version (descending) so latest is first
        foreach (List<ISlicerLibrary> libs in _librariesByName.Values)
        {
            libs.Sort((a, b) => CompareVersions(b.SlicerVersion, a.SlicerVersion));
        }
    }

    public ISlicerLibrary? GetLibrary(string slicerName, string version)
    {
        return _librariesByName.TryGetValue(slicerName, out List<ISlicerLibrary>? libs)
            ? libs.FirstOrDefault(l => l.SlicerVersion == version)
            : (ISlicerLibrary?)null;
    }

    public IEnumerable<ISlicerLibrary> GetLibraries(string slicerName)
    {
        return _librariesByName.TryGetValue(slicerName, out List<ISlicerLibrary>? libs)
            ? libs
            : Enumerable.Empty<ISlicerLibrary>();
    }

    public ISlicerUIProvider? GetUIProvider(string slicerName, string version)
    {
        string key = $"{slicerName}:{version}";
        return _uiProvidersByKey.TryGetValue(key, out ISlicerUIProvider? provider) ? provider : null;
    }

    public ISlicerLibrary? GetLatestLibrary(string slicerName)
    {
        return _librariesByName.TryGetValue(slicerName, out List<ISlicerLibrary>? libs)
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
        Version v1 = Version.TryParse(version1, out Version? parsed1) ? parsed1 : new Version(0, 0);
        Version v2 = Version.TryParse(version2, out Version? parsed2) ? parsed2 : new Version(0, 0);
        return v1.CompareTo(v2);
    }
}
