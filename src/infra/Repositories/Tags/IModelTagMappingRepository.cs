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
        Task<IReadOnlyList<Model3DTagMapping>> GetByTagIdAsync(Guid tagId, CancellationToken ct);
        Task<Model3DTagMapping?> GetMappingAsync(Guid modelId, Guid tagId, CancellationToken ct);
        Task AddAsync(Model3DTagMapping mapping, CancellationToken ct);
        Task RemoveAsync(Model3DTagMapping mapping, CancellationToken ct);
        Task RemoveByModelIdAsync(Guid modelId, CancellationToken ct);
        Task RemoveByTagIdAsync(Guid tagId, CancellationToken ct);
        Task RemoveByModelAndTagAsync(Guid modelId, Guid tagId, CancellationToken ct);
        Task SaveChangesAsync(CancellationToken ct);

        /// <summary>
        /// Gets all model IDs in the system.
        /// </summary>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Collection of all unique model IDs</returns>
        Task<IReadOnlyCollection<Guid>> GetAllModelsAsync(CancellationToken ct);

        /// <summary>
        /// Gets models that match tag criteria.
        /// </summary>
        /// <param name="tagIds">Tag IDs to match</param>
        /// <param name="requireAll">If true, require ALL tags (AND); if false, ANY tag (OR)</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Collection of matching model IDs</returns>
        Task<IReadOnlyCollection<Guid>> GetModelsWithTagsAsync(
            IEnumerable<Guid> tagIds,
            bool requireAll,
            CancellationToken ct);

        /// <summary>
        /// Gets models that exclude specific tags.
        /// </summary>
        /// <param name="tagIds">Tag IDs to exclude</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Collection of model IDs that do NOT have any of the specified tags</returns>
        Task<IReadOnlyCollection<Guid>> GetModelsExcludingTagsAsync(
            IEnumerable<Guid> tagIds,
            CancellationToken ct);
    }
}
