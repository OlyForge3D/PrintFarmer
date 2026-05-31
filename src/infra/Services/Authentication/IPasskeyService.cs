using Farm.Infrastructure.Domain;
using Fido2NetLib;
using Fido2NetLib.Objects;

namespace Farm.Infrastructure.Services.Authentication;

/// <summary>
/// Orchestrates WebAuthn/FIDO2 passkey registration and authentication ceremonies.
/// Challenge state is stored in <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/>
/// with a 5-minute TTL.
/// </summary>
public interface IPasskeyService
{
    /// <summary>
    /// Generates attestation options for a new passkey registration and caches the challenge.
    /// </summary>
    Task<CredentialCreateOptions> BeginRegistrationAsync(Guid userId, string username, CancellationToken ct = default);

    /// <summary>
    /// Verifies the authenticator attestation. Challenge is consumed from cache (replay prevention).
    /// </summary>
    Task<RegisteredPublicKeyCredential> CompleteRegistrationAsync(string username, AuthenticatorAttestationRawResponse attestationResponse, CancellationToken ct = default);

    /// <summary>
    /// Generates assertion options for a passkey login and caches the challenge.
    /// </summary>
    Task<AssertionOptions> BeginLoginAsync(string username, CancellationToken ct = default);

    /// <summary>
    /// Verifies the authenticator assertion and issues a JWT on success.
    /// </summary>
    Task<AuthenticationResult> CompleteLoginAsync(string username, AuthenticatorAssertionRawResponse assertionResponse, CancellationToken ct = default);

    /// <summary>
    /// Lists all registered passkey credentials for a user.
    /// </summary>
    Task<List<UserPasskeyCredential>> ListCredentialsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Deletes a passkey credential. Verifies ownership by userId.
    /// </summary>
    Task<bool> DeleteCredentialAsync(Guid userId, int credentialId, CancellationToken ct = default);

    /// <summary>
    /// Renames a passkey credential's DeviceName. Verifies ownership by userId.
    /// </summary>
    Task<bool> RenameCredentialAsync(Guid userId, int credentialId, string newName, CancellationToken ct = default);
}
