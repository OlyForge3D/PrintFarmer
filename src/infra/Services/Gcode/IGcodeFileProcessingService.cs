using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Gcode;

/// <summary>
/// Service for processing and storing G-code files from any source (upload, slice import, etc).
/// Handles file storage, metadata extraction, thumbnail processing, and entity persistence.
///
/// This interface lets Infrastructure services (for example the slice G-code import) coordinate
/// with file processing logic without creating a circular dependency on the API layer.
/// </summary>
public interface IGcodeFileProcessingService
{
    /// <summary>
    /// Unified method for processing and storing G-code files from any source
    /// (upload, slice import, or future sources).
    ///
    /// Handles all file processing: storage, hash calculation, duplicate detection,
    /// metadata extraction, thumbnail processing, entity creation, and database persistence.
    /// </summary>
    /// <param name="fileContent">The raw file content bytes</param>
    /// <param name="originalFileName">Original filename as provided by source</param>
    /// <param name="folderId">Virtual folder ID where file should be organized</param>
    /// <param name="virtualDirectory">Virtual directory path (e.g., '/', '/subfolder'). Defaults to '/'.</param>
    /// <param name="fileId">Optional specific ID for the file. If null, a new GUID is generated.</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The saved GcodeFile entity with all metadata populated</returns>
    /// <remarks>
    /// This method consolidates all file handling logic:
    /// 1. Stores file to disk with GUID-based filename
    /// 2. Calculates SHA256 hash and checks for duplicates
    /// 3. Extracts metadata from G-code (slicer, temps, filament, etc.)
    /// 4. Processes and extracts thumbnail image
    /// 5. Creates GcodeFile entity with complete metadata
    /// 6. Saves to database
    ///
    /// If a duplicate file is detected (same hash), an exception is thrown allowing
    /// the caller to decide whether to skip, replace, or re-import.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown if a duplicate file already exists</exception>
    Task<GcodeFile> ProcessAndStoreGcodeFileAsync(
        byte[] fileContent,
        string originalFileName,
        Guid folderId,
        string? virtualDirectory = null,
        Guid? fileId = null,
        CancellationToken ct = default);
}
