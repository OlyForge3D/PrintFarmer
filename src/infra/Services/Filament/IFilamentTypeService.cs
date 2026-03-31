using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Farm.Infrastructure.Services.Filament;

/// <summary>
/// Service for managing filament types and material presets.
/// </summary>
public interface IFilamentTypeService
{
    /// <summary>Gets all available filament types.</summary>
    Task<IReadOnlyList<FilamentTypeDto>> GetFilamentTypesAsync(CancellationToken ct);

    /// <summary>Gets a paged, optionally filtered list of filament types.</summary>
    Task<PagedResult<FilamentTypeDto>> GetPagedFilamentTypesAsync(int page, int pageSize, string? search, CancellationToken ct);

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

    /// <summary>Exports all filament types to CSV format.</summary>
    Task<byte[]> ExportToCsvAsync(CancellationToken ct);

    /// <summary>Imports filament types from a CSV stream with upsert logic.</summary>
    Task<FilamentCsvImportResult> ImportFromCsvAsync(Stream csvStream, CancellationToken ct);

    /// <summary>Imports selected filaments from SpoolmanDB community database.</summary>
    Task<SpoolmanDbImportResult> ImportFromSpoolmanDbAsync(SpoolmanDbImportRequest request, IReadOnlyList<SpoolmanDbFilamentEntry> allFilaments, CancellationToken ct);

    /// <summary>Imports selected entries from the Open Filament Database.</summary>
    Task<Farm.Infrastructure.OpenFilamentDb.OfdImportResult> ImportFromOpenFilamentDbAsync(IReadOnlyList<Farm.Infrastructure.OpenFilamentDb.OfdFlattenedEntry> entries, CancellationToken ct);

    /// <summary>Syncs all external materials from Spoolman's SpoolmanDB endpoint as filament types (upsert).</summary>
    Task<SpoolmanDbImportResult> SyncExternalMaterialsAsync(IReadOnlyList<SpoolmanDbMaterialEntry> materials, CancellationToken ct);
}
