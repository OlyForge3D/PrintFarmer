using System;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Services.OctoPrint;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

[ApiController]
[Route("api/users/{userId:guid}/apikeys")]
[Authorize(Policy = InteractiveSessionRequirement.PolicyName)]
public class UserApiKeysController : ControllerBase
{
    internal const int MaxNameLength = 256;

    /// <summary>Applied to Desktop-purpose keys when the caller doesn't specify an expiry.</summary>
    internal static readonly TimeSpan DefaultDesktopKeyLifetime = TimeSpan.FromDays(90);

    /// <summary>Maximum allowed expiry horizon for a Desktop-purpose key.</summary>
    internal static readonly TimeSpan MaxKeyLifetime = TimeSpan.FromDays(365);

    private readonly Farm.Infrastructure.Repositories.Api.IApiKeyRepository _repo;
    private readonly ISettingsService _settingsService;
    private readonly IUsersRepository _usersRepository;

    public UserApiKeysController(
        Farm.Infrastructure.Repositories.Api.IApiKeyRepository repo,
        ISettingsService settingsService,
        IUsersRepository usersRepository)
    {
        _repo = repo;
        _settingsService = settingsService;
        _usersRepository = usersRepository;
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
            k.ExpiresAt,
            k.Purpose,
            k.Scopes,
            k.IsExpired,
            DesktopScopePermissionMap.GetScopeNames(k.Scopes)));
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

        string name = string.IsNullOrWhiteSpace(req.Name) ? "user-generated" : req.Name.Trim();
        if (name.Length > MaxNameLength)
        {
            return BadRequest(new { error = $"API key name cannot exceed {MaxNameLength} characters." });
        }

        ApiKeyPurpose purpose = req.Purpose ?? ApiKeyPurpose.OctoPrint;
        if (!TryResolveRequestedScopes(req, out ApiKeyScope scopes, out string? scopeParseError))
        {
            return BadRequest(new { error = scopeParseError });
        }

        DateTime? expiresAt = req.ExpiresAt;

        if (!TryValidateScopesAndExpiry(purpose, scopes, ref expiresAt, out string? validationError))
        {
            return BadRequest(new { error = validationError });
        }

        // Privileged scopes translate 1:1 into permission claims at exchange time, so they may
        // only be stored on a key whose owner is independently authorized for them. Resolved from
        // the target owner's live database roles/grants - never from the caller's JWT claims, which
        // the caller could otherwise present to authorize a key for someone else. Checked here for
        // early, actionable feedback; the exchange re-resolves and downgrades regardless, so a
        // later revocation still takes effect.
        string? escalationError = await ValidateOwnerScopeAuthorizationAsync(userId, scopes, HttpContext.RequestAborted);
        if (escalationError is not null)
        {
            return BadRequest(new { error = escalationError });
        }

        string rawKey = GenerateKey();
        string storedValue = GetValueForStorage(rawKey, purpose);

        var key = new Farm.Infrastructure.Domain.ApiKey
        {
            UserId = userId,
            Name = name,
            KeyHash = storedValue,
            IsActive = true,
            Purpose = purpose,
            Scopes = scopes,
            ExpiresAt = expiresAt
        };

        await _repo.AddAsync(key);

        return Ok(new
        {
            key = rawKey,
            id = key.Id,
            purpose = key.Purpose,
            scopes = key.Scopes,
            scopeNames = DesktopScopePermissionMap.GetScopeNames(key.Scopes),
            expiresAt = key.ExpiresAt
        });
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

        if (oldKey.IsExpired)
        {
            return BadRequest(new { error = "Expired API keys cannot be rotated. Create a new API key instead." });
        }

        string rawKey = GenerateKey();
        string storedValue = GetValueForStorage(rawKey, oldKey.Purpose);

        oldKey.KeyHash = storedValue;
        await _repo.UpdateAsync(oldKey);

        // Rotation replaces only the secret: purpose, scopes, and expiry are preserved exactly.
        return Ok(new
        {
            key = rawKey,
            id = oldKey.Id,
            purpose = oldKey.Purpose,
            scopes = oldKey.Scopes,
            scopeNames = DesktopScopePermissionMap.GetScopeNames(oldKey.Scopes),
            expiresAt = oldKey.ExpiresAt
        });
    }

    /// <summary>
    /// Reveal a legacy OctoPrint API key when HashStoredApiKeys is disabled.
    /// Desktop API keys are always one-time secrets and cannot be revealed.
    /// </summary>
    [HttpGet("{keyId:guid}/reveal")]
    [Authorize]
    public async Task<IActionResult> RevealApiKeyAsync([FromRoute] Guid userId, [FromRoute] Guid keyId)
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

        if (key.Purpose == ApiKeyPurpose.Desktop)
        {
            return BadRequest(new { error = "Desktop API keys are one-time secrets and cannot be revealed. Rotate the key to generate a new secret." });
        }

        OctoPrintSettings settings = _settingsService.Get<OctoPrintSettings>();
        if (settings.HashStoredApiKeys)
        {
            return BadRequest(new { error = "Cannot reveal API key when hashing is enabled. Keys are stored as one-way hashes." });
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

        return User.IsInRole("farm_admin");
    }

    /// <summary>
    /// Resolves the requested scopes from either the canonical <c>scopeNames</c> array (preferred)
    /// or the legacy <c>scopes</c> flags field, which is retained for existing clients.
    /// </summary>
    /// <remarks>
    /// <c>scopeNames</c> exists because the legacy field is a <c>[Flags]</c> enum: it serializes
    /// the exact value 7 as the single name <c>"All"</c> and accepts raw numbers, both of which
    /// make privilege review error-prone. The canonical array names each granted scope explicitly
    /// and rejects composite aliases outright. Supplying both is rejected rather than silently
    /// resolved, so a client can never think it sent one thing while the server stored another.
    /// </remarks>
    private static bool TryResolveRequestedScopes(
        CreateApiKeyRequest req,
        out ApiKeyScope scopes,
        out string? error)
    {
        scopes = ApiKeyScope.None;
        error = null;

        // Mutual exclusion keys off field *presence*, not off whether the value happens to be
        // empty. Inferring presence from a non-empty array or a non-None flag would silently
        // accept `{"scopeNames": [], "scopes": "ModelRead"}` and
        // `{"scopeNames": ["ModelRead"], "scopes": "None"}` - in both cases the caller sent both
        // fields and could reasonably believe the other one governed what was stored.
        bool sentScopeNames = req.ScopeNames is not null;
        bool sentLegacyScopes = req.Scopes is not null;

        if (sentScopeNames && sentLegacyScopes)
        {
            error = "Specify either 'scopeNames' or the legacy 'scopes' field, not both.";
            return false;
        }

        if (!sentScopeNames)
        {
            scopes = req.Scopes ?? ApiKeyScope.None;
            return true;
        }

        foreach (string name in req.ScopeNames!)
        {
            if (!DesktopScopePermissionMap.TryParseScopeName(name, out ApiKeyScope parsed))
            {
                // Deliberately does not echo the caller-supplied value back.
                error = "Unknown API key scope name. Use the individual scope names returned by the API; composite aliases such as 'All' are not accepted.";
                return false;
            }

            scopes |= parsed;
        }

        return true;
    }

    /// <summary>
    /// Validates and normalizes the requested purpose/scopes/expiry combination, applying
    /// safe defaults for Desktop-purpose keys. OctoPrint-purpose and legacy keys are
    /// never allowed to carry scopes, so existing or unscoped keys can never gain desktop
    /// access.
    /// </summary>
    private static bool TryValidateScopesAndExpiry(
        ApiKeyPurpose purpose,
        ApiKeyScope scopes,
        ref DateTime? expiresAt,
        out string? error)
    {
        error = null;

        if (!Enum.IsDefined(purpose))
        {
            error = "Invalid API key purpose.";
            return false;
        }

        // Validated against the explicit known-flag mask, never against ApiKeyScope.All: All is
        // frozen at the three legacy model/library scopes and must never authorize the privileged
        // calibration/slicing flags.
        if (DesktopScopePermissionMap.HasUndefinedBits(scopes))
        {
            error = "Invalid API key scope.";
            return false;
        }

        DateTime now = DateTime.UtcNow;
        if (expiresAt is not null)
        {
            expiresAt = expiresAt.Value.Kind switch
            {
                DateTimeKind.Utc => expiresAt.Value,
                DateTimeKind.Local => expiresAt.Value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(expiresAt.Value, DateTimeKind.Utc),
            };

            if (expiresAt <= now)
            {
                error = "API key expiry must be in the future.";
                return false;
            }

            if (expiresAt > now.Add(MaxKeyLifetime))
            {
                error = $"API key expiry cannot exceed {MaxKeyLifetime.TotalDays:0} days from now.";
                return false;
            }
        }

        switch (purpose)
        {
            case ApiKeyPurpose.OctoPrint:
                if (scopes != ApiKeyScope.None)
                {
                    error = "Scopes can only be granted to Desktop-purpose API keys.";
                    return false;
                }

                break;

            case ApiKeyPurpose.Desktop:
                if (scopes == ApiKeyScope.None)
                {
                    error = "At least one scope is required for Desktop-purpose API keys.";
                    return false;
                }

                // Reject combinations that would dead-end mid-workflow (e.g. generating a
                // calibration profile without being able to submit the slice job it needs).
                IReadOnlyList<(string Scope, string MissingPrerequisite)> unsatisfied =
                    DesktopScopePermissionMap.GetUnsatisfiedDependencies(scopes);
                if (unsatisfied.Count > 0)
                {
                    string detail = string.Join(
                        "; ",
                        unsatisfied.Select(u => $"{u.Scope} also requires {u.MissingPrerequisite}"));
                    error = $"Incomplete scope selection: {detail}.";
                    return false;
                }

                if (expiresAt is null)
                {
                    // Safe default: Desktop keys always expire even when the caller doesn't specify a date.
                    expiresAt = now.Add(DefaultDesktopKeyLifetime);
                }

                break;

            default:
                error = "Unsupported API key purpose.";
                return false;
        }

        return true;
    }

    /// <summary>
    /// Verifies that the key's <b>owner</b> - not the caller - independently holds every
    /// permission the requested privileged scopes map to, so a key can never grant more than its
    /// owner already has. A <c>farm_admin</c> owner may authorize any mapped permission (the admin
    /// role itself is still never copied into an exchanged token).
    /// </summary>
    /// <returns>An error message when the owner is not authorized, otherwise <c>null</c>.</returns>
    private async Task<string?> ValidateOwnerScopeAuthorizationAsync(
        Guid ownerId,
        ApiKeyScope scopes,
        CancellationToken ct)
    {
        IReadOnlyList<string> requested = DesktopScopePermissionMap.GetPermissions(scopes);
        if (requested.Count == 0)
        {
            return null;
        }

        List<string> roles = await _usersRepository.GetActiveRoleNamesAsync(ownerId, ct) ?? [];
        if (roles.Contains(PrintFarmerPermissions.FarmAdminRole, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        List<(string Resource, string Action)> granted =
            await _usersRepository.GetGrantedPermissionsAsync(ownerId, ct) ?? [];
        HashSet<string> ownerPermissions = granted
            .Select(p => $"{p.Resource}:{p.Action}")
            .ToHashSet(StringComparer.Ordinal);

        List<string> missing = requested.Where(p => !ownerPermissions.Contains(p)).ToList();
        if (missing.Count == 0)
        {
            return null;
        }

        // Names come from PrintFarmerPermissions compile-time constants, never from the request
        // body, so echoing them back cannot reflect caller-controlled content.
        return "The API key owner is not authorized for the requested scope(s). " +
            $"Grant the owner these permissions first: {string.Join(", ", missing)}.";
    }

    private static string GenerateKey()
    {
        byte[] data = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(data);
        return Convert.ToHexString(data);
    }

    private string GetValueForStorage(string rawKey, ApiKeyPurpose purpose)
    {
        OctoPrintSettings settings = _settingsService.Get<OctoPrintSettings>();
        return purpose == ApiKeyPurpose.Desktop || settings.HashStoredApiKeys
            ? ComputeSha256Hash(rawKey)
            : rawKey;
    }

    private static string ComputeSha256Hash(string rawData)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] bytes = Encoding.UTF8.GetBytes(rawData);
        byte[] hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }
}

