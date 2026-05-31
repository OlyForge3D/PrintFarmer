using Fido2NetLib;
using Fido2NetLib.Objects;

namespace Farm.Infrastructure.Services.Authentication;

/// <summary>
/// Orchestrates WebAuthn/FIDO2 passkey registration and authentication ceremonies.
/// Challenge state is stored in <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/>
/// with a 5-minute TTL. Credential persistence is deferred to the entity migration in #354.
/// </summary>
public interface IPasskeyService
{
    /// <summary>
    /// Generates attestation options for a new passkey registration and caches the challenge.
    /// </summary>
    Task<CredentialCreateOptions> BeginRegistrationAsync(Guid userId, string username, CancellationToken ct = default);

    /// <summary>
    /// Verifies the authenticator attestation. Challenge is consumed from cache (replay prevention).
    /// Credential persistence is stubbed until #354 delivers <c>UserPasskeyCredential</c>.
    /// </summary>
    Task<RegisteredPublicKeyCredential> CompleteRegistrationAsync(string username, AuthenticatorAttestationRawResponse attestationResponse, CancellationToken ct = default);

    /// <summary>
    /// Generates assertion options for a passkey login and caches the challenge.
    /// </summary>
    Task<AssertionOptions> BeginLoginAsync(string username, CancellationToken ct = default);

    /// <summary>
    /// Verifies the authenticator assertion and issues a JWT on success.
    /// Stubbed for credential lookup until #354 delivers stored-credential support.
    /// </summary>
    Task<AuthenticationResult> CompleteLoginAsync(string username, AuthenticatorAssertionRawResponse assertionResponse, CancellationToken ct = default);
}
