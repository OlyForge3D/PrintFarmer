using System;
using System.Threading;
using System.Threading.Tasks;

namespace Farm.Infrastructure.Services.Gcode;

/// <summary>
/// Bridges the slicer artifact storage and the main-app GcodeFile library.
/// Imports a completed slice gcode artifact into the GcodeFile library
/// so that it can be referenced by a print queue job.
/// </summary>
public interface ISliceGcodeImportService
{
    /// <summary>
    /// Reads the gcode bytes from <paramref name="fullPath"/>, stores them using the
    /// standard GcodeFile storage and metadata-extraction pipeline, and returns the
    /// resulting GcodeFile ID.
    /// </summary>
    /// <remarks>
    /// If the same bytes were already imported (duplicate hash), the existing GcodeFile's
    /// ID is returned instead of creating a duplicate record.
    /// </remarks>
    /// <param name="fileName">Original artifact filename (e.g. "model.gcode").</param>
    /// <param name="fullPath">Absolute path to the artifact file on disk.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The GcodeFileId (new or pre-existing on duplicate).</returns>
    Task<Guid> ImportAsync(string fileName, string fullPath, CancellationToken ct);
}
