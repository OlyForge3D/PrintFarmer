using System;
using System.Threading;
using System.Threading.Tasks;

namespace Farm.Infrastructure.Services.Gcode;

/// <summary>
/// Result returned by <see cref="ISliceGcodeImportService.ImportAsync"/>.
/// Carries the GcodeFile ID and a flag indicating whether a new record was
/// created or an existing one was reused due to a duplicate content hash.
/// </summary>
/// <param name="FileId">The GcodeFile identifier (new or pre-existing).</param>
/// <param name="IsNewFile">
/// <see langword="true"/> when a new GcodeFile record was created;
/// <see langword="false"/> when an existing record was reused due to a duplicate hash.
/// </param>
public readonly record struct SliceGcodeImportResult(Guid FileId, bool IsNewFile);

/// <summary>
/// Bridges the slicer artifact storage and the main-app GcodeFile library.
/// Imports a completed slice gcode artifact into the GcodeFile library
/// so that it can be referenced by a print queue job.
/// </summary>
public interface ISliceGcodeImportService
{
    /// <summary>
    /// Reads the gcode bytes from <paramref name="fullPath"/>, stores them using the
    /// standard GcodeFile storage and metadata-extraction pipeline, and returns a
    /// <see cref="SliceGcodeImportResult"/> with the GcodeFile ID and whether the file
    /// was newly created or reused from a previous import with the same content hash.
    /// </summary>
    /// <remarks>
    /// If the same bytes were already imported (duplicate hash), the existing GcodeFile's
    /// ID is returned with <see cref="SliceGcodeImportResult.IsNewFile"/> set to
    /// <see langword="false"/> instead of creating a duplicate record.
    /// </remarks>
    /// <param name="fileName">Original artifact filename (e.g. "model.gcode").</param>
    /// <param name="fullPath">Absolute path to the artifact file on disk.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="SliceGcodeImportResult"/> containing the GcodeFile ID and whether
    /// a new record was created (<see langword="true"/>) or an existing record was reused
    /// due to a duplicate content hash (<see langword="false"/>).
    /// </returns>
    Task<SliceGcodeImportResult> ImportAsync(string fileName, string fullPath, CancellationToken ct);
}
