using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Farm.Modules.Devices.Services.OctoPrint;

public interface IOctoPrintAuthService
{
    Task<bool> ValidateApiKeyAsync(
        string? apiKey,
        bool requireValidKey = false);

    /// <summary>
    /// Resolves an <c>X-Api-Key</c> header value to a genuine <see cref="ClaimsPrincipal"/> for
    /// the key's owning user, suitable for use as an authentication result (see
    /// <see cref="Farm.Modules.Devices.Authentication.OctoPrintApiKeyAuthenticationHandler"/>). Returns
    /// <c>null</c> for a missing, invalid, expired, wrong-purpose, ownerless, or inactive-owner
    /// key. The global admin key (<c>OctoPrint:GlobalApiKey</c>) is deliberately never resolved
    /// here — it has no owning user account to build a real, ACL-checkable identity from, so it
    /// intentionally does not authenticate the print-enqueuing endpoint (see issue #1666).
    /// </summary>
    Task<ClaimsPrincipal?> ResolveApiKeyPrincipalAsync(string? apiKey, CancellationToken ct = default);
}

public class OctoPrintAuthService(
    ISettingsService settingsService,
    ILogger<OctoPrintAuthService> logger,
    Farm.Infrastructure.Repositories.Api.IApiKeyRepository apiKeyRepo,
    IUsersRepository usersRepo,
    IConfiguration config) : IOctoPrintAuthService
{
    private readonly ISettingsService _settingsService = settingsService;
    private readonly ILogger<OctoPrintAuthService> _logger = logger;
    private readonly Farm.Infrastructure.Repositories.Api.IApiKeyRepository _apiKeyRepo = apiKeyRepo;
    private readonly IUsersRepository _usersRepo = usersRepo;
    private readonly IConfiguration _config = config;

    public async Task<bool> ValidateApiKeyAsync(
        string? apiKey,
        bool requireValidKey = false)
    {
        // Read settings from database on each request so changes take effect immediately
        var settings = _settingsService.Get<OctoPrintSettings>();

        // If RequireApiKey is false, accept any (or null) apiKey.
        if (!requireValidKey && !settings.RequireApiKey)
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
            _logger.LogWarning("Invalid, expired, or non-OctoPrint-purpose API key presented (redacted)");
            return false;
        }

        _logger.LogInformation("OctoPrint API key validated for user {UserId}", stored.UserId);
        return true;
    }

    public async Task<ClaimsPrincipal?> ResolveApiKeyPrincipalAsync(string? apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        // The global admin key has no owning user account. Building a farm_admin-role
        // principal for it here would give QueueActorIdentity.Resolve a fabricated Guid
        // that doesn't correspond to a real user, which would either fail unpredictably
        // against DB-backed checks (e.g. JobQueueService.IsFarmAdminAsync) or, if it
        // happened to collide, silently bypass the exact per-user PrinterGroup ACL this
        // fix exists to enforce. Fail closed instead: this key no longer authenticates
        // the print-enqueuing endpoint. It remains valid for /api/version and /api/server
        // via the legacy ValidateApiKeyAsync path above.
        string? globalKey = _config["OctoPrint:GlobalApiKey"];
        if (!string.IsNullOrEmpty(globalKey) && string.Equals(globalKey, apiKey, StringComparison.Ordinal))
        {
            _logger.LogWarning("Global OctoPrint API key presented for an authenticated endpoint; rejecting (no owning user to authorize against).");
            return null;
        }

        // Try raw key match first (when hashing is disabled), then hashed lookup.
        ApiKey? stored = await _apiKeyRepo.GetByRawKeyAsync(apiKey);
        if (stored is not null && !IsUsableForSlicerAuth(stored))
        {
            stored = null;
        }

        if (stored is null)
        {
            string hash = ComputeSha256Hash(apiKey);
            stored = await _apiKeyRepo.GetByKeyHashAsync(hash);
            if (stored is not null && !IsUsableForSlicerAuth(stored))
            {
                stored = null;
            }
        }

        if (stored is null || stored.UserId is null)
        {
            _logger.LogWarning("Invalid, expired, non-OctoPrint-purpose, or ownerless API key presented (redacted)");
            return null;
        }

        User? owner = await _usersRepo.GetUserEntityAsync(stored.UserId.Value, ct);
        if (owner is null || !owner.IsActive)
        {
            _logger.LogWarning("OctoPrint API key belongs to a missing or inactive user {UserId}", stored.UserId);
            return null;
        }

        List<string> roles = await _usersRepo.GetActiveRoleNamesAsync(owner.Id, ct);
        List<(string Resource, string Action)> granted = await _usersRepo.GetGrantedPermissionsAsync(owner.Id, ct);
        List<(string Resource, string Action)> denied = await _usersRepo.GetDeniedPermissionsAsync(owner.Id, ct);

        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, owner.Id.ToString()),
            new(ClaimTypes.Name, owner.Username),
        ];
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(granted.Select(p => new Claim(PrintFarmerPermissions.ClaimType, $"{p.Resource}:{p.Action}")));

        // Explicit deny claims keep the resource:admin implication from silently overriding
        // a per-action deny — mirrors AuthenticationService.GenerateJwtTokenAsync.
        claims.AddRange(denied.Select(p => new Claim(PrintFarmerPermissions.DenyClaimType, $"{p.Resource}:{p.Action}")));

        _logger.LogInformation("OctoPrint API key authenticated for user {UserId}", owner.Id);

        var identity = new ClaimsIdentity(claims, OctoPrintApiKeyAuthenticationSchemeName);
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Local copy of the OctoPrintApiKey authentication scheme name. Kept as a plain string
    /// (rather than referencing Farm.Modules.Devices.Authentication.OctoPrintApiKeyDefaults directly)
    /// to avoid a dependency from this service namespace onto the Authentication namespace;
    /// the two must stay in sync, which
    /// <c>OctoPrintApiKeyAuthenticationHandlerTests</c> guards against drifting.
    /// </summary>
    private const string OctoPrintApiKeyAuthenticationSchemeName = "OctoPrintApiKey";

    /// <summary>
    /// Slicer/OctoPrint-compatible uploads only accept OctoPrint-purpose, non-expired keys.
    /// Desktop-purpose keys (see issue #837/#838) are scoped to the desktop token exchange
    /// and must never be usable here.
    /// </summary>
    private static bool IsUsableForSlicerAuth(ApiKey key)
    {
        return key.Purpose == ApiKeyPurpose.OctoPrint && !key.IsExpired;
    }

    private static string ComputeSha256Hash(string rawData)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(rawData);
        byte[] hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }
}
