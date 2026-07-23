using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Api;
using Farm.Infrastructure.Repositories.Users;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Farm.Infrastructure.Services.Authentication;

/// <summary>
/// Default <see cref="IApiKeyExchangeService"/> implementation. Reuses the same JWT
/// signing key/issuer/audience configuration as <see cref="AuthenticationService"/>'s
/// login tokens so exchanged tokens validate identically on both the main API and the
/// slicer host, but issues a much shorter-lived, minimally scoped token: no role claims,
/// no permission claims, only the exchanged key's explicit <see cref="ApiKeyScope"/> flags.
/// </summary>
public class ApiKeyExchangeService(
    IApiKeyRepository apiKeyRepository,
    IUsersRepository usersRepository,
    IAuthAuditService auditService,
    IConfiguration configuration,
    ILogger<ApiKeyExchangeService> logger) : IApiKeyExchangeService
{
    private const string GenericError = "Invalid API key";

    private readonly IApiKeyRepository _apiKeyRepository = apiKeyRepository;
    private readonly IUsersRepository _usersRepository = usersRepository;
    private readonly IAuthAuditService _auditService = auditService;
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<ApiKeyExchangeService> _logger = logger;

    public async Task<ApiKeyExchangeResult> ExchangeApiKeyAsync(string rawApiKey, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawApiKey))
        {
            return await FailAsync("missing_key", ipAddress, userAgent, ct);
        }

        string keyHash = ComputeSha256Hash(rawApiKey);
        ApiKey? apiKey = await _apiKeyRepository.GetByKeyHashAsync(keyHash);
        if (apiKey is null)
        {
            // Covers not-found, revoked (IsActive=false), and expired - the repository
            // query already filters all three at the DB level.
            return await FailAsync("key_not_found_or_inactive", ipAddress, userAgent, ct);
        }

        if (apiKey.Purpose != ApiKeyPurpose.Desktop)
        {
            return await FailAsync("wrong_purpose", ipAddress, userAgent, ct);
        }

        if (apiKey.Scopes == ApiKeyScope.None)
        {
            return await FailAsync("no_scopes_granted", ipAddress, userAgent, ct);
        }

        if (apiKey.UserId is null)
        {
            return await FailAsync("key_has_no_owner", ipAddress, userAgent, ct);
        }

        User? owner = await _usersRepository.GetUserEntityAsync(apiKey.UserId.Value, ct);
        if (owner is null || !owner.IsActive)
        {
            return await FailAsync("owner_inactive_or_missing", ipAddress, userAgent, ct);
        }

        string? rawKey = _configuration["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(rawKey) || rawKey.Length < 32)
        {
            _logger.LogError("JWT key is missing or too short. Minimum 32 characters recommended.");
            return await FailAsync("server_misconfigured", ipAddress, userAgent, ct);
        }

        List<string> scopeNames = ExpandScopes(apiKey.Scopes);

        int lifetimeMinutes = 15;
        string? configuredLifetime = _configuration["Jwt:DesktopExchangeLifetimeMinutes"];
        if (!string.IsNullOrWhiteSpace(configuredLifetime) && int.TryParse(configuredLifetime, out int parsedLifetime) && parsedLifetime > 0)
        {
            lifetimeMinutes = parsedLifetime;
        }

        DateTime expiresAt = DateTime.UtcNow.AddMinutes(lifetimeMinutes);

#pragma warning disable S6781
        SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes(rawKey));
#pragma warning restore S6781
        SigningCredentials creds = new(key, SecurityAlgorithms.HmacSha256);

        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, owner.Id.ToString()),
            new(ClaimTypes.Name, owner.Username),
            new(DesktopScopeClaims.TokenUse, DesktopScopeClaims.DesktopExchangeTokenUse),
            new(DesktopScopeClaims.ApiKeyId, apiKey.Id.ToString())
        ];
        claims.AddRange(scopeNames.Select(s => new Claim(DesktopScopeClaims.Scope, s)));

        SecurityTokenDescriptor tokenDescriptor = new()
        {
            Subject = new ClaimsIdentity(claims),
            Expires = expiresAt,
            Issuer = _configuration["Jwt:Issuer"],
            Audience = _configuration["Jwt:Audience"],
            SigningCredentials = creds
        };

        JsonWebTokenHandler handler = new();
        string token = handler.CreateToken(tokenDescriptor);

        await _auditService.LogApiKeyExchangeAsync(owner.Id, apiKey.Id, ipAddress, userAgent, cancellationToken: ct);

        return new ApiKeyExchangeResult(true, token, expiresAt, scopeNames);
    }

    private async Task<ApiKeyExchangeResult> FailAsync(string reason, string? ipAddress, string? userAgent, CancellationToken ct)
    {
        await _auditService.LogApiKeyExchangeFailedAsync(reason, ipAddress, userAgent, cancellationToken: ct);
        return new ApiKeyExchangeResult(false, Error: GenericError);
    }

    private static List<string> ExpandScopes(ApiKeyScope scopes)
    {
        List<string> names = [];
        foreach (ApiKeyScope flag in Enum.GetValues<ApiKeyScope>())
        {
            if (flag is ApiKeyScope.None or ApiKeyScope.All)
            {
                continue;
            }

            if (scopes.HasFlag(flag))
            {
                names.Add(flag.ToString());
            }
        }

        return names;
    }

    private static string ComputeSha256Hash(string rawData)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(rawData);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
