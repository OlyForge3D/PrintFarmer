using System.Text;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Authentication;

/// <summary>
/// Cache key prefix for pending registration challenges.
/// </summary>
file static class CacheKeys
{
    internal static string Registration(string username) => $"passkey:reg:{username.ToLowerInvariant()}";

    internal static string Assertion(string username) => $"passkey:auth:{username.ToLowerInvariant()}";
}

public class PasskeyService(
    Fido2 fido2,
    IDistributedCache cache,
    ILogger<PasskeyService> logger) : IPasskeyService
{
    private static readonly DistributedCacheEntryOptions ChallengeExpiry = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
    };

    public Task<CredentialCreateOptions> BeginRegistrationAsync(Guid userId, string username, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        Fido2User user = new()
        {
            Id = userId.ToByteArray(),
            Name = username,
            DisplayName = username,
        };

        CredentialCreateOptions options = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = user,
            ExcludeCredentials = [],
            AuthenticatorSelection = new AuthenticatorSelection
            {
                UserVerification = UserVerificationRequirement.Required,
                ResidentKey = ResidentKeyRequirement.Preferred,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });

        return CacheAndReturnAsync(CacheKeys.Registration(username), options.ToJson(), options, ct);
    }

    public async Task<RegisteredPublicKeyCredential> CompleteRegistrationAsync(
        string username,
        AuthenticatorAttestationRawResponse attestationResponse,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(attestationResponse);

        CredentialCreateOptions originalOptions = await LoadOptionsAsync<CredentialCreateOptions>(
            CacheKeys.Registration(username),
            CredentialCreateOptions.FromJson,
            ct);

        RegisteredPublicKeyCredential result = await fido2.MakeNewCredentialAsync(
            new MakeNewCredentialParams
            {
                AttestationResponse = attestationResponse,
                OriginalOptions = originalOptions,
                IsCredentialIdUniqueToUserCallback = (_, _) => Task.FromResult(true),
            },
            ct);

        // TODO #354: persist result to UserPasskeyCredential entity
        logger.LogWarning(
            "Passkey registration verified for {Username} — credential persistence deferred to #354 (id={CredentialId})",
            username,
            Convert.ToBase64String(result.Id));

        return result;
    }

    public Task<AssertionOptions> BeginLoginAsync(string username, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        AssertionOptions options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Required,
        });

        return CacheAndReturnAsync(CacheKeys.Assertion(username), options.ToJson(), options, ct);
    }

    public async Task<AuthenticationResult> CompleteLoginAsync(
        string username,
        AuthenticatorAssertionRawResponse assertionResponse,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(assertionResponse);

        // Load and consume the cached challenge to detect replay attempts
        _ = await LoadOptionsAsync<AssertionOptions>(
            CacheKeys.Assertion(username),
            AssertionOptions.FromJson,
            ct);

        // TODO #354: look up stored UserPasskeyCredential + call fido2.MakeAssertionAsync + issue JWT
        logger.LogWarning(
            "Passkey assertion ceremony skipped credential lookup for {Username} — deferred to #354",
            username);

        return new AuthenticationResult(
            false,
            Error: "Passkey login not yet available — credential storage pending #354");
    }

    // ─── helpers ─────────────────────────────────────────────────────────────
    private async Task<T> CacheAndReturnAsync<T>(string key, string json, T value, CancellationToken ct)
    {
        await cache.SetAsync(key, Encoding.UTF8.GetBytes(json), ChallengeExpiry, ct);
        return value;
    }

    private async Task<T> LoadOptionsAsync<T>(string key, Func<string, T> fromJson, CancellationToken ct)
    {
        byte[]? bytes = await cache.GetAsync(key, ct);
        if (bytes is null)
        {
            throw new PasskeyChallengeNotFoundException($"No pending challenge for key '{key}'. It may have expired or already been used.");
        }

        // Delete immediately to prevent replay
        await cache.RemoveAsync(key, ct);

        return fromJson(Encoding.UTF8.GetString(bytes));
    }
}
