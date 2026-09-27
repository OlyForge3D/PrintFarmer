using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.HostUpdates.PullApproval;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates.PullApproval;

public sealed class HostUpdateDaemonRequestVerifierTests : IDisposable
{
    private const string KeyId = "daemon-key-0000000001";
    private const string Path = HostUpdateDaemonApiRoutes.Approval;
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Ledger _nonces = new();

    public void Dispose()
    {
        _key.Dispose();
        _otherKey.Dispose();
    }

    [Fact]
    public void ValidSignedRequest_IsAccepted_WithHighWaterCommitData()
    {
        HostUpdateDaemonSignedRequest request = Sign(counter: 11, created: Now, nonce: Nonce(1));

        HostUpdateDaemonRequestVerification result = Verify(request, Record());

        result.Outcome.Should().Be(HostUpdateDaemonRequestOutcome.Accepted);
        result.Counter.Should().Be(11);
        result.Nonce.Should().Be(Nonce(1));
        result.RequestDigest.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void ForgedSignature_IsUnauthenticated()
    {
        HostUpdateDaemonSignedRequest request = Sign(counter: 11, created: Now, nonce: Nonce(1), signer: _otherKey);

        Verify(request, Record()).Outcome.Should().Be(HostUpdateDaemonRequestOutcome.Unauthenticated);
    }

    [Fact]
    public void TamperedBody_IsUnauthenticated()
    {
        HostUpdateDaemonSignedRequest request = Sign(counter: 11, created: Now, nonce: Nonce(1), body: "{\"a\":1}");

        HostUpdateDaemonRequestVerification result = Verify(request with { Body = Encoding.UTF8.GetBytes("{\"a\":2}") }, Record());

        result.Outcome.Should().Be(HostUpdateDaemonRequestOutcome.Unauthenticated);
        result.ReasonCode.Should().Be("request_digest_mismatch");
    }

    [Fact]
    public void TamperedPathOrCounter_IsUnauthenticated()
    {
        HostUpdateDaemonSignedRequest request = Sign(counter: 11, created: Now, nonce: Nonce(1));

        Verify(request with { Path = HostUpdateDaemonApiRoutes.Status }, Record()).Outcome.Should().Be(HostUpdateDaemonRequestOutcome.Unauthenticated);
        Verify(request with { Counter = "99" }, Record()).Outcome.Should().Be(HostUpdateDaemonRequestOutcome.Unauthenticated);
    }

    [Theory]
    [InlineData("pf=(\"@method\" \"@path\");created=1790510400;nonce=\"AAAAAAAAAAAAAAAAAAAAAA\";keyid=\"daemon-key-0000000001\";alg=\"ecdsa-p256-sha256\";tag=\"printfarmer-host-update-v1\"")]
    [InlineData("pf=(\"@method\" \"@path\" \"@query\" \"content-digest\" \"printfarmer-request-counter\");created=1790510400;nonce=\"AAAAAAAAAAAAAAAAAAAAAA\";keyid=\"daemon-key-0000000001\";alg=\"hmac-sha256\";tag=\"printfarmer-host-update-v1\"")]
    [InlineData("sig=(\"@method\" \"@path\" \"@query\" \"content-digest\" \"printfarmer-request-counter\");created=1790510400;nonce=\"AAAAAAAAAAAAAAAAAAAAAA\";keyid=\"daemon-key-0000000001\";alg=\"ecdsa-p256-sha256\";tag=\"printfarmer-host-update-v1\"")]
    public void NonProfileSignatureInput_IsUnauthenticated(string signatureInput)
    {
        HostUpdateDaemonSignedRequest request = Sign(counter: 11, created: Now, nonce: Nonce(1));

        Verify(request with { SignatureInput = signatureInput }, Record()).Outcome.Should().Be(HostUpdateDaemonRequestOutcome.Unauthenticated);
    }

    [Fact]
    public void UnknownKey_IsUnauthenticated()
    {
        HostUpdateDaemonSignedRequest request = Sign(counter: 11, created: Now, nonce: Nonce(1));

        HostUpdateDaemonRequestVerification result = HostUpdateDaemonRequestVerifier.Verify(request, _ => null, _nonces, Now, enrollmentStatusRequest: false);

        result.Outcome.Should().Be(HostUpdateDaemonRequestOutcome.Unauthenticated);
        result.ReasonCode.Should().Be(HostUpdatePullReasons.UnknownHostIdentity);
        result.Authenticated.Should().BeFalse();
    }

    [Theory]
    [InlineData(61)]
    [InlineData(-61)]
    public void TimestampOutsideWindow_IsRejected(int offsetSeconds)
    {
        HostUpdateDaemonSignedRequest request = Sign(counter: 11, created: Now.AddSeconds(offsetSeconds), nonce: Nonce(1));

        HostUpdateDaemonRequestVerification result = Verify(request, Record());

        result.Outcome.Should().Be(HostUpdateDaemonRequestOutcome.OutsideTimeWindow);
        result.Authenticated.Should().BeTrue(because: "a key-holder rejection is answered with a signed response");
        result.Accepted.Should().BeFalse();
    }

    [Fact]
    public void ReplayedNonce_IsReplay_AndNeverFork()
    {
        HostUpdateDaemonSignedRequest request = Sign(counter: 5, created: Now, nonce: Nonce(1));
        _nonces.Add(KeyId, Nonce(1));

        // Even though counter 5 is below the mark with a later timestamp, the nonce check runs first.
        HostUpdateDaemonRequestVerification result = Verify(request, Record(highWater: 10, highWaterCreated: Now.AddSeconds(-30)));

        result.Outcome.Should().Be(HostUpdateDaemonRequestOutcome.Replay);
        result.Authenticated.Should().BeTrue(because: "a key-holder rejection is answered with a signed response");
        result.Accepted.Should().BeFalse();
    }

    [Fact]
    public void NonCanonicalBase64Signature_IsUnauthenticated_NotAnException()
    {
        HostUpdateDaemonSignedRequest request = Sign(counter: 11, created: Now, nonce: Nonce(10));
        string malformed = "pf=:" + new string('A', 85) + "B==:";

        Verify(request with { Signature = malformed }, Record()).Outcome.Should().Be(HostUpdateDaemonRequestOutcome.Unauthenticated);
    }

    [Fact]
    public void CounterBelowMark_WithEarlierTimestamp_IsStaleNotFork()
    {
        HostUpdateDaemonSignedRequest request = Sign(counter: 9, created: Now.AddSeconds(-20), nonce: Nonce(2));

        HostUpdateDaemonRequestVerification result = Verify(request, Record(highWater: 10, highWaterCreated: Now.AddSeconds(-10)));

        result.Outcome.Should().Be(HostUpdateDaemonRequestOutcome.Stale);
    }

    [Fact]
    public void CounterBelowMark_WithLaterTimestamp_IsForkEvidence()
    {
        HostUpdateDaemonSignedRequest request = Sign(counter: 9, created: Now, nonce: Nonce(3));

        HostUpdateDaemonRequestVerification result = Verify(request, Record(highWater: 10, highWaterCreated: Now.AddSeconds(-10)));

        result.Outcome.Should().Be(HostUpdateDaemonRequestOutcome.ForkDetected);
    }

    [Fact]
    public void SameCounter_WithDifferentDigest_IsForkEvidence()
    {
        HostUpdateDaemonSignedRequest request = Sign(counter: 10, created: Now, nonce: Nonce(4));

        HostUpdateDaemonRequestVerification result = Verify(request, Record(highWater: 10, highWaterCreated: Now, highWaterDigest: new string('a', 64)));

        result.Outcome.Should().Be(HostUpdateDaemonRequestOutcome.ForkDetected);
    }

    [Fact]
    public void HigherCounter_WithRegressedTimestamp_IsStale()
    {
        HostUpdateDaemonSignedRequest request = Sign(counter: 11, created: Now.AddSeconds(-20), nonce: Nonce(5));

        Verify(request, Record(highWater: 10, highWaterCreated: Now.AddSeconds(-5))).Outcome.Should().Be(HostUpdateDaemonRequestOutcome.Stale);
    }

    [Theory]
    [InlineData(HostUpdateDaemonEnrollmentState.Revoked, HostUpdateDaemonRequestOutcome.Revoked)]
    [InlineData(HostUpdateDaemonEnrollmentState.Quarantined, HostUpdateDaemonRequestOutcome.Quarantined)]
    [InlineData(HostUpdateDaemonEnrollmentState.Pending, HostUpdateDaemonRequestOutcome.PendingScope)]
    public void NonActiveKeys_AreAuthenticatedRejections(HostUpdateDaemonEnrollmentState state, HostUpdateDaemonRequestOutcome expected)
    {
        HostUpdateDaemonSignedRequest request = Sign(counter: 11, created: Now, nonce: Nonce(6));

        HostUpdateDaemonRequestVerification result = Verify(request, Record(state: state));

        result.Outcome.Should().Be(expected);
        result.Authenticated.Should().BeTrue();
        result.Accepted.Should().BeFalse();
    }

    [Fact]
    public void PendingKey_MayOnlyPollEnrollmentStatus()
    {
        HostUpdateDaemonSignedRequest request = Sign(counter: 11, created: Now, nonce: Nonce(7), path: HostUpdateDaemonApiRoutes.Enrollment);

        HostUpdateDaemonRequestVerification result = HostUpdateDaemonRequestVerifier.Verify(
            request, _ => Record(state: HostUpdateDaemonEnrollmentState.Pending), _nonces, Now, enrollmentStatusRequest: true);

        result.Outcome.Should().Be(HostUpdateDaemonRequestOutcome.Accepted);
    }

    [Fact]
    public void ExpiredKey_IsRejected()
    {
        HostUpdateDaemonSignedRequest request = Sign(counter: 11, created: Now, nonce: Nonce(8));

        Verify(request, Record() with { KeyExpiresAt = Now }).Outcome.Should().Be(HostUpdateDaemonRequestOutcome.Expired);
    }

    [Fact]
    public void NonP256PublicKey_IsUnauthenticated()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        HostUpdateDaemonSignedRequest request = Sign(counter: 11, created: Now, nonce: Nonce(9));

        Verify(request, Record() with { PublicKey = p384.ExportSubjectPublicKeyInfo() }).Outcome.Should().Be(HostUpdateDaemonRequestOutcome.Unauthenticated);
    }

