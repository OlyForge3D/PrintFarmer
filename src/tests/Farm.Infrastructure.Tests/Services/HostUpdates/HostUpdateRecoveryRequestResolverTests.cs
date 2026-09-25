using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// The single journal-binding rule shared by the admin API and the host-local recovery CLI
/// (issue #2980): neither surface may reconstruct a failed request from weaker evidence.
/// </summary>
public sealed class HostUpdateRecoveryRequestResolverTests
{
    [Fact]
    public void Empty_history_is_no_history()
    {
        HostUpdateRecoveryRequestResolver.Resolve([], null).ErrorCode.Should().Be(HostUpdateRecoveryRequestResolver.NoHistory);
    }

    [Fact]
    public void Last_activity_not_in_recovery_is_refused()
    {
        HostUpdateExecutionRequest request = Request();
        HostUpdateRecoveryRequestResolution result = HostUpdateRecoveryRequestResolver.Resolve(
            [Bound(request, HostUpdateExecutionState.RecoveryRequired, "failure"), Bound(request, HostUpdateExecutionState.Completed, "installed-state:after")],
            null);

        result.ErrorCode.Should().Be(HostUpdateRecoveryRequestResolver.NotInRecovery);
    }

    [Fact]
    public void Missing_binding_on_any_activity_is_refused()
    {
        HostUpdateExecutionRequest request = Request();
        HostUpdateRecoveryRequestResolution result = HostUpdateRecoveryRequestResolver.Resolve(
            [
                new HostUpdateExecutionActivity("a", request.ReleaseId, HostUpdateExecutionState.Applying, "apply:before", DateTimeOffset.UtcNow),
                Bound(request, HostUpdateExecutionState.RecoveryRequired, "failure"),
            ],
            null);

        result.ErrorCode.Should().Be(HostUpdateRecoveryRequestResolver.BindingMismatch);
    }

    [Fact]
    public void Completely_unbound_history_is_binding_missing()
    {
        HostUpdateRecoveryRequestResolution result = HostUpdateRecoveryRequestResolver.Resolve(
            [new HostUpdateExecutionActivity("a", "stable:1.2.3", HostUpdateExecutionState.RecoveryRequired, "failure", DateTimeOffset.UtcNow)],
            null);

        result.ErrorCode.Should().Be(HostUpdateRecoveryRequestResolver.BindingMissing);
    }

    [Fact]
    public void Binding_hash_that_does_not_match_the_recorded_request_is_refused()
    {
        HostUpdateExecutionRequest request = Request();
        HostUpdateExecutionActivity tampered = Bound(request, HostUpdateExecutionState.RecoveryRequired, "failure") with
        {
            RequestBinding = request with { AuthenticatedSequence = 2 },
        };

        HostUpdateRecoveryRequestResolver.Resolve([tampered], null).ErrorCode.Should().Be(HostUpdateRecoveryRequestResolver.BindingMismatch);
    }

    [Fact]
    public void Request_id_must_match_when_supplied()
    {
        HostUpdateExecutionRequest request = Request();
        HostUpdateExecutionActivity[] history = [Bound(request, HostUpdateExecutionState.RecoveryRequired, "failure")];

        HostUpdateRecoveryRequestResolver.Resolve(history, "other").ErrorCode.Should().Be(HostUpdateRecoveryRequestResolver.RequestMismatch);
        HostUpdateRecoveryRequestResolution matched = HostUpdateRecoveryRequestResolver.Resolve(history, "request-1");
        matched.Succeeded.Should().BeTrue();
        matched.Request.Should().Be(request);
        HostUpdateRecoveryRequestResolver.Resolve(history, null).Request.Should().Be(request);
    }

    [Theory]
    [InlineData("stable:1.2.3", true)]
    [InlineData("insider:1.2.3-rc.1", true)]
    [InlineData("1.2.3", false)]
    [InlineData("stable:../../etc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Release_id_grammar_is_enforced(string? releaseId, bool expected)
    {
        HostUpdateRecoveryRequestResolver.IsValidReleaseId(releaseId).Should().Be(expected);
    }

    private static HostUpdateExecutionActivity Bound(HostUpdateExecutionRequest request, HostUpdateExecutionState state, string phase) =>
        new(Guid.NewGuid().ToString("N"), request.ReleaseId, state, phase, DateTimeOffset.UtcNow)
        {
            RequestBindingHash = HostUpdateRequestBinding.Compute(request),
            RequestBinding = request,
        };

    private static HostUpdateExecutionRequest Request() =>
        new("stable:1.2.3", 1, "sha256:" + new string('a', 64), new string('b', 40), HostUpdateExecutionChannel.Stable,
            [new HostUpdateExecutionTarget("api", "linux-amd64", "sha256:" + new string('a', 64))])
        {
            RequestId = "request-1",
            TrustRoot = "trust-root",
            PolicyRevision = 1,
            PolicyFingerprint = "policy",
            HostPlatform = "linux-amd64",
            AuthorizationKind = HostUpdateAuthorizationKind.Manual,
        };
}