/// <summary>
/// Request to create an API key.
/// </summary>
/// <param name="Name">Display name for the key.</param>
/// <param name="Purpose">OctoPrint (default) or Desktop.</param>
/// <param name="Scopes">
/// Legacy flags field, retained for existing clients. Prefer <paramref name="ScopeNames"/>.
/// </param>
/// <param name="ExpiresAt">Optional expiry; Desktop keys always get one.</param>
/// <param name="ScopeNames">
/// Canonical, explicit scope names (e.g. <c>["ModelRead", "CalibrationRead"]</c>). Composite
/// aliases such as <c>"All"</c> are rejected. Cannot be combined with <paramref name="Scopes"/>.
/// </param>
public record CreateApiKeyRequest(
    string? Name,
    ApiKeyPurpose? Purpose = null,
    ApiKeyScope? Scopes = null,
    DateTime? ExpiresAt = null,
    IReadOnlyList<string>? ScopeNames = null);

/// <summary>
/// An API key as returned to the owning user or an administrator. Never contains the secret.
/// </summary>
/// <param name="Id">The key's unique identifier.</param>
/// <param name="Name">The key's display name.</param>
/// <param name="IsActive">Whether the key is currently enabled.</param>
/// <param name="CreatedAt">When the key was created (UTC).</param>
/// <param name="ExpiresAt">When the key expires (UTC), if it has an expiry.</param>
/// <param name="Purpose">What the key authenticates: OctoPrint uploads or the Desktop app.</param>
/// <param name="Scopes">
/// Legacy flags rendering. Renders the exact value 7 as the single, misleading name <c>All</c>;
/// prefer <paramref name="ScopeNames"/>.
/// </param>
/// <param name="IsExpired">Whether the key's expiry has already passed.</param>
/// <param name="ScopeNames">The key's scopes as individual canonical names.</param>
public record ApiKeyDto(
    Guid Id,
    string Name,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    ApiKeyPurpose Purpose,
    ApiKeyScope Scopes,
    bool IsExpired,
    IReadOnlyList<string> ScopeNames);
