using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure.Services.HostUpdates.PullApproval;

/// <summary>Serializer settings for the daemon contract: camelCase members and string enums, as in the API.</summary>
public static class HostUpdateDaemonJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 16
    };
}

/// <summary>Signs response envelopes with the per-enrollment response key (API side).</summary>
public static class HostUpdateDaemonResponseSigner
{
    public static HostUpdateDaemonSignedResponseDto Sign<TPayload>(
        HostUpdateDaemonResponseEnvelope<TPayload> envelope,
        ECDsa responseKey,
        string responseKeyId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(responseKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(responseKeyId);
        if (!HostUpdateDaemonRequestVerifier.IsP256(responseKey))
        {
            throw new ArgumentException("Response key must be ECDSA P-256.", nameof(responseKey));
        }

        string content = JsonSerializer.Serialize(envelope, HostUpdateDaemonJson.Options);
        byte[] signature = responseKey.SignData(
            SignedBytes(content),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return new HostUpdateDaemonSignedResponseDto
        {
            SignedContent = content,
            ResponseKeyId = responseKeyId,
            Algorithm = HostUpdateDaemonSignatureAlgorithm.EcdsaP256Sha256,
            Signature = Base64UrlEncode(signature)
        };
    }

    internal static byte[] SignedBytes(string content) =>
        Encoding.UTF8.GetBytes(HostUpdateDaemonProtocol.ResponseSignaturePrefix + content);

    internal static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[]? Base64UrlDecode(string value)
    {
        if (value.Length is 0 or > 256)
        {
            return null;
        }

        string padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>What the daemon expects a response to echo, from its own durable state.</summary>
public sealed record HostUpdateDaemonResponseExpectation
{
    public required string PinnedResponseKeyId { get; init; }

    public required ReadOnlyMemory<byte> PinnedResponseKey { get; init; }

    public required string InstallationId { get; init; }

    public required string KeyId { get; init; }

    public required long EnrollmentEpoch { get; init; }

    public required string RequestNonce { get; init; }

    public required long RequestCounter { get; init; }

    public required long LastAcknowledgedCounter { get; init; }

    public required long LastEnrollmentStateRevision { get; init; }
}

/// <summary>Daemon handling of a response.</summary>
public enum HostUpdateDaemonResponseOutcome
{
    /// <summary>Unauthentic or not for this request: ignore it and admit nothing.</summary>
    Discard,

    /// <summary>Authentic, but the API's state moved underneath the daemon: hold all work.</summary>
    NeedsOperator,

    Accepted
}

/// <summary>Result of <see cref="HostUpdateDaemonResponseVerifier.Verify"/>.</summary>
public sealed record HostUpdateDaemonResponseVerification
{
    public required HostUpdateDaemonResponseOutcome Outcome { get; init; }

    public required string ReasonCode { get; init; }

    public HostUpdateDaemonResponseEnvelope<JsonElement>? Envelope { get; init; }

    public bool Accepted => Outcome == HostUpdateDaemonResponseOutcome.Accepted;

    /// <summary>Deserializes the payload of an accepted response of the given type.</summary>
    public bool TryGetPayload<TPayload>(HostUpdateDaemonResponseType type, out TPayload? payload)
    {
        payload = default;
        if (!Accepted || Envelope is null || Envelope.ResponseType != type)
        {
            return false;
        }

        try
        {
            payload = Envelope.Payload.Deserialize<TPayload>(HostUpdateDaemonJson.Options);
            return payload is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// Daemon-side verification of a signed response. Fails closed: unsigned, mis-signed, unpinned-key
/// or non-matching responses are discarded; epoch, installation, counter or revision regressions
/// mean the API state was replaced or rolled back and yield <c>NeedsOperator</c>.
/// </summary>
public static class HostUpdateDaemonResponseVerifier
{
    public static HostUpdateDaemonResponseVerification Verify(
        HostUpdateDaemonSignedResponseDto? response,
        HostUpdateDaemonResponseExpectation expectation,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        if (response is null || string.IsNullOrEmpty(response.SignedContent) || string.IsNullOrEmpty(response.Signature))
        {
            return Discard("response_unsigned");
        }

        if (Encoding.UTF8.GetByteCount(response.SignedContent) > HostUpdateDaemonProtocol.MaxRequestBodyBytes)
        {
            return Discard("response_too_large");
        }

        if (!string.Equals(response.ResponseKeyId, expectation.PinnedResponseKeyId, StringComparison.Ordinal))
        {
            return Discard("response_key_unexpected");
        }

        if (response.Algorithm != HostUpdateDaemonSignatureAlgorithm.EcdsaP256Sha256)
        {
            return Discard("response_algorithm_unsupported");
        }

        byte[]? signature = HostUpdateDaemonResponseSigner.Base64UrlDecode(response.Signature);
        if (signature is null
            || !HostUpdateDaemonRequestVerifier.VerifySignature(
                expectation.PinnedResponseKey,
                HostUpdateDaemonResponseSigner.SignedBytes(response.SignedContent),
                signature))
        {
            return Discard("response_signature_invalid");
        }

        HostUpdateDaemonResponseEnvelope<JsonElement>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<HostUpdateDaemonResponseEnvelope<JsonElement>>(response.SignedContent, HostUpdateDaemonJson.Options);
        }
        catch (JsonException)
        {
            return Discard("response_malformed");
        }

        if (envelope is null || envelope.ProtocolVersion != HostUpdateDaemonProtocol.Version)
        {
            return Discard("response_protocol_unsupported");
        }

        if (!string.Equals(envelope.RequestNonce, expectation.RequestNonce, StringComparison.Ordinal))
        {
            return Discard("response_nonce_mismatch");
        }

        if (!string.Equals(envelope.KeyId, expectation.KeyId, StringComparison.Ordinal))
        {
            return Discard("response_key_binding_mismatch");
        }

        if ((now - envelope.IssuedAt).Duration() > HostUpdateDaemonProtocol.ClockSkew)
        {
            return Discard("response_outside_time_window");
        }

        if (!string.Equals(envelope.InstallationId, expectation.InstallationId, StringComparison.Ordinal))
        {
            return NeedsOperator(HostUpdatePullReasons.InstallationMismatch, envelope);
        }

        if (envelope.EnrollmentEpoch != expectation.EnrollmentEpoch)
        {
            return NeedsOperator("enrollment_epoch_changed", envelope);
        }

        if (envelope.AcknowledgedCounter < expectation.LastAcknowledgedCounter
            || envelope.AcknowledgedCounter >= expectation.RequestCounter)
        {
            return NeedsOperator("api_counter_rollback", envelope);
        }

        if (envelope.EnrollmentStateRevision < expectation.LastEnrollmentStateRevision)
        {
            return NeedsOperator("api_state_rollback", envelope);
        }

        return new HostUpdateDaemonResponseVerification
        {
            Outcome = HostUpdateDaemonResponseOutcome.Accepted,
            ReasonCode = "accepted",
            Envelope = envelope
        };
    }

    private static HostUpdateDaemonResponseVerification Discard(string reason) =>
        new() { Outcome = HostUpdateDaemonResponseOutcome.Discard, ReasonCode = reason };

    private static HostUpdateDaemonResponseVerification NeedsOperator(string reason, HostUpdateDaemonResponseEnvelope<JsonElement> envelope) =>
        new() { Outcome = HostUpdateDaemonResponseOutcome.NeedsOperator, ReasonCode = reason, Envelope = envelope };
}
