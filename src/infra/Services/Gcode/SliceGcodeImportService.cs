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
/// Reads file bytes from disk and delegates to <see cref="IGcodeFileProcessingService"/>
/// for storage, metadata extraction, thumbnail extraction, and database persistence.
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
    public async Task<SliceGcodeImportResult> ImportAsync(string fileName, string fullPath, CancellationToken ct)
    {
        // Read the artifact bytes from disk.
        byte[] bytes = await File.ReadAllBytesAsync(fullPath, ct);

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
