using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Data.Repositories
{
    /// <summary>
    /// Repository for managing API key persistence and retrieval.
    /// </summary>
    public interface IApiKeyRepository
    {
        /// <summary>
        /// Gets an API key by its hashed value.
        /// </summary>
        /// <param name="keyHash">The hashed API key value.</param>
        /// <returns>The API key if found; otherwise null.</returns>
        Task<ApiKey?> GetByKeyHashAsync(string keyHash);

        /// <summary>
        /// Find an API key by its raw (unhashed) value. Used when hashing is disabled.
        /// </summary>
        Task<ApiKey?> GetByRawKeyAsync(string rawKey);

        /// <summary>
        /// Gets all API keys for a specific user.
        /// </summary>
        /// <param name="userId">The user ID to get keys for.</param>
        /// <returns>All API keys belonging to the user.</returns>
        Task<IEnumerable<ApiKey>> GetByUserIdAsync(Guid userId);

        /// <summary>
        /// Gets an API key by its unique identifier.
        /// </summary>
        /// <param name="id">The API key ID.</param>
        /// <returns>The API key if found; otherwise null.</returns>
        Task<ApiKey?> GetByIdAsync(Guid id);

        /// <summary>
        /// Adds a new API key to the repository.
        /// </summary>
        /// <param name="key">The API key to add.</param>
        Task AddAsync(ApiKey key);

        /// <summary>
        /// Updates an existing API key.
        /// </summary>
        /// <param name="key">The API key to update.</param>
        Task UpdateAsync(ApiKey key);

        /// <summary>
        /// Deletes an API key by its ID.
        /// </summary>
        /// <param name="id">The ID of the API key to delete.</param>
        Task DeleteAsync(Guid id);
    }
}
