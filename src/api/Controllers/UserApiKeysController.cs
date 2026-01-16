using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers
{
    [ApiController]
    [Route("api/users/{userId:guid}/apikeys")]
    public class UserApiKeysController : ControllerBase
    {
        private readonly Farm.Web.Api.Data.Repositories.IApiKeyRepository _repo;

        public UserApiKeysController(Farm.Web.Api.Data.Repositories.IApiKeyRepository repo)
        {
            _repo = repo;
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
}
