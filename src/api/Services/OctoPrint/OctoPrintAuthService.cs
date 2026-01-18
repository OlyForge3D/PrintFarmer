using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Services.OctoPrint
{
    public interface IOctoPrintAuthService
    {
        Task<bool> ValidateApiKeyAsync(string? apiKey, Guid? targetPrinterId = null, Guid? userId = null);
    }

    public class OctoPrintAuthService(IOptions<OctoPrintSettings> options, ILogger<OctoPrintAuthService> logger, Farm.Web.Api.Data.Repositories.IApiKeyRepository apiKeyRepo, IConfiguration config) : IOctoPrintAuthService
    {
        private readonly OctoPrintSettings _settings = options.Value;
        private readonly ILogger<OctoPrintAuthService> _logger = logger;
        private readonly Farm.Web.Api.Data.Repositories.IApiKeyRepository _apiKeyRepo = apiKeyRepo;
        private readonly IConfiguration _config = config;

        public async Task<bool> ValidateApiKeyAsync(string? apiKey, Guid? targetPrinterId = null, Guid? userId = null)
        {
            // TODO: Implement DB-backed validation against per-user, per-printer and global keys.
            // For now, if RequireApiKey is false, accept any (or null) apiKey.
            if (!_settings.RequireApiKey)
            {
                _logger.LogDebug("OctoPrint API key validation disabled in settings.");
                return true;
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogWarning("Missing X-Api-Key header while requirement is enabled.");
                return false;
            }
            // Check for global admin key from configuration (appsettings or secret)
            string? globalKey = _config["OctoPrint:GlobalApiKey"];
            if (!string.IsNullOrEmpty(globalKey) && string.Equals(globalKey, apiKey, StringComparison.Ordinal))
            {
                _logger.LogInformation("Authenticated with global OctoPrint API key (redacted)");
                return true;
            }

            // Hash the provided key and compare against stored KeyHash
            string hash = ComputeSha256Hash(apiKey);
            var stored = await _apiKeyRepo.GetByKeyHashAsync(hash);
            if (stored is null)
            {
                _logger.LogWarning("Invalid OctoPrint API key presented (redacted)");
                return false;
            }

            _logger.LogInformation("OctoPrint API key validated for user {UserId}", stored.UserId);
            return true;
        }

        private static string ComputeSha256Hash(string rawData)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(rawData);
            byte[] hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash);
        }
    }
}
