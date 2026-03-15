using System;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Services.OctoPrint;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers
{
    [ApiController]
    [Route("api/users/{userId:guid}/apikeys")]
    public class UserApiKeysController : ControllerBase
    {
        private readonly Farm.Infrastructure.Repositories.Api.IApiKeyRepository _repo;
        private readonly ISettingsService _settingsService;

        public UserApiKeysController(
            Farm.Infrastructure.Repositories.Api.IApiKeyRepository repo,
            ISettingsService settingsService)
        {
            _repo = repo;
            _settingsService = settingsService;
        }

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> ListApiKeysAsync([FromRoute] Guid userId)
        {
            if (!IsCallerAuthorized(userId))
            {
                return Forbid();
            }

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
            if (!IsCallerAuthorized(userId))
            {
                return Forbid();
            }

            OctoPrintSettings settings = _settingsService.Get<OctoPrintSettings>();
            string rawKey = GenerateKey();
            string storedValue = settings.HashStoredApiKeys ? ComputeSha256Hash(rawKey) : rawKey;

            var key = new Farm.Infrastructure.Domain.ApiKey
            {
                UserId = userId,
                Name = req?.Name ?? "user-generated",
                KeyHash = storedValue,
                IsActive = true
            };

            await _repo.AddAsync(key);

            return Ok(new { key = rawKey, id = key.Id });
        }

        [HttpPatch("{keyId:guid}/toggle")]
        [Authorize]
        public async Task<IActionResult> ToggleApiKeyAsync([FromRoute] Guid userId, [FromRoute] Guid keyId)
        {
            if (!IsCallerAuthorized(userId))
            {
                return Forbid();
            }

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
            if (!IsCallerAuthorized(userId))
            {
                return Forbid();
            }

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
            if (!IsCallerAuthorized(userId))
            {
                return Forbid();
            }

            ApiKey? oldKey = await _repo.GetByIdAsync(keyId);
            if (oldKey == null || oldKey.UserId != userId)
            {
                return NotFound();
            }

            // Generate new key
            OctoPrintSettings settings = _settingsService.Get<OctoPrintSettings>();
            string rawKey = GenerateKey();
            string storedValue = settings.HashStoredApiKeys ? ComputeSha256Hash(rawKey) : rawKey;

            oldKey.KeyHash = storedValue;
            await _repo.UpdateAsync(oldKey);

            return Ok(new { key = rawKey, id = oldKey.Id });
        }

        /// <summary>
        /// Reveal the raw API key. Only works when HashStoredApiKeys is disabled.
        /// </summary>
        [HttpGet("{keyId:guid}/reveal")]
        [Authorize]
        public async Task<IActionResult> RevealApiKeyAsync([FromRoute] Guid userId, [FromRoute] Guid keyId)
        {
            if (!IsCallerAuthorized(userId))
            {
                return Forbid();
            }

            OctoPrintSettings settings = _settingsService.Get<OctoPrintSettings>();
            if (settings.HashStoredApiKeys)
            {
                return BadRequest(new { error = "Cannot reveal API key when hashing is enabled. Keys are stored as one-way hashes." });
            }

            ApiKey? key = await _repo.GetByIdAsync(keyId);
            if (key == null || key.UserId != userId)
            {
                return NotFound();
            }

            // When hashing is disabled, KeyHash contains the raw key
            return Ok(new { key = key.KeyHash });
        }

        /// <summary>
        /// Get the current API key settings (whether hashing is enabled).
        /// </summary>
        [HttpGet("/api/apikeys/settings")]
        [Authorize]
        public IActionResult GetApiKeySettings()
        {
            OctoPrintSettings settings = _settingsService.Get<OctoPrintSettings>();
            return Ok(new { hashingEnabled = settings.HashStoredApiKeys });
        }

        private bool IsCallerAuthorized(Guid targetUserId)
        {
            string? callerIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(callerIdStr))
            {
                return false;
            }

            if (Guid.TryParse(callerIdStr, out Guid callerId) && callerId == targetUserId)
            {
                return true;
            }

            return User.IsInRole("Admin") || User.IsInRole("Administrator");
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
