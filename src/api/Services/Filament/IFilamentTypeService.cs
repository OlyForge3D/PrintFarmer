using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Farm.Web.Api.Services.Filament
{
    public interface IFilamentTypeService
    {
        Task<IReadOnlyList<FilamentTypeDto>> GetFilamentTypesAsync(CancellationToken ct);
        Task<FilamentPresetsDto> GetFilamentPresetsAsync(CancellationToken ct);
        Task<FilamentTypeDto> CreateFilamentTypeAsync(CreateFilamentTypeRequest req, CancellationToken ct);
        Task UpdateFilamentTypeAsync(Guid id, UpdateFilamentTypeRequest req, CancellationToken ct);
        Task DeleteFilamentTypeAsync(Guid id, CancellationToken ct);
        Task SaveFilamentPresetsAsync(FilamentPresetsDto presets, CancellationToken ct);
        Task<SpoolmanFilamentImportResult> ImportFromSpoolmanAsync(CancellationToken ct);
    }
}
