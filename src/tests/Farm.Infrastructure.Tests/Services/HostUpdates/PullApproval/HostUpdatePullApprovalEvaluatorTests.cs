using System.Security.Cryptography;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.HostUpdates.PullApproval;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates.PullApproval;

public sealed class HostUpdatePullApprovalEvaluatorTests
{
    private const string KeyId = "daemon-key-0000000001";
    private const string ApprovalId = "approval-000000000001";
    private static readonly string InstallationId = new('1', 32);
    private static readonly string ManifestDigest = "sha256:" + new string('a', 64);
    private static readonly string Topology = "sha256:" + new string('b', 64);
    private static readonly string Configuration = "sha256:" + new string('c', 64);
    private static readonly string Schema = "sha256:" + new string('d', 64);
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ManualApproval_MatchingCurrentState_IsApproved_WithImmutableIdentityOnly()
    {
        HostUpdatePullApprovalResponseDto result = HostUpdatePullApprovalEvaluator.Evaluate(Context());

        result.Decision.Should().Be(HostUpdatePullApprovalDecision.Approved);
        result.Reasons.Should().BeEmpty();
        result.Approval!.ReleaseId.Should().Be("v1.5.0");
        result.Approval.ManifestDigest.Should().Be(ManifestDigest);
        result.Approval.Authorization.Reauthenticated.Should().BeTrue();
    }

    [Fact]
    public void NoCandidate_ReturnsNone()
    {
        HostUpdatePullApprovalResponseDto result = HostUpdatePullApprovalEvaluator.Evaluate(Context() with { Candidate = null });

        result.Decision.Should().Be(HostUpdatePullApprovalDecision.None);
        result.Approval.Should().BeNull();
        result.Reasons.Should().Equal(HostUpdatePullReasons.NoApproval);
    }

    [Fact]
    public void AutomaticApproval_IsAlwaysDenied_WhileRuntimeDisabled()
    {
        HostUpdatePullRuntimeGate.AutomaticUpdatesEnabled.Should().BeFalse();

        HostUpdatePullApprovalResponseDto result = HostUpdatePullApprovalEvaluator.Evaluate(
            Context(Approval() with { Origin = HostUpdateApprovalOrigin.Automatic }) with { AutomaticPolicyEnabled = true });

        result.Decision.Should().Be(HostUpdatePullApprovalDecision.Denied);
        result.Approval.Should().BeNull();
        result.Reasons.Should().Equal(HostUpdatePullReasons.AutomaticUpdatesRuntimeDisabled);
    }

    [Fact]
    public void UnknownHost_IsDenied_WithoutInspectingCandidate()
    {
        HostUpdatePullApprovalResponseDto result = HostUpdatePullApprovalEvaluator.Evaluate(Context() with { Enrollment = null });

        result.Decision.Should().Be(HostUpdatePullApprovalDecision.Denied);
        result.Reasons.Should().Equal(HostUpdatePullReasons.UnknownHostIdentity);
    }

    [Theory]
    [InlineData(HostUpdateDaemonEnrollmentState.Revoked, HostUpdatePullReasons.EnrollmentRevoked)]
    [InlineData(HostUpdateDaemonEnrollmentState.Quarantined, HostUpdatePullReasons.EnrollmentQuarantined)]
    [InlineData(HostUpdateDaemonEnrollmentState.Pending, HostUpdatePullReasons.EnrollmentPending)]
    public void NonActiveEnrollment_IsDenied(HostUpdateDaemonEnrollmentState state, string reason)
    {
        HostUpdatePullApprovalContext context = Context();

        AssertDenied(context with { Enrollment = context.Enrollment! with { State = state } }, reason);
    }

    [Fact]
    public void ExpiredEnrollment_IsDenied()
    {
        HostUpdatePullApprovalContext context = Context();

        AssertDenied(context with { Enrollment = context.Enrollment! with { KeyExpiresAt = Now } }, HostUpdatePullReasons.EnrollmentExpired);
    }

