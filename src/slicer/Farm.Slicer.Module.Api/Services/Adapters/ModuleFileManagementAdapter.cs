using Farm.Slicer.Module.Services;
using ModuleIFileManagement = Farm.Slicer.Module.Services.ISlicerFileManagementService;

namespace Farm.Slicer.Module.Api.Services.Adapters;

/// <summary>
/// Bridges <see cref="Farm.Slicer.Module.Services.ISlicerFileManagementService"/> (module) to the
/// API's <see cref="ISlicerFileStorage"/> for stream-based file storage and deletion.
/// </summary>
internal sealed class ModuleFileManagementAdapter(ISlicerFileStorage fileStorage) : ModuleIFileManagement
{
    private readonly ISlicerFileStorage _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));

    /// <inheritdoc />
    public async Task<Guid> StoreFileAsync(Stream sourceStream, string fileName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceStream);

        Guid fileId = Guid.NewGuid();
        string extension = Path.GetExtension(fileName);
        string key = $"{fileId}{extension}";
        string contentType = GetContentType(extension);

        _ = await _fileStorage.UploadFileAsync(key, sourceStream, contentType, ct);

        return fileId;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteFileAsync(Guid fileId, CancellationToken ct = default)
    {
        // Try the ID as-is first; the original extension is unknown so search with just the GUID prefix.
        string key = fileId.ToString();
        bool exists = await _fileStorage.FileExistsAsync(key, ct);

        if (!exists)
        {
            // The file may have been stored with an extension — metadata lookup can resolve this.
            SlicerFileMetadata? meta = await _fileStorage.GetFileMetadataAsync(key, ct);
            if (meta is null)
            {
                return false;
            }

            key = meta.Key;
        }

        await _fileStorage.DeleteFileAsync(key, ct);
        return true;
    }

    private static string GetContentType(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".stl" => "model/stl",
            ".3mf" => "application/vnd.ms-3mfdocument",
            ".obj" => "model/obj",
            ".gcode" => "text/plain",
            _ => "application/octet-stream",
        };
}
