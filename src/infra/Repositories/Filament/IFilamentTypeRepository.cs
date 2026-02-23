using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Filament;

/// <summary>
/// Repository for managing filament type definitions and presets.
/// </summary>
public interface IFilamentTypeRepository
{
    /// <summary>Gets all filament types as DTOs.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<FilamentTypeDto>> GetFilamentTypesAsync(CancellationToken ct = default);

    /// <summary>Gets a paged, optionally filtered list of filament types.</summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Items per page.</param>
    /// <param name="search">Optional case-insensitive name filter.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PagedResult<FilamentTypeDto>> GetPagedAsync(int page, int pageSize, string? search = null, CancellationToken ct = default);

    /// <summary>Gets filament presets including bed temperatures, print temperatures, and enclosure requirements.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<FilamentPresetsDto> GetFilamentPresetsAsync(CancellationToken ct = default);

    /// <summary>Adds a new filament type.</summary>
    /// <param name="ft">The filament type entity to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddFilamentTypeAsync(FilamentType ft, CancellationToken ct = default);

    /// <summary>Gets a filament type by ID as a DTO.</summary>
    /// <param name="id">The filament type identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FilamentTypeDto?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Updates an existing filament type.</summary>
    /// <param name="ft">The filament type entity with updates.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateFilamentTypeAsync(FilamentType ft, CancellationToken ct = default);

    /// <summary>Deletes a filament type by ID.</summary>
    /// <param name="id">The filament type identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteFilamentTypeAsync(Guid id, CancellationToken ct = default);

    /// <summary>Saves pending changes to the database.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task SaveChangesAsync(CancellationToken ct = default);

    /// <summary>Gets the raw filament type entity for modification.</summary>
    /// <param name="id">The filament type identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FilamentType?> GetEntityByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Gets a filament type by name.</summary>
    /// <param name="name">The filament type name to find.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FilamentType?> GetByNameAsync(string name, CancellationToken ct = default);
}
