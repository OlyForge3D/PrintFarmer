namespace Farm.Slicer.Module.Services;

/// <summary>
/// Adapter interface for general file management operations (upload, delete, move).
/// The host application provides the implementation bridging to its storage infrastructure.
/// </summary>
public interface ISlicerFileManagementService
{
    /// <summary>
    /// Stores a file from a stream.
    /// </summary>
    /// <param name="sourceStream">The source data stream.</param>
    /// <param name="fileName">Original file name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored file identifier.</returns>
    Task<Guid> StoreFileAsync(Stream sourceStream, string fileName, CancellationToken ct = default);

    /// <summary>
    /// Deletes a stored file by its ID.
    /// </summary>
    /// <param name="fileId">The stored file identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the file was deleted.</returns>
    Task<bool> DeleteFileAsync(Guid fileId, CancellationToken ct = default);
}
