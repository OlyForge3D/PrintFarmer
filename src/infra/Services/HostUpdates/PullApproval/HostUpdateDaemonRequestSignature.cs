using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure.Services.HostUpdates.PullApproval;

/// <summary>
/// Public-key record for an enrolled daemon, as held in the protected <c>HostUpdates:HostState</c>
/// store. Revoked and quarantined records are retained as tombstones.
/// </summary>
public sealed record HostUpdateDaemonKeyRecord
{
    public required string KeyId { get; init; }

    public required string InstallationId { get; init; }

    public required long EnrollmentEpoch { get; init; }

    public required HostUpdateDaemonEnrollmentState State { get; init; }

    /// <summary>DER-encoded SubjectPublicKeyInfo of the daemon's ECDSA P-256 key.</summary>
    public required ReadOnlyMemory<byte> PublicKey { get; init; }

    public required DateTimeOffset KeyExpiresAt { get; init; }

    public required long EnrollmentStateRevision { get; init; }

    public required long HighWaterCounter { get; init; }

    public DateTimeOffset? HighWaterCreatedAt { get; init; }

    public string? HighWaterRequestDigest { get; init; }
}

/// <summary>Raw request material the verifier needs. Header values are passed through unparsed.</summary>
public sealed record HostUpdateDaemonSignedRequest
{
    public required string Method { get; init; }

    public required string Path { get; init; }

    /// <summary>RFC 9421 <c>@query</c> value: <c>?</c> when there is no query.</summary>
    public string Query { get; init; } = "?";

    public required ReadOnlyMemory<byte> Body { get; init; }

    public string? ContentDigest { get; init; }

    public string? Counter { get; init; }

    public string? SignatureInput { get; init; }

    public string? Signature { get; init; }
}

/// <summary>Replay ledger of nonces already accepted for a key, kept in the protected host-state store.</summary>
public interface IHostUpdateDaemonNonceLedger
{
    bool Contains(string keyId, string nonce);
}

/// <summary>Verification outcome, in the order the checks run.</summary>
public enum HostUpdateDaemonRequestOutcome
{
    /// <summary>Malformed, unknown key or bad signature. The only outcome answered with an unsigned 401.</summary>
    Unauthenticated,

    /// <summary>Signed by the key holder but outside the timestamp window. Answered with a signed 401.</summary>
    OutsideTimeWindow,

    /// <summary>Nonce already used. Answered with a signed 401; a replay never quarantines a key.</summary>
    Replay,

    Revoked,
    Quarantined,
    Expired,

    /// <summary>Pending keys may only poll enrollment status.</summary>
    PendingScope,

    /// <summary>Counter at or below the high-water mark without fork evidence.</summary>
    Stale,

    /// <summary>Evidence a single serialized signer cannot produce; the caller must quarantine the key.</summary>
    ForkDetected,

    Accepted
}

/// <summary>Result of <see cref="HostUpdateDaemonRequestVerifier.Verify"/>. Verification never mutates state.</summary>
public sealed record HostUpdateDaemonRequestVerification
{
    public required HostUpdateDaemonRequestOutcome Outcome { get; init; }

    public required string ReasonCode { get; init; }

    public string? KeyId { get; init; }

    public string? Nonce { get; init; }

    public long? Counter { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Hex SHA-256 of the signature base; recorded with the new high-water mark.</summary>
    public string? RequestDigest { get; init; }

    public bool Accepted => Outcome == HostUpdateDaemonRequestOutcome.Accepted;

    /// <summary>True when the key holder produced the signature, so a signed response may be returned.</summary>
    public bool Authenticated => Outcome != HostUpdateDaemonRequestOutcome.Unauthenticated;
}

/// <summary>
/// Fixed RFC 9421 profile for daemon requests. Only one exact component list and parameter order is
/// accepted; anything else is unauthenticated. Covered: <c>@method</c>, <c>@path</c>, <c>@query</c>,
/// <c>content-digest</c> and the request counter header; parameters <c>created</c>, <c>nonce</c>,
/// <c>keyid</c>, <c>alg</c> and <c>tag</c>.
/// </summary>
public static partial class HostUpdateDaemonRequestSignature
{
    private const string Components = "(\"@method\" \"@path\" \"@query\" \"content-digest\" \"printfarmer-request-counter\")";

    public static string ContentDigest(ReadOnlySpan<byte> body) =>
        "sha-256=:" + Convert.ToBase64String(SHA256.HashData(body)) + ":";

    public static string SignatureParams(long created, string nonce, string keyId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Components};created={created};nonce=\"{nonce}\";keyid=\"{keyId}\";alg=\"{HostUpdateDaemonProtocol.SignatureAlgorithm}\";tag=\"{HostUpdateDaemonProtocol.SignatureTag}\"");

