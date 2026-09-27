using System.Security.Cryptography;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.HostUpdates.PullApproval;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates.PullApproval;

public sealed class HostUpdateDaemonResponseVerifierTests : IDisposable
{
    private const string ResponseKeyId = "api-response-key-0001";
    private const string KeyId = "daemon-key-0000000001";
    private const string RequestNonce = "AAAAAAAAAAAAAAAAAAAAAA";
    private static readonly string InstallationId = new('1', 32);
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly ECDsa _responseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _responseKey.Dispose();
        _otherKey.Dispose();
    }

    [Fact]
    public void SignedResponse_RoundTrips_AndPayloadIsTyped()
    {
        HostUpdateDaemonSignedResponseDto response = Sign(Envelope());

        HostUpdateDaemonResponseVerification result = HostUpdateDaemonResponseVerifier.Verify(response, Expectation(), Now);

        result.Outcome.Should().Be(HostUpdateDaemonResponseOutcome.Accepted);
        result.TryGetPayload(HostUpdateDaemonResponseType.EnrollmentStatus, out HostUpdateDaemonEnrollmentStatusDto? payload).Should().BeTrue();
        payload!.State.Should().Be(HostUpdateDaemonEnrollmentState.Active);
        result.TryGetPayload(HostUpdateDaemonResponseType.Approval, out HostUpdatePullApprovalResponseDto? _).Should().BeFalse();
    }

    [Fact]
    public void UnsignedResponse_IsDiscarded()
    {
        HostUpdateDaemonSignedResponseDto response = Sign(Envelope()) with { Signature = "" };

        Verify(response).Should().Be(HostUpdateDaemonResponseOutcome.Discard);
        HostUpdateDaemonResponseVerifier.Verify(null, Expectation(), Now).Outcome.Should().Be(HostUpdateDaemonResponseOutcome.Discard);
    }

    [Fact]
    public void UnpinnedResponseKeyId_IsDiscarded()
    {
        Verify(Sign(Envelope()) with { ResponseKeyId = "api-response-key-0002" }).Should().Be(HostUpdateDaemonResponseOutcome.Discard);
    }

    [Fact]
    public void ResponseSignedByAnotherKey_IsDiscarded()
    {
        HostUpdateDaemonSignedResponseDto response = HostUpdateDaemonResponseSigner.Sign(Envelope(), _otherKey, ResponseKeyId);

        HostUpdateDaemonResponseVerification result = HostUpdateDaemonResponseVerifier.Verify(response, Expectation(), Now);

        result.Outcome.Should().Be(HostUpdateDaemonResponseOutcome.Discard);
        result.ReasonCode.Should().Be("response_signature_invalid");
    }

    [Fact]
    public void TamperedContent_IsDiscarded()
    {
        HostUpdateDaemonSignedResponseDto response = Sign(Envelope());

        Verify(response with { SignedContent = response.SignedContent.Replace("Active", "Revoked", StringComparison.Ordinal) })
            .Should().Be(HostUpdateDaemonResponseOutcome.Discard);
    }

    [Fact]
    public void ResponseForAnotherRequest_IsDiscarded()
    {
        HostUpdateDaemonResponseVerification result = HostUpdateDaemonResponseVerifier.Verify(
            Sign(Envelope() with { RequestNonce = "BBBBBBBBBBBBBBBBBBBBBB" }), Expectation(), Now);

        result.Outcome.Should().Be(HostUpdateDaemonResponseOutcome.Discard);
        result.ReasonCode.Should().Be("response_nonce_mismatch");
    }

    [Fact]
    public void StaleIssuedAt_IsDiscarded()
    {
        Verify(Sign(Envelope() with { IssuedAt = Now.AddMinutes(-5) })).Should().Be(HostUpdateDaemonResponseOutcome.Discard);
    }

    [Fact]
    public void EpochChange_NeedsOperator()
    {
        Verify(Sign(Envelope() with { EnrollmentEpoch = 4 })).Should().Be(HostUpdateDaemonResponseOutcome.NeedsOperator);
    }

    [Fact]
    public void InstallationChange_NeedsOperator()
    {
        Verify(Sign(Envelope() with { InstallationId = new string('2', 32) })).Should().Be(HostUpdateDaemonResponseOutcome.NeedsOperator);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(12)]
    public void AcknowledgedCounterRegressionOrOverrun_NeedsOperator(long acknowledged)
    {
        HostUpdateDaemonResponseVerification result = HostUpdateDaemonResponseVerifier.Verify(
            Sign(Envelope() with { AcknowledgedCounter = acknowledged }), Expectation(), Now);

        result.Outcome.Should().Be(HostUpdateDaemonResponseOutcome.NeedsOperator);
        result.ReasonCode.Should().Be("api_counter_rollback");
    }

    [Fact]
    public void EnrollmentRevisionRegression_NeedsOperator()
    {
        Verify(Sign(Envelope() with { EnrollmentStateRevision = 6 })).Should().Be(HostUpdateDaemonResponseOutcome.NeedsOperator);
    }

    private HostUpdateDaemonResponseOutcome Verify(HostUpdateDaemonSignedResponseDto response) =>
        HostUpdateDaemonResponseVerifier.Verify(response, Expectation(), Now).Outcome;

    private HostUpdateDaemonSignedResponseDto Sign<T>(HostUpdateDaemonResponseEnvelope<T> envelope) =>
        HostUpdateDaemonResponseSigner.Sign(envelope, _responseKey, ResponseKeyId);

    private static HostUpdateDaemonResponseEnvelope<HostUpdateDaemonEnrollmentStatusDto> Envelope() => new()
    {
        ProtocolVersion = HostUpdateDaemonProtocol.Version,
        ResponseType = HostUpdateDaemonResponseType.EnrollmentStatus,
        InstallationId = InstallationId,
        KeyId = KeyId,
        EnrollmentEpoch = 3,
        RequestNonce = RequestNonce,
        AcknowledgedCounter = 10,
        EnrollmentStateRevision = 7,
        IssuedAt = Now,
        Payload = new HostUpdateDaemonEnrollmentStatusDto
        {
            State = HostUpdateDaemonEnrollmentState.Active,
            KeyExpiresAt = Now.AddDays(30),
            Reasons = []
        }
    };

    private HostUpdateDaemonResponseExpectation Expectation() => new()
    {
        PinnedResponseKeyId = ResponseKeyId,
        PinnedResponseKey = _responseKey.ExportSubjectPublicKeyInfo(),
        InstallationId = InstallationId,
        KeyId = KeyId,
        EnrollmentEpoch = 3,
        RequestNonce = RequestNonce,
        RequestCounter = 11,
        LastAcknowledgedCounter = 10,
        LastEnrollmentStateRevision = 7
    };
}
