using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Data.Repositories
{
    public interface IApiKeyRepository
    {
        Task<ApiKey?> GetByKeyHashAsync(string keyHash);

        /// <summary>
        /// Find an API key by its raw (unhashed) value. Used when hashing is disabled.
        /// </summary>
        Task<ApiKey?> GetByRawKeyAsync(string rawKey);

        Task<IEnumerable<ApiKey>> GetByUserIdAsync(Guid userId);

        Task<ApiKey?> GetByIdAsync(Guid id);

        Task AddAsync(ApiKey key);

        Task UpdateAsync(ApiKey key);

        Task DeleteAsync(Guid id);
    }
}
