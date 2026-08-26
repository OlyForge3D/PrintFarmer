namespace Farm.Infrastructure.Services.Interfaces;

/// <summary>
/// Narrow, module-safe seam onto the single G-code file deletion capability that
/// <c>Farm.Web.Api.Services.Gcode.IGcodeFilesService</c> (which stays host-resident, since its
/// full surface pulls in many Farm.Web.Api-only DTOs) exposes to callers outside the host, such as
/// <c>Farm.Modules.PrintQueue</c>'s slice-to-print bridge, without giving them a compile-time
/// reference back to the host (issue #2040).
/// </summary>
public interface IGcodeFileDeleter
{
    /// <summary>
    /// Deletes a G-code file from the library.
    /// </summary>
    /// <param name="id">Unique identifier of the file to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if deletion succeeded, false if file not found or cannot be deleted.</returns>
    Task<bool> DeleteFileAsync(Guid id, CancellationToken ct);
}