    [Fact]
    public void MissingOrMismatchedInstallation_IsDenied()
    {
        AssertDenied(Context() with { StrictInstallationId = null }, HostUpdatePullReasons.InstallationIdentityUnavailable);
        AssertDenied(Context() with { StrictInstallationId = new string('2', 32) }, HostUpdatePullReasons.InstallationMismatch);
    }

    [Fact]
    public void KillSwitch_IsDenied_EvenWithoutCandidate()
    {
        AssertDenied(Context() with { KillSwitchActive = true }, HostUpdatePullReasons.KillSwitchActive);
        AssertDenied(Context() with { KillSwitchActive = true, Candidate = null }, HostUpdatePullReasons.KillSwitchActive);
    }

    public static TheoryData<string> CandidateDenials() => new(
    [
        HostUpdatePullReasons.ApprovalRevoked,
        HostUpdatePullReasons.ApprovalConsumed,
        HostUpdatePullReasons.ApprovalExpired,
        HostUpdatePullReasons.ApprovalNotYetValid,
        HostUpdatePullReasons.ApprovalLifetimeExceeded,
        HostUpdatePullReasons.ApprovalBindingMismatch,
        HostUpdatePullReasons.ApprovalIdentityInvalid,
        HostUpdatePullReasons.PolicyChanged,
        HostUpdatePullReasons.TrustChanged,
        HostUpdatePullReasons.HostPolicyChanged,
        HostUpdatePullReasons.TopologyDrift,
        HostUpdatePullReasons.ConfigurationDrift,
        HostUpdatePullReasons.SchemaDrift,
        HostUpdatePullReasons.ChannelMismatch,
        HostUpdatePullReasons.DowngradeRejected,
        HostUpdatePullReasons.InstalledReleaseUnknown,
        HostUpdatePullReasons.AuthorizationMissing,
        HostUpdatePullReasons.RecoveryPrerequisitesMissing
    ]);

    [Theory]
    [MemberData(nameof(CandidateDenials))]
    public void EachStaleRevokedDriftedOrUnauthorizedCondition_FailsClosed(string reason)
    {
        HostUpdateStoredApproval approval = Approval();
        HostUpdatePullApprovalContext context = reason switch
        {
            HostUpdatePullReasons.ApprovalRevoked => Context(approval with { Revoked = true }),
            HostUpdatePullReasons.ApprovalConsumed => Context(approval with { Consumed = true }),
            HostUpdatePullReasons.ApprovalExpired => Context() with { Now = approval.ExpiresAt },
            HostUpdatePullReasons.ApprovalNotYetValid => Context(approval with { IssuedAt = Now.AddMinutes(2), ExpiresAt = Now.AddMinutes(4) }),
            HostUpdatePullReasons.ApprovalLifetimeExceeded => Context(approval with { ExpiresAt = approval.IssuedAt.AddMinutes(6) }),
            HostUpdatePullReasons.ApprovalBindingMismatch => Context(approval with { EnrollmentEpoch = 2 }),
            HostUpdatePullReasons.ApprovalIdentityInvalid => Context(approval with { ManifestDigest = "https://example.invalid/manifest" }),
            HostUpdatePullReasons.PolicyChanged => Context() with { PolicyRevision = 5 },
            HostUpdatePullReasons.TrustChanged => Context() with { TrustRevision = 9 },
            HostUpdatePullReasons.HostPolicyChanged => Context() with { ReportedHostPolicyRevision = 13 },
            HostUpdatePullReasons.TopologyDrift => Context() with { TopologyFingerprint = "sha256:" + new string('e', 64) },
            HostUpdatePullReasons.ConfigurationDrift => Context() with { ConfigurationFingerprint = "sha256:" + new string('e', 64) },
            HostUpdatePullReasons.SchemaDrift => Context() with { SchemaFingerprint = "sha256:" + new string('e', 64) },
            HostUpdatePullReasons.ChannelMismatch => Context() with { Channel = "insider" },
            HostUpdatePullReasons.DowngradeRejected => Context() with { InstalledReleaseId = "v1.5.0" },
            HostUpdatePullReasons.InstalledReleaseUnknown => Context() with { InstalledReleaseId = null },
            HostUpdatePullReasons.AuthorizationMissing => Context(approval with { Authorization = approval.Authorization with { Reauthenticated = false } }),
            HostUpdatePullReasons.RecoveryPrerequisitesMissing => Context() with { RecoveryPrerequisitesMet = false },
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };

        AssertDenied(context, reason);
    }

