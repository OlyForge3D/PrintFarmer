namespace Farm.Slicer.Module.Services;

/// <summary>
/// Adapter interface for stored file operations (reading, path resolution).
/// The host application provides the implementation bridging to its storage infrastructure.
/// </summary>
public interface IStoredFileOperationsService
{
    /// <summary>
    /// Gets the physical file path for a stored file by its ID.
    /// </summary>
    /// <param name="fileId">The stored file identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The physical file path, or <c>null</c> if not found.</returns>
    Task<string?> GetFilePathAsync(Guid fileId, CancellationToken ct = default);

    /// <summary>
    /// Reads the contents of a stored file.
    /// </summary>
    /// <param name="fileId">The stored file identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The file contents as a stream, or <c>null</c> if not found.</returns>
    Task<Stream?> ReadFileAsync(Guid fileId, CancellationToken ct = default);
}
