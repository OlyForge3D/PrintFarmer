using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.FolderManagement;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Gcode;

/// <summary>
/// Imports a completed slicer gcode artifact into the main-app GcodeFile library.
/// Reads bytes from a caller-supplied stream (rather than resolving a filesystem path
/// itself) and delegates to <see cref="IGcodeFileProcessingService"/> for storage,
/// metadata extraction, thumbnail extraction, and database persistence.
/// </summary>
public sealed class SliceGcodeImportService(
    IGcodeFileProcessingService gcodeProcessingService,
    IFolderManagementService folderService,
    ILogger<SliceGcodeImportService> logger) : ISliceGcodeImportService
{
    private readonly IGcodeFileProcessingService _gcodeProcessingService =
        gcodeProcessingService ?? throw new ArgumentNullException(nameof(gcodeProcessingService));

    private readonly IFolderManagementService _folderService =
        folderService ?? throw new ArgumentNullException(nameof(folderService));

    private readonly ILogger<SliceGcodeImportService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<SliceGcodeImportResult> ImportAsync(string fileName, Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Read the artifact bytes from the caller-supplied stream. The caller (typically
        // IArtifactsService.OpenReadStreamAsync) is responsible for opening this stream from
        // the artifact's storage location, so there is no separate path resolution here that
        // could go stale between resolution and read.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        byte[] bytes = buffer.ToArray();

        // Resolve or create the root gcode virtual folder for organisation.
        FolderNode folder = await _folderService.GetOrCreateFolderAsync("/", "gcode", ct);

        try
        {
            GcodeFile gcodeFile = await _gcodeProcessingService.ProcessAndStoreGcodeFileAsync(
                fileContent: bytes,
                originalFileName: fileName,
                folderId: folder.Id,
                virtualDirectory: "/",
                sourcePrinterId: null,
                originalPrinterPath: null,
                thumbnailUrl: null,
                fileId: null,
                ct: ct);

            _logger.LogInformation(
                "Imported slice gcode {FileName} as GcodeFile {GcodeFileId} ({Bytes} bytes)",
                fileName, gcodeFile.Id, bytes.Length);

            return new SliceGcodeImportResult(gcodeFile.Id, IsNewFile: true);
        }
        catch (DuplicateFileException ex) when (
            ex.ExistingFileId is not null
            && Guid.TryParse(ex.ExistingFileId, out Guid existingId))
        {
            // Slice was already imported (same hash). Re-use the existing GcodeFile.
            _logger.LogInformation(
                "Slice gcode {FileName} already exists as GcodeFile {ExistingId}; reusing",
                fileName, existingId);
            return new SliceGcodeImportResult(existingId, IsNewFile: false);
        }
    }
}