    [Fact]
    public void ManualApproval_WithoutExecutePermission_IsDenied()
    {
        HostUpdateStoredApproval approval = Approval();

        AssertDenied(Context(approval with { Authorization = approval.Authorization with { ExecutePermission = false } }), HostUpdatePullReasons.AuthorizationMissing);
    }

    [Theory]
    [InlineData(HostUpdateApprovalOrigin.ManualOneTime)]
    [InlineData(HostUpdateApprovalOrigin.Automatic)]
    public void Approval_WithoutFarmAdmin_IsDenied(HostUpdateApprovalOrigin origin)
    {
        HostUpdateStoredApproval approval = Approval() with { Origin = origin };

        AssertDenied(Context(approval with { Authorization = approval.Authorization with { FarmAdmin = false } }), HostUpdatePullReasons.AuthorizationMissing);
    }

    [Theory]
    [InlineData(HostUpdateApprovalOrigin.ManualOneTime)]
    [InlineData(HostUpdateApprovalOrigin.Automatic)]
    public void Approval_AuthorizedAfterIssuance_IsDenied(HostUpdateApprovalOrigin origin)
    {
        HostUpdateStoredApproval approval = Approval() with { Origin = origin };

        AssertDenied(
            Context(approval with { Authorization = approval.Authorization with { AuthorizedAt = approval.IssuedAt.AddSeconds(1) } }),
            HostUpdatePullReasons.AuthorizationMissing);
    }

    [Fact]
    public void AutomaticApproval_WithAutomaticPolicyDisabled_IsDenied()
    {
        HostUpdatePullApprovalResponseDto result = HostUpdatePullApprovalEvaluator.Evaluate(
            Context(Approval() with { Origin = HostUpdateApprovalOrigin.Automatic }) with { AutomaticPolicyEnabled = false });

        result.Decision.Should().Be(HostUpdatePullApprovalDecision.Denied);
        result.Reasons.Should().BeEquivalentTo(
            [HostUpdatePullReasons.AutomaticPolicyDisabled, HostUpdatePullReasons.AutomaticUpdatesRuntimeDisabled]);
    }

    [Fact]
    public void RecoveryPlan_RequiresPlanDigest()
    {
        AssertDenied(Context(Approval() with { TargetKind = HostUpdateApprovalTargetKind.SignedRecoveryPlan }), HostUpdatePullReasons.ApprovalIdentityInvalid);
    }

    [Fact]
    public void Confirm_MatchingRequest_IsConfirmed()
    {
        HostUpdateApprovalConfirmationDto result = HostUpdatePullApprovalEvaluator.Confirm(Context(), Confirmation());

        result.Decision.Should().Be(HostUpdateApprovalConfirmationDecision.Confirmed);
        result.ValidUntil.Should().Be(Approval().ExpiresAt);
    }

    [Theory]
    [InlineData("approvalId")]
    [InlineData("manifestDigest")]
    [InlineData("planDigest")]
    [InlineData("hostPolicyRevision")]
    [InlineData("checkpoint")]
    public void Confirm_MismatchedRequest_IsDenied(string field)
    {
        HostUpdateApprovalConfirmationRequestDto request = field switch
        {
            "approvalId" => Confirmation() with { ApprovalId = "approval-000000000002" },
            "manifestDigest" => Confirmation() with { ManifestDigest = "sha256:" + new string('f', 64) },
            "planDigest" => Confirmation() with { PlanDigest = "sha256:" + new string('f', 64) },
            "hostPolicyRevision" => Confirmation() with { HostPolicyRevision = 13 },
            _ => Confirmation() with { Checkpoint = "/var/lib/printfarmer" }
        };

        HostUpdateApprovalConfirmationDto result = HostUpdatePullApprovalEvaluator.Confirm(Context(), request);

        result.Decision.Should().Be(HostUpdateApprovalConfirmationDecision.Denied);
        result.ValidUntil.Should().BeNull();
        result.Reasons.Should().Contain(HostUpdatePullReasons.ConfirmationMismatch);
    }

