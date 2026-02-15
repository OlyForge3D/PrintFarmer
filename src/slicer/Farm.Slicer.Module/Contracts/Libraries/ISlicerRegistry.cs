namespace Farm.Slicer.Module.Contracts.Libraries;

/// <summary>
/// Registry that aggregates all registered slicer libraries.
/// </summary>
public interface ISlicerRegistry
{
    /// <summary>
    /// Gets a specific slicer library by name and version.
    /// </summary>
    /// <param name="slicerName">The slicer name (e.g., "OrcaSlicer").</param>
    /// <param name="version">The exact version string.</param>
    /// <returns>The matching library, or <c>null</c> if not found.</returns>
    ISlicerLibrary? GetLibrary(string slicerName, string version);

    /// <summary>
    /// Gets all registered library versions for a slicer, ordered by version descending.
    /// </summary>
    /// <param name="slicerName">The slicer name.</param>
    /// <returns>All matching slicer libraries.</returns>
    IEnumerable<ISlicerLibrary> GetLibraries(string slicerName);

    /// <summary>
    /// Gets the UI provider for a specific slicer name and version.
    /// </summary>
    /// <param name="slicerName">The slicer name.</param>
    /// <param name="version">The exact version string.</param>
    /// <returns>The UI provider, or <c>null</c> if not available.</returns>
    ISlicerUIProvider? GetUIProvider(string slicerName, string version);

    /// <summary>
    /// Gets the latest (highest version) library for a slicer.
    /// </summary>
    /// <param name="slicerName">The slicer name.</param>
    /// <returns>The latest library, or <c>null</c> if none registered.</returns>
    ISlicerLibrary? GetLatestLibrary(string slicerName);

    /// <summary>
    /// Lists all registered slicer libraries across all slicer types.
    /// </summary>
    /// <returns>All registered slicer libraries.</returns>
    IEnumerable<ISlicerLibrary> ListAllLibraries();

    /// <summary>
    /// Lists the names of all available slicer types.
    /// </summary>
    /// <returns>Distinct slicer names.</returns>
    IEnumerable<string> ListAvailableSlicers();
}
