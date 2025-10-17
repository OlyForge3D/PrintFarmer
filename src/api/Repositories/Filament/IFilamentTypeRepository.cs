using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Shared;

namespace Farm.Web.Api.Repositories.Filament;

public interface IFilamentTypeRepository
{
    Task<IReadOnlyList<Shared.FilamentTypeDto>> GetFilamentTypesAsync(CancellationToken ct = default);
    Task<Shared.FilamentPresetsDto> GetFilamentPresetsAsync(CancellationToken ct = default);
    Task AddFilamentTypeAsync(Farm.Infrastructure.Domain.FilamentType ft, CancellationToken ct = default);
    Task<Shared.FilamentTypeDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task UpdateFilamentTypeAsync(Farm.Infrastructure.Domain.FilamentType ft, CancellationToken ct = default);
    Task DeleteFilamentTypeAsync(Guid id, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
    Task<Farm.Infrastructure.Domain.FilamentType?> GetEntityByIdAsync(Guid id, CancellationToken ct = default);
    Task<Farm.Infrastructure.Domain.FilamentType?> GetByNameAsync(string name, CancellationToken ct = default);
}
