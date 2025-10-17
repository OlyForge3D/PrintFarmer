using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shared = Farm.Web.Shared;

namespace Farm.Web.Api.Services.Filament
{
    public interface IFilamentTypeService
    {
        Task<IReadOnlyList<Shared.FilamentTypeDto>> GetFilamentTypesAsync(CancellationToken ct);
        Task<Shared.FilamentPresetsDto> GetFilamentPresetsAsync(CancellationToken ct);
        Task<Shared.FilamentTypeDto> CreateFilamentTypeAsync(Shared.CreateFilamentTypeRequest req, CancellationToken ct);
        Task UpdateFilamentTypeAsync(System.Guid id, Shared.UpdateFilamentTypeRequest req, CancellationToken ct);
        Task DeleteFilamentTypeAsync(System.Guid id, CancellationToken ct);
        Task SaveFilamentPresetsAsync(Shared.FilamentPresetsDto presets, CancellationToken ct);
        Task<Shared.SpoolmanFilamentImportResult> ImportFromSpoolmanAsync(CancellationToken ct);
    }
}
