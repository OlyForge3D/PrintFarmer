using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Tags
{
    public interface ITagRepository
    {
        Task<Model3DTag?> GetByIdAsync(Guid id, CancellationToken ct);
        Task<Model3DTag?> GetByNameAsync(string name, CancellationToken ct);
        Task<IReadOnlyList<Model3DTag>> ListAllAsync(CancellationToken ct);
        Task AddAsync(Model3DTag tag, CancellationToken ct);
        Task RemoveAsync(Model3DTag tag, CancellationToken ct);
        Task SaveChangesAsync(CancellationToken ct);
    }
}