    [Fact]
    public void Confirm_AfterRevocation_IsDenied()
    {
        HostUpdateApprovalConfirmationDto result = HostUpdatePullApprovalEvaluator.Confirm(Context(Approval() with { Revoked = true }), Confirmation());

        result.Decision.Should().Be(HostUpdateApprovalConfirmationDecision.Denied);
        result.Reasons.Should().Contain(HostUpdatePullReasons.ApprovalRevoked);
    }

    [Fact]
    public void Confirm_WithoutApproval_IsDenied()
    {
        HostUpdateApprovalConfirmationDto result = HostUpdatePullApprovalEvaluator.Confirm(Context() with { Candidate = null }, Confirmation());

        result.Decision.Should().Be(HostUpdateApprovalConfirmationDecision.Denied);
        result.Reasons.Should().Equal(HostUpdatePullReasons.NoApproval);
    }

    private static void AssertDenied(HostUpdatePullApprovalContext context, string reason)
    {
        HostUpdatePullApprovalResponseDto result = HostUpdatePullApprovalEvaluator.Evaluate(context);

        result.Decision.Should().Be(HostUpdatePullApprovalDecision.Denied);
        result.Approval.Should().BeNull();
        result.Reasons.Should().Contain(reason);
    }

    private static HostUpdateApprovalConfirmationRequestDto Confirmation() => new()
    {
        ApprovalId = ApprovalId,
        ManifestDigest = ManifestDigest,
        PlanDigest = null,
        HostPolicyRevision = 12,
        Checkpoint = "pre_pull"
    };

    private static HostUpdatePullApprovalContext Context(HostUpdateStoredApproval? candidate = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new HostUpdatePullApprovalContext
        {
            Enrollment = new HostUpdateDaemonKeyRecord
            {
                KeyId = KeyId,
                InstallationId = InstallationId,
                EnrollmentEpoch = 3,
                State = HostUpdateDaemonEnrollmentState.Active,
                PublicKey = key.ExportSubjectPublicKeyInfo(),
                KeyExpiresAt = Now.AddDays(30),
                EnrollmentStateRevision = 7,
                HighWaterCounter = 10,
                HighWaterCreatedAt = Now.AddSeconds(-30),
                HighWaterRequestDigest = null
            },
            StrictInstallationId = InstallationId,
            KillSwitchActive = false,
            Channel = "stable",
            PolicyRevision = 4,
            TrustRevision = 8,
            TopologyFingerprint = Topology,
            ConfigurationFingerprint = Configuration,
            SchemaFingerprint = Schema,
            ReportedHostPolicyRevision = 12,
            InstalledReleaseId = "v1.4.2",
            AutomaticPolicyEnabled = false,
            RecoveryPrerequisitesMet = true,
            Candidate = candidate ?? Approval(),
            Now = Now
        };
    }

    private static HostUpdateStoredApproval Approval() => new()
    {
        ApprovalId = ApprovalId,
        TargetKind = HostUpdateApprovalTargetKind.SignedRelease,
        Origin = HostUpdateApprovalOrigin.ManualOneTime,
        ReleaseId = "v1.5.0",
        ManifestDigest = ManifestDigest,
        PlanDigest = null,
        Channel = "stable",
        InstallationId = InstallationId,
        EnrollmentEpoch = 3,
        KeyId = KeyId,
        PolicyRevision = 4,
        TrustRevision = 8,
        HostPolicyRevision = 12,
        TopologyFingerprint = Topology,
        ConfigurationFingerprint = Configuration,
        SchemaFingerprint = Schema,
        IssuedAt = Now.AddMinutes(-1),
        ExpiresAt = Now.AddMinutes(3),
        Revoked = false,
        Consumed = false,
        Authorization = new HostUpdateApprovalAuthorizationEvidence
        {
            AuthorizationRevision = 1,
            AuthorizedAt = Now.AddMinutes(-1),
            Reauthenticated = true,
            FarmAdmin = true,
            ExecutePermission = true
        }
    };
}
