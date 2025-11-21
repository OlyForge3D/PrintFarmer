using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Tags
{
    public interface IModelTagMappingRepository
    {
        Task<Model3DTagMapping?> GetByIdAsync(Guid id, CancellationToken ct);
        Task<IReadOnlyList<Model3DTagMapping>> GetByModelIdAsync(Guid modelId, CancellationToken ct);
        Task<Model3DTagMapping?> GetMappingAsync(Guid modelId, Guid tagId, CancellationToken ct);
        Task AddAsync(Model3DTagMapping mapping, CancellationToken ct);
        Task RemoveAsync(Model3DTagMapping mapping, CancellationToken ct);
        Task RemoveByModelIdAsync(Guid modelId, CancellationToken ct);
        Task RemoveByModelAndTagAsync(Guid modelId, Guid tagId, CancellationToken ct);
        Task SaveChangesAsync(CancellationToken ct);
    }
}