    public static string SignatureBase(string method, string path, string query, string contentDigest, long counter, string signatureParams)
    {
        var builder = new StringBuilder();
        builder.Append("\"@method\": ").Append(method).Append('\n');
        builder.Append("\"@path\": ").Append(path).Append('\n');
        builder.Append("\"@query\": ").Append(query).Append('\n');
        builder.Append("\"content-digest\": ").Append(contentDigest).Append('\n');
        builder.Append("\"printfarmer-request-counter\": ").Append(counter.ToString(CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("\"@signature-params\": ").Append(signatureParams);
        return builder.ToString();
    }

    /// <summary>Signs a request. Used by the daemon and by contract tests.</summary>
    public static HostUpdateDaemonSignedRequest Sign(
        string method,
        string path,
        string query,
        ReadOnlyMemory<byte> body,
        long counter,
        DateTimeOffset created,
        string nonce,
        string keyId,
        ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        string digest = ContentDigest(body.Span);
        string parameters = SignatureParams(created.ToUnixTimeSeconds(), nonce, keyId);
        string signatureBase = SignatureBase(method, path, query, digest, counter, parameters);
        byte[] signature = privateKey.SignData(
            Encoding.UTF8.GetBytes(signatureBase),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return new HostUpdateDaemonSignedRequest
        {
            Method = method,
            Path = path,
            Query = query,
            Body = body,
            ContentDigest = digest,
            Counter = counter.ToString(CultureInfo.InvariantCulture),
            SignatureInput = HostUpdateDaemonProtocol.SignatureLabel + "=" + parameters,
            Signature = HostUpdateDaemonProtocol.SignatureLabel + "=:" + Convert.ToBase64String(signature) + ":"
        };
    }

    internal static Match ParseSignatureInput(string value) => SignatureInputPattern().Match(value);

    internal static Match ParseSignature(string value) => SignaturePattern().Match(value);

    internal static bool IsCounter(string value) => CounterPattern().IsMatch(value);

    [GeneratedRegex("^pf=\\(\"@method\" \"@path\" \"@query\" \"content-digest\" \"printfarmer-request-counter\"\\);created=(?<created>[1-9][0-9]{0,11});nonce=\"(?<nonce>[A-Za-z0-9_-]{22,64})\";keyid=\"(?<keyid>[A-Za-z0-9_-]{16,64})\";alg=\"ecdsa-p256-sha256\";tag=\"printfarmer-host-update-v1\"$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SignatureInputPattern();

    [GeneratedRegex("^pf=:(?<sig>[A-Za-z0-9+/]{86}==):$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SignaturePattern();

    [GeneratedRegex("^[1-9][0-9]{0,17}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex CounterPattern();
}

/// <summary>
/// Verifies a signed daemon request against its key record. Order is fixed by the design:
/// signature, timestamp window, nonce, enrollment state, then counter. A failure at any step returns
/// without state change; the caller persists the nonce and new high-water mark only on <c>Accepted</c>
/// and quarantines only on <c>ForkDetected</c>.
/// </summary>
public static class HostUpdateDaemonRequestVerifier
{
    public static HostUpdateDaemonRequestVerification Verify(
        HostUpdateDaemonSignedRequest request,
        Func<string, HostUpdateDaemonKeyRecord?> findKey,
        IHostUpdateDaemonNonceLedger nonces,
        DateTimeOffset now,
        bool enrollmentStatusRequest)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(findKey);
        ArgumentNullException.ThrowIfNull(nonces);

        if (request.Body.Length > HostUpdateDaemonProtocol.MaxRequestBodyBytes
            || request.SignatureInput is null || request.Signature is null
            || request.ContentDigest is null || request.Counter is null
            || !HostUpdateDaemonRequestSignature.IsCounter(request.Counter))
        {
            return Unauthenticated("request_signature_malformed");
        }

        Match input = HostUpdateDaemonRequestSignature.ParseSignatureInput(request.SignatureInput);
        Match signatureMatch = HostUpdateDaemonRequestSignature.ParseSignature(request.Signature);
        if (!input.Success || !signatureMatch.Success)
        {
            return Unauthenticated("request_signature_malformed");
        }

        if (!string.Equals(request.ContentDigest, HostUpdateDaemonRequestSignature.ContentDigest(request.Body.Span), StringComparison.Ordinal))
        {
            return Unauthenticated("request_digest_mismatch");
        }

        string keyId = input.Groups["keyid"].Value;
        HostUpdateDaemonKeyRecord? record = findKey(keyId);
        if (record is null || !string.Equals(record.KeyId, keyId, StringComparison.Ordinal))
        {
            return Unauthenticated(HostUpdatePullReasons.UnknownHostIdentity);
        }

        long counter = long.Parse(request.Counter, CultureInfo.InvariantCulture);
        long createdSeconds = long.Parse(input.Groups["created"].Value, CultureInfo.InvariantCulture);
        string nonce = input.Groups["nonce"].Value;
        string parameters = request.SignatureInput[(HostUpdateDaemonProtocol.SignatureLabel.Length + 1)..];
        string signatureBase = HostUpdateDaemonRequestSignature.SignatureBase(
            request.Method, request.Path, request.Query, request.ContentDigest, counter, parameters);
        byte[] baseBytes = Encoding.UTF8.GetBytes(signatureBase);

        byte[] signatureBytes = new byte[64];
        if (!Convert.TryFromBase64String(signatureMatch.Groups["sig"].Value, signatureBytes, out int signatureLength)
            || signatureLength != signatureBytes.Length
            || !VerifySignature(record.PublicKey, baseBytes, signatureBytes))
        {
            return Unauthenticated("request_signature_invalid");
        }

        DateTimeOffset created;
        try
        {
            created = DateTimeOffset.FromUnixTimeSeconds(createdSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Unauthenticated("request_signature_malformed");
        }

        string digest = Convert.ToHexString(SHA256.HashData(baseBytes)).ToLowerInvariant();
        var result = new HostUpdateDaemonRequestVerification
        {
            Outcome = HostUpdateDaemonRequestOutcome.Accepted,
            ReasonCode = "accepted",
            KeyId = keyId,
            Nonce = nonce,
            Counter = counter,
            CreatedAt = created,
            RequestDigest = digest
        };

        if ((now - created).Duration() > HostUpdateDaemonProtocol.ClockSkew)
        {
            return result with { Outcome = HostUpdateDaemonRequestOutcome.OutsideTimeWindow, ReasonCode = "request_outside_time_window" };
        }

        if (nonces.Contains(keyId, nonce))
        {
            return result with { Outcome = HostUpdateDaemonRequestOutcome.Replay, ReasonCode = "request_replayed" };
        }

        switch (record.State)
        {
            case HostUpdateDaemonEnrollmentState.Revoked:
                return result with { Outcome = HostUpdateDaemonRequestOutcome.Revoked, ReasonCode = HostUpdatePullReasons.EnrollmentRevoked };
            case HostUpdateDaemonEnrollmentState.Quarantined:
                return result with { Outcome = HostUpdateDaemonRequestOutcome.Quarantined, ReasonCode = HostUpdatePullReasons.EnrollmentQuarantined };
            case HostUpdateDaemonEnrollmentState.Pending when !enrollmentStatusRequest:
                return result with { Outcome = HostUpdateDaemonRequestOutcome.PendingScope, ReasonCode = HostUpdatePullReasons.EnrollmentPending };
            case HostUpdateDaemonEnrollmentState.Pending:
            case HostUpdateDaemonEnrollmentState.Active:
            case HostUpdateDaemonEnrollmentState.Rotating:
                break;
            default:
                return Unauthenticated(HostUpdatePullReasons.UnknownHostIdentity);
        }

        if (now >= record.KeyExpiresAt)
        {
            return result with { Outcome = HostUpdateDaemonRequestOutcome.Expired, ReasonCode = HostUpdatePullReasons.EnrollmentExpired };
        }

        if (counter > record.HighWaterCounter)
        {
            if (record.HighWaterCreatedAt is { } highWaterCreated && created < highWaterCreated)
            {
                return result with { Outcome = HostUpdateDaemonRequestOutcome.Stale, ReasonCode = "request_timestamp_regressed" };
            }

            return result;
        }

        bool laterThanMark = record.HighWaterCreatedAt is { } markCreated && created > markCreated;
        bool conflictingDigest = counter == record.HighWaterCounter
            && record.HighWaterRequestDigest is not null
            && !string.Equals(record.HighWaterRequestDigest, digest, StringComparison.Ordinal);
        if (laterThanMark || conflictingDigest)
        {
            return result with { Outcome = HostUpdateDaemonRequestOutcome.ForkDetected, ReasonCode = "request_fork_detected" };
        }

        return result with { Outcome = HostUpdateDaemonRequestOutcome.Stale, ReasonCode = "request_counter_stale" };
    }

    internal static bool VerifySignature(ReadOnlyMemory<byte> subjectPublicKeyInfo, byte[] data, byte[] signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo.Span, out int read);
            if (read != subjectPublicKeyInfo.Length || !IsP256(key))
            {
                return false;
            }

            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    internal static bool IsP256(ECDsa key)
    {
        ECParameters parameters = key.ExportParameters(includePrivateParameters: false);
        return parameters.Curve.IsNamed
            && (string.Equals(parameters.Curve.Oid.Value, ECCurve.NamedCurves.nistP256.Oid.Value, StringComparison.Ordinal)
                || string.Equals(parameters.Curve.Oid.FriendlyName, ECCurve.NamedCurves.nistP256.Oid.FriendlyName, StringComparison.OrdinalIgnoreCase));
    }

    private static HostUpdateDaemonRequestVerification Unauthenticated(string reason) =>
        new() { Outcome = HostUpdateDaemonRequestOutcome.Unauthenticated, ReasonCode = reason };
}