    private static string Nonce(int seed) => Convert.ToBase64String(SHA256.HashData(BitConverter.GetBytes(seed)))[..22].Replace('+', '-').Replace('/', '_');

    private HostUpdateDaemonRequestVerification Verify(HostUpdateDaemonSignedRequest request, HostUpdateDaemonKeyRecord record) =>
        HostUpdateDaemonRequestVerifier.Verify(request, id => id == record.KeyId ? record : null, _nonces, Now, enrollmentStatusRequest: false);

    private HostUpdateDaemonSignedRequest Sign(long counter, DateTimeOffset created, string nonce, ECDsa? signer = null, string body = "", string path = Path) =>
        HostUpdateDaemonRequestSignature.Sign("GET", path, "?", Encoding.UTF8.GetBytes(body), counter, created, nonce, KeyId, signer ?? _key);

    private HostUpdateDaemonKeyRecord Record(
        HostUpdateDaemonEnrollmentState state = HostUpdateDaemonEnrollmentState.Active,
        long highWater = 10,
        DateTimeOffset? highWaterCreated = null,
        string? highWaterDigest = null) => new()
        {
            KeyId = KeyId,
            InstallationId = new string('1', 32),
            EnrollmentEpoch = 3,
            State = state,
            PublicKey = _key.ExportSubjectPublicKeyInfo(),
            KeyExpiresAt = Now.AddDays(30),
            EnrollmentStateRevision = 7,
            HighWaterCounter = highWater,
            HighWaterCreatedAt = highWaterCreated ?? Now.AddSeconds(-30),
            HighWaterRequestDigest = highWaterDigest
        };

    private sealed class Ledger : IHostUpdateDaemonNonceLedger
    {
        private readonly HashSet<string> _seen = [];

        public void Add(string keyId, string nonce) => _seen.Add(keyId + "/" + nonce);

        public bool Contains(string keyId, string nonce) => _seen.Contains(keyId + "/" + nonce);
    }
}
