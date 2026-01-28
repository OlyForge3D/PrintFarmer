using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Farm.Web.Api.Services.Filament
{
    /// <summary>
    /// Service for managing filament types and material presets.
    /// </summary>
    public interface IFilamentTypeService
    {
        /// <summary>Gets all available filament types.</summary>
        Task<IReadOnlyList<FilamentTypeDto>> GetFilamentTypesAsync(CancellationToken ct);

        /// <summary>Gets filament presets for temperature recommendations.</summary>
        Task<FilamentPresetsDto> GetFilamentPresetsAsync(CancellationToken ct);

        /// <summary>Creates a new filament type.</summary>
        Task<FilamentTypeDto> CreateFilamentTypeAsync(CreateFilamentTypeRequest req, CancellationToken ct);

        /// <summary>Updates an existing filament type.</summary>
        Task UpdateFilamentTypeAsync(Guid id, UpdateFilamentTypeRequest req, CancellationToken ct);

        /// <summary>Deletes a filament type by ID.</summary>
        Task DeleteFilamentTypeAsync(Guid id, CancellationToken ct);

        /// <summary>Saves filament temperature presets.</summary>
        Task SaveFilamentPresetsAsync(FilamentPresetsDto presets, CancellationToken ct);

        /// <summary>Imports filament types from connected Spoolman instance.</summary>
        Task<SpoolmanFilamentImportResult> ImportFromSpoolmanAsync(CancellationToken ct);
    }
}
