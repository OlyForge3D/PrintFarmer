using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Tags
{
    public interface ITagRepository
    {
        Task<Tag?> GetByIdAsync(Guid id, CancellationToken ct);
        Task<Tag?> GetByNameAsync(string name, CancellationToken ct);
        Task<IReadOnlyList<Tag>> ListAllAsync(CancellationToken ct);
        Task AddAsync(Tag tag, CancellationToken ct);
        Task RemoveAsync(Tag tag, CancellationToken ct);
        Task SaveChangesAsync(CancellationToken ct);
    }
}
