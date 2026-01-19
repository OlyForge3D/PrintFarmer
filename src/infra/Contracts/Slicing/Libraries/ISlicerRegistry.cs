namespace Farm.Infrastructure.Contracts.Slicing.Libraries;

/// <summary>
/// Registry that aggregates all registered slicer libraries.
/// Provides unified access to slicer profiles, assets, and UI metadata.
/// </summary>
public interface ISlicerRegistry
{
    /// <summary>
    /// Gets a specific slicer library by name and version.
    /// </summary>
    /// <param name="slicerName">The name of the slicer (e.g., "OrcaSlicer", "PrusaSlicer").</param>
    /// <param name="version">The version of the slicer library.</param>
    ISlicerLibrary? GetLibrary(string slicerName, string version);

    /// <summary>
    /// Gets all registered libraries for a slicer type.
    /// </summary>
    /// <param name="slicerName">The name of the slicer type to get libraries for.</param>
    IEnumerable<ISlicerLibrary> GetLibraries(string slicerName);

    /// <summary>
    /// Gets UI metadata for a specific slicer library.
    /// </summary>
    /// <param name="slicerName">The name of the slicer.</param>
    /// <param name="version">The version of the slicer library.</param>
    ISlicerUIProvider? GetUIProvider(string slicerName, string version);

    /// <summary>
    /// Gets the latest/default version of a slicer.
    /// </summary>
    /// <param name="slicerName">The name of the slicer to get the latest version for.</param>
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
