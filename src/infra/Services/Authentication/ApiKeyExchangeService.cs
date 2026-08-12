using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Api;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Farm.Infrastructure.Services.Authentication;

/// <summary>
/// Default <see cref="IApiKeyExchangeService"/> implementation. Reuses the same JWT
/// signing key/issuer/audience configuration as <see cref="AuthenticationService"/>'s
/// login tokens so exchanged tokens validate identically on both the main API and the
/// slicer host, but issues a much shorter-lived, minimally scoped token.
/// </summary>
/// <remarks>
/// <para>
/// An exchanged token never carries a role claim. It carries one <c>scope</c> claim per
/// explicitly stored <see cref="ApiKeyScope"/> flag, plus - for the privileged flags listed in
/// <see cref="DesktopScopePermissionMap.PermissionByScope"/> - the single mapped
/// <c>permission</c> claim for each.
/// </para>
/// <para>
/// <b>Anti-self-escalation.</b> The emitted claim set derives from <b>one effective mask</b>:
/// <c>(explicit flags stored on the key) ∩ (the owner's live authorization)</c>, resolved from a
/// single snapshot of the owner's active roles and granted permissions on every exchange. The
/// <c>scope</c> claims, the <c>permission</c> claims, and the scope list returned to the client all
/// come from that same mask, so a scope can never appear without its permission. A
/// <c>farm_admin</c> owner may authorize any explicitly selected mapped permission, but the admin
/// role itself is never copied into the token, so an admin-owned key is still limited to exactly
/// what was selected.
/// </para>
/// <para>
/// <b>Downgrade, not hard failure.</b> When the owner has lost a permission, only the affected
/// permission-backed scopes are dropped; the legacy model/library scopes are retained. Revoking a
/// calibration role therefore does not break an unrelated desktop model sync. The exchange fails
/// only when nothing survives. The requested, effective, and dropped scope names plus the granted
/// permissions are recorded in the audit log. Revocation takes effect on the next exchange, bounded
/// by the token lifetime, which is itself clamped to <see cref="MaxLifetimeMinutes"/> minutes.
/// </para>
/// </remarks>
public class ApiKeyExchangeService(
    IApiKeyRepository apiKeyRepository,
    IUsersRepository usersRepository,
    IAuthAuditService auditService,
    IConfiguration configuration,
    ILogger<ApiKeyExchangeService> logger) : IApiKeyExchangeService
{
    private const string GenericError = "Invalid API key";

    /// <summary>Lifetime applied when <c>Jwt:DesktopExchangeLifetimeMinutes</c> is unset or invalid.</summary>
    internal const int DefaultLifetimeMinutes = 15;

    /// <summary>Hard ceiling on the exchange token lifetime, regardless of configuration.</summary>
    internal const int MaxLifetimeMinutes = 15;

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

        if (DesktopScopePermissionMap.HasUndefinedBits(apiKey.Scopes))
        {
            // A stored value outside the known mask cannot be interpreted safely, so it is
            // rejected rather than silently masked down to its recognized bits.
            return await FailAsync("unknown_scope_bits", ipAddress, userAgent, ct);
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

        IReadOnlyList<string> scopeNames = DesktopScopePermissionMap.GetScopeNames(apiKey.Scopes);

        // Least privilege: one effective mask, computed from a single live snapshot of the owner's
        // authorization. Both the scope claims and the permission claims below derive from it, so
        // a scope can never survive without its permission or vice versa.
        OwnerAuthorization ownerAuthorization = await ResolveOwnerAuthorizationAsync(owner.Id, ct);
        EffectiveDesktopScopes effective = DesktopScopePermissionMap.ResolveEffectiveScopes(
            apiKey.Scopes,
            ownerAuthorization.IsFarmAdmin,
            ownerAuthorization.Permissions,
            ownerAuthorization.DeniedPermissions);

        if (effective.Effective == ApiKeyScope.None)
        {
            // Every stored scope was revoked - there is nothing left to authorize.
            return await FailAsync("owner_missing_scope_authorization", ipAddress, userAgent, ct);
        }

        IReadOnlyList<string> effectiveScopeNames = DesktopScopePermissionMap.GetScopeNames(effective.Effective);
        IReadOnlyList<string> droppedScopeNames = DesktopScopePermissionMap.GetScopeNames(effective.Dropped);
        IReadOnlyList<string> grantedPermissions = DesktopScopePermissionMap.GetPermissions(effective.Effective);

        if (effective.WasDowngraded)
        {
            // Names are compile-time constants, never caller-supplied text.
            _logger.LogWarning(
                "Desktop API key exchange downgraded for user {UserId}: dropped scopes {DroppedScopes} the owner is no longer authorized for",
                owner.Id,
                string.Join(", ", droppedScopeNames));
        }

        DateTime expiresAt = DateTime.UtcNow.AddMinutes(ResolveLifetimeMinutes());

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
        claims.AddRange(effectiveScopeNames.Select(s => new Claim(DesktopScopeClaims.Scope, s)));

        // Deliberately no role claim: a farm_admin owner authorizes the selected permissions but
        // never lends the token its implicit admin bypass.
        claims.AddRange(grantedPermissions.Select(p => new Claim(PrintFarmerPermissions.ClaimType, p)));

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

        await _auditService.LogApiKeyExchangeAsync(
            owner.Id,
            apiKey.Id,
            ipAddress,
            userAgent,
            new ApiKeyExchangeScopeAudit(scopeNames, effectiveScopeNames, droppedScopeNames, grantedPermissions),
            cancellationToken: ct);

        return new ApiKeyExchangeResult(true, token, expiresAt, [.. effectiveScopeNames]);
    }

    /// <summary>
    /// Resolves the exchange token lifetime, clamped to <see cref="MaxLifetimeMinutes"/>. An
    /// exchange token is a bearer credential held on an end-user machine, so a misconfigured or
    /// hostile value must never be able to extend it into a long-lived credential.
    /// </summary>
    private int ResolveLifetimeMinutes()
    {
        string? configured = _configuration["Jwt:DesktopExchangeLifetimeMinutes"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultLifetimeMinutes;
        }

        if (!int.TryParse(configured, out int parsed) || parsed <= 0)
        {
            _logger.LogWarning(
                "Jwt:DesktopExchangeLifetimeMinutes is not a positive integer; falling back to {DefaultLifetimeMinutes} minutes.",
                DefaultLifetimeMinutes);
            return DefaultLifetimeMinutes;
        }

        if (parsed > MaxLifetimeMinutes)
        {
            _logger.LogWarning(
                "Jwt:DesktopExchangeLifetimeMinutes ({ConfiguredLifetimeMinutes}) exceeds the {MaxLifetimeMinutes}-minute ceiling for Desktop exchange tokens; clamping.",
                parsed,
                MaxLifetimeMinutes);
            return MaxLifetimeMinutes;
        }

        return parsed;
    }

    /// <summary>
    /// A single point-in-time snapshot of the owner's authorization, so every scope decision in one
    /// exchange is evaluated against consistent data rather than re-querying per scope.
    /// </summary>
    private sealed record OwnerAuthorization(
        bool IsFarmAdmin,
        IReadOnlySet<string> Permissions,
        IReadOnlySet<string> DeniedPermissions);

    private async Task<OwnerAuthorization> ResolveOwnerAuthorizationAsync(Guid ownerId, CancellationToken ct)
    {
        List<string> roles = await _usersRepository.GetActiveRoleNamesAsync(ownerId, ct) ?? [];
        bool isFarmAdmin = roles.Contains(PrintFarmerPermissions.FarmAdminRole, StringComparer.OrdinalIgnoreCase);

        List<(string Resource, string Action)> granted =
            await _usersRepository.GetGrantedPermissionsAsync(ownerId, ct) ?? [];
        HashSet<string> permissions = granted
            .Select(p => $"{p.Resource}:{p.Action}")
            .ToHashSet(StringComparer.Ordinal);

        // Explicit denies are resolved alongside grants so the same-resource admin implication
        // cannot resurrect an action the operator denied - the precedence rule established in
        // docs/ROLE_PERMISSION_PRECEDENCE.md.
        List<(string Resource, string Action)> denied =
            await _usersRepository.GetDeniedPermissionsAsync(ownerId, ct) ?? [];
        HashSet<string> deniedPermissions = denied
            .Select(p => $"{p.Resource}:{p.Action}")
            .ToHashSet(StringComparer.Ordinal);

        return new OwnerAuthorization(isFarmAdmin, permissions, deniedPermissions);
    }

    private async Task<ApiKeyExchangeResult> FailAsync(string reason, string? ipAddress, string? userAgent, CancellationToken ct)
    {
        await _auditService.LogApiKeyExchangeFailedAsync(reason, ipAddress, userAgent, cancellationToken: ct);
        return new ApiKeyExchangeResult(false, Error: GenericError);
    }

    private static string ComputeSha256Hash(string rawData)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(rawData);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
