using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Model
{
    public interface IModelRepository
    {
        Task<Model3D?> GetByIdAsync(Guid id, CancellationToken ct);
        Task<Model3D?> GetByIdWithTagsAsync(Guid id, CancellationToken ct);
        Task<Model3D?> GetByHashAsync(string fileHash, CancellationToken ct);
        Task<IReadOnlyList<Model3D>> ListValidAsync(CancellationToken ct);
        Task<int> CountValidAsync(CancellationToken ct);
        Task<IReadOnlyList<Model3D>> SearchAsync(string? query, Guid[]? tagIds, string sortBy, bool descending, int skip, int take, CancellationToken ct);
        Task AddAsync(Model3D model, CancellationToken ct);
        Task RemoveAsync(Model3D model, CancellationToken ct);
        Task UpdateAsync(Model3D model, CancellationToken ct);
        Task SaveChangesAsync(CancellationToken ct);
    }
}
