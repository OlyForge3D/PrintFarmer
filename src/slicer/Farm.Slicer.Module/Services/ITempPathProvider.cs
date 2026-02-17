namespace Farm.Slicer.Module.Services;

/// <summary>
/// Adapter interface for temporary file path management.
/// The host application provides the implementation.
/// </summary>
public interface ITempPathProvider
{
    /// <summary>Gets the base path for temporary files.</summary>
    string TempPath { get; }

    /// <summary>
    /// Gets a temporary file path with the specified extension.
    /// </summary>
    /// <param name="extension">File extension (e.g., ".stl", ".gcode").</param>
    /// <returns>Full path to a unique temporary file.</returns>
    string GetTempFilePath(string extension);
}
