using ModuleIStoredFileOps = Farm.Slicer.Module.Services.IStoredFileOperationsService;
using Farm.Slicer.Module.Services;

namespace Farm.Web.Api.Services.Adapters;

/// <summary>
/// Bridges <see cref="Farm.Slicer.Module.Services.IStoredFileOperationsService"/> (module) to the
/// API's <see cref="ISlicerFileStorage"/> for GUID-based file path resolution and stream reading.
/// </summary>
internal sealed class ModuleStoredFileOpsAdapter(ISlicerFileStorage fileStorage) : ModuleIStoredFileOps
{
    private readonly ISlicerFileStorage _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));

    /// <inheritdoc />
    public async Task<string?> GetFilePathAsync(Guid fileId, CancellationToken ct = default)
    {
        string key = fileId.ToString();
        SlicerFileMetadata? meta = await _fileStorage.GetFileMetadataAsync(key, ct);

        if (meta is not null)
        {
            return meta.CustomMetadata.TryGetValue("FilePath", out string? path) ? path : null;
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<Stream?> ReadFileAsync(Guid fileId, CancellationToken ct = default)
    {
        string key = fileId.ToString();
        bool exists = await _fileStorage.FileExistsAsync(key, ct);

        if (!exists)
        {
            return null;
        }

        return await _fileStorage.DownloadFileAsync(key, ct);
    }
}
