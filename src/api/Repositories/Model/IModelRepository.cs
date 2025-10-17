using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using System.Collections.Generic;

namespace Farm.Web.Api.Repositories.Model
{
    public interface IModelRepository
    {
        Task<Model3D?> GetByIdAsync(Guid id, CancellationToken ct);
        Task<Model3D?> GetByHashAsync(string fileHash, CancellationToken ct);
        Task<IReadOnlyList<Model3D>> ListValidAsync(CancellationToken ct);
        Task AddAsync(Model3D model, CancellationToken ct);
        Task RemoveAsync(Model3D model, CancellationToken ct);
        Task SaveChangesAsync(CancellationToken ct);
    }
}
