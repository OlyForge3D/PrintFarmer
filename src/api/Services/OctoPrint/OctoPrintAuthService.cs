using System;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services.OctoPrint;

public interface IOctoPrintAuthService
{
    Task<bool> ValidateApiKeyAsync(string? apiKey, Guid? targetPrinterId = null, Guid? userId = null);
}

public class OctoPrintAuthService(
    ISettingsService settingsService,
    ILogger<OctoPrintAuthService> logger,
    Farm.Infrastructure.Repositories.Api.IApiKeyRepository apiKeyRepo,
    IConfiguration config) : IOctoPrintAuthService
{
    private readonly ISettingsService _settingsService = settingsService;
    private readonly ILogger<OctoPrintAuthService> _logger = logger;
    private readonly Farm.Infrastructure.Repositories.Api.IApiKeyRepository _apiKeyRepo = apiKeyRepo;
    private readonly IConfiguration _config = config;

    public async Task<bool> ValidateApiKeyAsync(string? apiKey, Guid? targetPrinterId = null, Guid? userId = null)
    {
        // Read settings from database on each request so changes take effect immediately
        var settings = _settingsService.Get<OctoPrintSettings>();

        // If RequireApiKey is false, accept any (or null) apiKey.
        if (!settings.RequireApiKey)
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

        // Try raw key match first (when hashing is disabled)
        ApiKey? stored = await _apiKeyRepo.GetByRawKeyAsync(apiKey);
        if (stored is not null && !IsUsableForSlicerAuth(stored))
        {
            stored = null;
        }

        if (stored is not null)
        {
            _logger.LogInformation("OctoPrint API key validated (raw match) for user {UserId}", stored.UserId);
            return true;
        }

        // Hash the provided key and compare against stored KeyHash
        string hash = ComputeSha256Hash(apiKey);
        stored = await _apiKeyRepo.GetByKeyHashAsync(hash);
        if (stored is null || !IsUsableForSlicerAuth(stored))
        {
            _logger.LogWarning("Invalid, expired, or non-General-purpose OctoPrint API key presented (redacted)");
            return false;
        }

        _logger.LogInformation("OctoPrint API key validated for user {UserId}", stored.UserId);
        return true;
    }

    /// <summary>
    /// Slicer/OctoPrint-compatible uploads only accept General-purpose, non-expired keys.
    /// Desktop-purpose keys (see issue #837/#838) are scoped to the desktop token exchange
    /// and must never be usable here.
    /// </summary>
    private static bool IsUsableForSlicerAuth(ApiKey key)
    {
        return key.Purpose == ApiKeyPurpose.General && !key.IsExpired;
    }

    private static string ComputeSha256Hash(string rawData)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(rawData);
        byte[] hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }
}
