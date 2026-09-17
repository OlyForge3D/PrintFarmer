using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateSideEffectReconcilerTests
{
    private static HostUpdateExecutionRequest Request() => new(
        "rel-1",
        1,
        "sha256:" + new string('a', 64),
        new string('b', 40),
        HostUpdateExecutionChannel.Stable,
        Enumerable.Range(1, 6)
            .Select(i => new HostUpdateExecutionTarget("svc-" + i, "linux/amd64", "sha256:" + new string("abcdef"[i - 1], 64)))
            .ToArray());

    [Fact]
    public async Task ReconcileAsync_MigrationRequiresAllContextsAtTarget()
    {
        var reconciler = new HostUpdateSideEffectReconciler(new FakeMigrationReconciler(true), new FakeDigestVerifier());

        HostUpdateSideEffectReconciliation result = await reconciler.ReconcileAsync("migration", Request(), CancellationToken.None);

        result.Reconciled.Should().BeTrue();
        result.Detail.Should().Be("migration_state_matches");
    }

    [Fact]
    public async Task ReconcileAsync_MigrationReturnsUncertainWhenAnyContextHasPendingMigrations()
    {
        var reconciler = new HostUpdateSideEffectReconciler(new FakeMigrationReconciler(false), new FakeDigestVerifier());

        HostUpdateSideEffectReconciliation result = await reconciler.ReconcileAsync("migration", Request(), CancellationToken.None);

        result.Reconciled.Should().BeFalse();
        result.Detail.Should().Be("migration_state_incomplete");
    }

    [Fact]
    public async Task ReconcileAsync_ApplyRequiresExactRunningDigestProof()
    {
        var digestVerifier = new FakeDigestVerifier();
        var reconciler = new HostUpdateSideEffectReconciler(new FakeMigrationReconciler(true), digestVerifier);

        HostUpdateSideEffectReconciliation result = await reconciler.ReconcileAsync("apply", Request(), CancellationToken.None);

        result.Reconciled.Should().BeTrue();
        digestVerifier.VerifiedDigests.Should().HaveCount(6);
        result.Detail.Should().Be("running_digests_match");
    }

    [Fact]
    public async Task ReconcileAsync_ApplyReturnsUncertainWhenRunningDigestProofFails()
    {
        var reconciler = new HostUpdateSideEffectReconciler(new FakeMigrationReconciler(true), new FakeDigestVerifier(throwOnVerify: true));

        HostUpdateSideEffectReconciliation result = await reconciler.ReconcileAsync("apply", Request(), CancellationToken.None);

        result.Reconciled.Should().BeFalse();
        result.Detail.Should().StartWith("running_digests_unverified:");
    }

    private sealed class FakeMigrationReconciler(bool reconciled) : IHostUpdateMigrationReconciler
    {
        public Task<bool> IsReconciledAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(reconciled);
    }

    private sealed class FakeDigestVerifier(bool throwOnVerify = false) : IHostUpdateDigestVerifier
    {
        public IReadOnlyDictionary<string, string> VerifiedDigests { get; private set; } = new Dictionary<string, string>();

        public Task VerifyDigestsAsync(IReadOnlyDictionary<string, string> expectedDigestsByService, CancellationToken cancellationToken)
        {
            if (throwOnVerify)
            {
                throw new InvalidOperationException("digest_mismatch");
            }

            VerifiedDigests = expectedDigestsByService;
            return Task.CompletedTask;
        }
    }
}
