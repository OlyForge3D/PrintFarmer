using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure;

namespace Farm.Infrastructure.Repositories.Filament;

public interface IFilamentTypeRepository
{
    Task<IReadOnlyList<FilamentTypeDto>> GetFilamentTypesAsync(CancellationToken ct = default);
    Task<FilamentPresetsDto> GetFilamentPresetsAsync(CancellationToken ct = default);
    Task AddFilamentTypeAsync(FilamentType ft, CancellationToken ct = default);
    Task<FilamentTypeDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task UpdateFilamentTypeAsync(FilamentType ft, CancellationToken ct = default);
    Task DeleteFilamentTypeAsync(Guid id, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
    Task<FilamentType?> GetEntityByIdAsync(Guid id, CancellationToken ct = default);
    Task<FilamentType?> GetByNameAsync(string name, CancellationToken ct = default);
}
