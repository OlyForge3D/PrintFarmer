using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers
{
    [ApiController]
    [Route("api/users/{userId:guid}/apikeys")]
    public class UserApiKeysController(Farm.Web.Api.Data.Repositories.IApiKeyRepository repo) : ControllerBase
    {
        private readonly Farm.Web.Api.Data.Repositories.IApiKeyRepository _repo = repo;

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> ListApiKeysAsync([FromRoute] Guid userId)
        {
            // TODO: verify caller is same user or admin
            IEnumerable<ApiKey> keys = await _repo.GetByUserIdAsync(userId);
            IEnumerable<ApiKeyDto> result = keys.Select(k => new ApiKeyDto(
                k.Id,
                k.Name,
                k.IsActive,
                k.CreatedAt,
                k.ExpiresAt));
            return Ok(result);
        }

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> CreateApiKeyAsync([FromRoute] Guid userId, [FromBody] CreateApiKeyRequest req)
        {
            // TODO: verify caller is same user or admin
            string rawKey = GenerateKey();
            string hash = ComputeSha256Hash(rawKey);

            var key = new Farm.Infrastructure.Domain.ApiKey
            {
                UserId = userId,
                Name = req?.Name ?? "user-generated",
                KeyHash = hash,
                IsActive = true
            };

            await _repo.AddAsync(key);

            return Ok(new { key = rawKey, id = key.Id });
        }

        [HttpPatch("{keyId:guid}/toggle")]
        [Authorize]
        public async Task<IActionResult> ToggleApiKeyAsync([FromRoute] Guid userId, [FromRoute] Guid keyId)
        {
            // TODO: verify caller is same user or admin
            ApiKey? key = await _repo.GetByIdAsync(keyId);
            if (key == null || key.UserId != userId)
            {
                return NotFound();
            }

            key.IsActive = !key.IsActive;
            await _repo.UpdateAsync(key);

            return Ok(new { id = key.Id, isActive = key.IsActive });
        }

        [HttpDelete("{keyId:guid}")]
        [Authorize]
        public async Task<IActionResult> DeleteApiKeyAsync([FromRoute] Guid userId, [FromRoute] Guid keyId)
        {
            // TODO: verify caller is same user or admin
            ApiKey? key = await _repo.GetByIdAsync(keyId);
            if (key == null || key.UserId != userId)
            {
                return NotFound();
            }

            await _repo.DeleteAsync(keyId);
            return NoContent();
        }

        [HttpPost("{keyId:guid}/rotate")]
        [Authorize]
        public async Task<IActionResult> RotateApiKeyAsync([FromRoute] Guid userId, [FromRoute] Guid keyId)
        {
            // TODO: verify caller is same user or admin
            ApiKey? oldKey = await _repo.GetByIdAsync(keyId);
            if (oldKey == null || oldKey.UserId != userId)
            {
                return NotFound();
            }

            // Generate new key
            string rawKey = GenerateKey();
            string hash = ComputeSha256Hash(rawKey);

            oldKey.KeyHash = hash;
            await _repo.UpdateAsync(oldKey);

            return Ok(new { key = rawKey, id = oldKey.Id });
        }

        private static string GenerateKey()
        {
            byte[] data = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(data);
            return Convert.ToHexString(data);
        }

        private static string ComputeSha256Hash(string rawData)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] bytes = Encoding.UTF8.GetBytes(rawData);
            byte[] hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash);
        }
    }

    public record CreateApiKeyRequest(string? Name);

    public record ApiKeyDto(
        Guid Id,
        string Name,
        bool IsActive,
        DateTime CreatedAt,
        DateTime? ExpiresAt);
}
