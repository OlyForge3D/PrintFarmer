using System.Formats.Cbor;
using System.Text;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Users;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
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
    AppDbContext db,
    IUsersRepository usersRepository,
    IAuthenticationService authenticationService,
    IEnumerable<IMetadataService> metadataServices,
    ILogger<PasskeyService> logger) : IPasskeyService
{
    // IMetadataService is optional — not all deployments configure FIDO MDS.
    private readonly IMetadataService? _metadataService = metadataServices.FirstOrDefault();
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

    public async Task<(RegisteredPublicKeyCredential Credential, int NewCredentialId)> CompleteRegistrationAsync(
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

        // Enforce uniqueness: reject if credential ID already registered
        RegisteredPublicKeyCredential result = await fido2.MakeNewCredentialAsync(
            new MakeNewCredentialParams
            {
                AttestationResponse = attestationResponse,
                OriginalOptions = originalOptions,
                IsCredentialIdUniqueToUserCallback = async (args, _) =>
                    !await db.UserPasskeyCredentials.AnyAsync(c => c.CredentialId == args.CredentialId, ct),
            },
            ct);

        User? user = await usersRepository.GetByUsernameAsync(username, ct);
        if (user is null)
        {
            throw new InvalidOperationException($"User '{username}' not found after successful attestation.");
        }

        string? aaguidDescription = await ResolveAaGuidDescriptionAsync(result.AttestationObject, ct);

        UserPasskeyCredential credential = new()
        {
            UserId = user.Id,
            CredentialId = result.Id,
            PublicKey = result.PublicKey,
            SignCount = result.SignCount,
            AaguidDescription = aaguidDescription,
            CreatedAt = DateTime.UtcNow,
        };

        db.UserPasskeyCredentials.Add(credential);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Passkey registered for {Username} (credentialId={CredentialId}, aaguid={AaGuid})",
            username,
            Convert.ToBase64String(result.Id),
            aaguidDescription ?? "unknown");

        return (result, credential.Id);
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
        AssertionOptions storedOptions = await LoadOptionsAsync<AssertionOptions>(
            CacheKeys.Assertion(username),
            AssertionOptions.FromJson,
            ct);

        UserPasskeyCredential? credential = await db.UserPasskeyCredentials
            .Include(c => c.User)
            .FirstOrDefaultAsync(c => c.CredentialId == assertionResponse.RawId, ct);

        if (credential is null)
        {
            logger.LogWarning("Passkey assertion failed — no stored credential for {Username}", username);
            return new AuthenticationResult(false, Error: "Credential not found.");
        }

        if (!string.Equals(credential.User.Username, username, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Passkey assertion rejected — credential belongs to {Owner}, not {Requester}",
                credential.User.Username,
                username);
            return new AuthenticationResult(false, Error: "Credential not found.");
        }

        VerifyAssertionResult assertionResult = await fido2.MakeAssertionAsync(
            new MakeAssertionParams
            {
                AssertionResponse = assertionResponse,
                OriginalOptions = storedOptions,
                StoredPublicKey = credential.PublicKey,
                StoredSignatureCounter = credential.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                    Task.FromResult(
                        args.UserHandle.SequenceEqual(credential.User.Id.ToByteArray()) &&
                        args.CredentialId.SequenceEqual(credential.CredentialId)),
            },
            ct);

        credential.SignCount = assertionResult.SignCount;
        credential.LastUsedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        string token = await authenticationService.GenerateJwtTokenAsync(credential.User);

        logger.LogInformation(
            "Passkey login successful for {Username} (credentialId={CredentialId}, newSignCount={SignCount})",
            username,
            Convert.ToBase64String(credential.CredentialId),
            assertionResult.SignCount);

        User user = credential.User;
        Contracts.Auth.UserDto userDto = new()
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email ?? string.Empty,
            FirstName = user.FirstName,
            LastName = user.LastName,
        };

        return new AuthenticationResult(true, token, DateTime.UtcNow.AddDays(7), userDto);
    }

    public async Task<List<UserPasskeyCredential>> ListCredentialsAsync(Guid userId, CancellationToken ct = default)
    {
        return await db.UserPasskeyCredentials
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<bool> DeleteCredentialAsync(Guid userId, int credentialId, CancellationToken ct = default)
    {
        UserPasskeyCredential? credential = await db.UserPasskeyCredentials
            .FirstOrDefaultAsync(c => c.Id == credentialId && c.UserId == userId, ct);

        if (credential is null)
        {
            return false;
        }

        db.UserPasskeyCredentials.Remove(credential);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Passkey credential {CredentialId} deleted for user {UserId}", credentialId, userId);
        return true;
    }

    public async Task<bool> RenameCredentialAsync(Guid userId, int credentialId, string newName, CancellationToken ct = default)
    {
        UserPasskeyCredential? credential = await db.UserPasskeyCredentials
            .FirstOrDefaultAsync(c => c.Id == credentialId && c.UserId == userId, ct);

        if (credential is null)
        {
            return false;
        }

        credential.DeviceName = newName;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Passkey credential {CredentialId} renamed to '{NewName}' for user {UserId}", credentialId, newName, userId);
        return true;
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Best-effort AAGUID description lookup from FIDO MDS.
    /// Returns null if the metadata service is unavailable or the AAGUID is unknown.
    /// </summary>
    private async Task<string?> ResolveAaGuidDescriptionAsync(byte[]? attestationObject, CancellationToken ct)
    {
        if (_metadataService is null || attestationObject is null)
        {
            return null;
        }

        try
        {
            Guid? aaGuid = ExtractAaGuid(attestationObject);
            if (aaGuid is null || aaGuid == Guid.Empty)
            {
                return null;
            }

            MetadataBLOBPayloadEntry? entry = await _metadataService.GetEntryAsync(aaGuid.Value, ct);
            return entry?.MetadataStatement?.Description;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "AAGUID metadata lookup failed — AaguidDescription will be null");
            return null;
        }
    }

    /// <summary>
    /// Extracts the 16-byte AAGUID from a CBOR-encoded attestation object.
    /// Returns null on any parse failure (best-effort).
    /// </summary>
    private static Guid? ExtractAaGuid(byte[] attestationObject)
    {
        try
        {
            CborReader reader = new(attestationObject);
            reader.ReadStartMap();
            while (reader.PeekState() != CborReaderState.EndMap)
            {
                string key = reader.ReadTextString();
                if (key == "authData")
                {
                    byte[] authData = reader.ReadByteString();

                    // authData layout: rpIdHash(32) + flags(1) + signCount(4) + aaguid(16) + ...
                    if (authData.Length < 53)
                    {
                        return null;
                    }

                    byte flags = authData[32];
                    bool hasAttestedCredentialData = (flags & 0x40) != 0;
                    if (!hasAttestedCredentialData)
                    {
                        return null;
                    }

                    // AAGUID bytes are in big-endian RFC 4122 format
                    byte[] aaguidBytes = authData[37..53];
                    return new Guid(aaguidBytes, bigEndian: true);
                }

                reader.SkipValue();
            }
        }
        catch
        {
            // Best-effort — never throw from AAGUID extraction
        }

        return null;
    }

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
