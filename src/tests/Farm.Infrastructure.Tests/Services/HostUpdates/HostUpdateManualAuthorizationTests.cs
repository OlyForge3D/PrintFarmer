#pragma warning disable VSTHRD003
using Farm.Infrastructure.Services.HostUpdates;
using Farm.Infrastructure.Settings;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateManualAuthorizationTests
{
    [Fact]
    public async Task ResolveManualAsync_ConsumesOneTimeAuthorizationAndMapsOnlyServerCandidate()
    {
        ResolverHarness harness = new();
        HostUpdateManualAuthorizationResponse authorization = await harness.Resolver.AuthorizeCurrentAsync(new(), default);

        HostUpdateExecutionResolutionResult result = await harness.Resolver.ResolveManualAsync(new(authorization.AuthorizationId), default);

        Assert.True(result.Succeeded, result.Error);
        Assert.NotNull(result.Request);
        Assert.Equal(authorization.AuthorizationId, result.Request.RequestId);
        Assert.Equal(HostUpdateAuthorizationKind.Manual, result.Request.AuthorizationKind);
        Assert.Equal(harness.Candidate.ManifestDigest, result.Request.ManifestDigest);
        Assert.Equal(harness.Candidate.Sequence, result.Request.AuthenticatedSequence);
        Assert.Equal(harness.Candidate.Channel, result.Request.Channel.ToString().ToLowerInvariant());
        Assert.Equal(HostUpdateExecutionRequest.RequiredServiceIds, result.Request.Targets.Select(t => t.ServiceId).ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ResolveManualAsync_RejectsConsumedAuthorizationReplay()
    {
        ResolverHarness harness = new();
        HostUpdateManualAuthorizationResponse authorization = await harness.Resolver.AuthorizeCurrentAsync(new(), default);
        Assert.True((await harness.Resolver.ResolveManualAsync(new(authorization.AuthorizationId), default)).Succeeded);

        HostUpdateExecutionResolutionResult replay = await harness.Resolver.ResolveManualAsync(new(authorization.AuthorizationId), default);

        Assert.False(replay.Succeeded);
        Assert.Equal("authorization_consumed", replay.Error);
    }

    [Fact]
    public async Task ResolveManualAsync_RejectsPolicyRevisionDrift()
    {
        ResolverHarness harness = new();
        HostUpdateManualAuthorizationResponse authorization = await harness.Resolver.AuthorizeCurrentAsync(new(), default);
        harness.Settings.Current = harness.Settings.Current with { PolicyRevision = 2 };

        HostUpdateExecutionResolutionResult result = await harness.Resolver.ResolveManualAsync(new(authorization.AuthorizationId), default);

        Assert.False(result.Succeeded);
        Assert.Equal("authorization_policy_drift", result.Error);
    }

    [Fact]
    public async Task ResolveManualAsync_RejectsCandidateDigestRebind()
    {
        ResolverHarness harness = new();
        HostUpdateManualAuthorizationResponse authorization = await harness.Resolver.AuthorizeCurrentAsync(new(), default);
        harness.Cache.CurrentValue = harness.Candidate with { ManifestDigest = "sha256:" + new string('9', 64) };

        HostUpdateExecutionResolutionResult result = await harness.Resolver.ResolveManualAsync(new(authorization.AuthorizationId), default);

        Assert.False(result.Succeeded);
        Assert.Equal("manifest_binding_conflict", result.Error);
    }

    [Fact]
    public async Task ResolveManualAsync_RejectsExpiredAuthorization()
    {
        ResolverHarness harness = new(ttl: TimeSpan.FromSeconds(1));
        HostUpdateManualAuthorizationResponse authorization = await harness.Resolver.AuthorizeCurrentAsync(new(), default);
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddSeconds(2);

        HostUpdateExecutionResolutionResult result = await harness.Resolver.ResolveManualAsync(new(authorization.AuthorizationId), default);

        Assert.False(result.Succeeded);
        Assert.Equal("authorization_expired", result.Error);
    }

    [Fact]
    public async Task ResolveManualAsync_RejectsStaleCacheLastKnownGood()
    {
        ResolverHarness harness = new();
        harness.Cache.Error = "network_failure";

        HostUpdateExecutionResolutionResult result = await harness.Resolver.ResolveManualAsync(new(), default);

        Assert.False(result.Succeeded);
        Assert.Equal("candidate_cache_error", result.Error);
    }

    [Fact]
    public async Task ResolveManualAsync_RejectsWrongSelectedChannel()
    {
        ResolverHarness harness = new();
        harness.Settings.Current = harness.Settings.Current with { Channel = UpdateChannelSettings.InsiderChannel };

        HostUpdateExecutionResolutionResult result = await harness.Resolver.ResolveManualAsync(new(), default);

        Assert.False(result.Succeeded);
        Assert.Equal("candidate_channel_mismatch", result.Error);
    }

    [Fact]
    public async Task ResolveManualAsync_RejectsHighWaterReplayDuplicateForCombinedIntent()
    {
        ResolverHarness harness = new();
        Assert.True((await harness.Resolver.ResolveManualAsync(new(), default)).Succeeded);

        HostUpdateExecutionResolutionResult duplicate = await harness.Resolver.ResolveManualAsync(new(), default);

        Assert.False(duplicate.Succeeded);
        Assert.Equal("candidate_replay_rejected", duplicate.Error);
    }

    [Fact]
    public async Task ResolveManualAsync_OnlyOneConcurrentConsumerWins()
    {
        ResolverHarness harness = new();
        HostUpdateManualAuthorizationResponse authorization = await harness.Resolver.AuthorizeCurrentAsync(new(), default);

        HostUpdateExecutionResolutionResult[] results = await Task.WhenAll(
            harness.Resolver.ResolveManualAsync(new(authorization.AuthorizationId), default),
            harness.Resolver.ResolveManualAsync(new(authorization.AuthorizationId), default));

        Assert.Single(results, r => r.Succeeded);
        Assert.Single(results, r => !r.Succeeded && r.Error == "authorization_consumed");
    }

    [Fact]
    public async Task ResolveManualAsync_RefusesWhenAdmissionFenceIsActive()
    {
        ResolverHarness harness = new(fence: new FixedFence(new(true, "host_update_operation_active", "op-1")));

        HostUpdateExecutionResolutionResult result = await harness.Resolver.ResolveManualAsync(new(), default);

        Assert.False(result.Succeeded);
        Assert.Equal("host_update_operation_active", result.Error);
    }

    private sealed class ResolverHarness
    {
        public ResolverHarness(TimeSpan? ttl = null, IHostUpdateAdmissionFence? fence = null)
        {
            string root = Path.Combine(Path.GetTempPath(), "printfarmer-manual-auth-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            AuthorizationStore = new FileHostUpdateManualAuthorizationStore(root, ttl);
            Replay = new MemoryReplayStore();
            Cache = new MutableCache(Candidate);
            Resolver = new HostUpdateExecutionRequestResolver(Settings, Cache, Replay, new MemoryManifestBindingStore(), AuthorizationStore, fence ?? new InactiveHostUpdateAdmissionFence(), Clock);
        }

        public VerifiedHostUpdateCandidate Candidate { get; } = new(
            "release-1",
            new string('a', 40),
            42,
            "sha256:" + new string('b', 64),
            UpdateChannelSettings.StableChannel,
            true,
            true,
            true,
            true,
            true,
            true,
            new HostUpdatePlatformDigests(
                "sha256:" + new string('c', 64),
                "sha256:" + new string('d', 64),
                "sha256:" + new string('e', 64),
                "sha256:" + new string('f', 64),
                "sha256:" + new string('1', 64),
                "sha256:" + new string('2', 64)),
            HostPlatform: "linux-amd64");

        public MutableSettings Settings { get; } = new(new HostUpdateSchedulerSettings(Channel: UpdateChannelSettings.StableChannel, PolicyRevision: 1));
        public MutableClock Clock { get; } = new(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        public FileHostUpdateManualAuthorizationStore AuthorizationStore { get; }
        public MemoryReplayStore Replay { get; }
        public MutableCache Cache { get; }
        public HostUpdateExecutionRequestResolver Resolver { get; }
    }

    private sealed class MutableSettings(HostUpdateSchedulerSettings value) : IHostUpdateSchedulerSettings
    {
        public HostUpdateSchedulerSettings Current { get; set; } = value;
    }

    private sealed class MutableCache(VerifiedHostUpdateCandidate current) : IHostUpdateSchedulerCandidateCache
    {
        public VerifiedHostUpdateCandidate? Current => CurrentValue;
        public VerifiedHostUpdateCandidate? CurrentValue { get; set; } = current;
        public string? Error { get; set; }
        public string? LastError => Error;
    }

    private sealed class MutableClock(DateTimeOffset now) : IHostUpdateClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class FixedFence(HostUpdateAdmissionFenceStatus status) : IHostUpdateAdmissionFence
    {
        public HostUpdateAdmissionFenceStatus GetStatus() => status;
    }

    private sealed class MemoryManifestBindingStore : IVerifiedReleaseManifestBindingStore
    {
        private readonly Dictionary<string, string> _bindings = new(StringComparer.Ordinal);
        public Task EnsureBoundAsync(string releaseId, string manifestDigest, CancellationToken cancellationToken)
        {
            if (_bindings.TryGetValue(releaseId, out string? existing) && existing != manifestDigest)
            {
                throw new InvalidDataException("conflict");
            }

            _bindings[releaseId] = manifestDigest;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryReplayStore : IHostUpdateReplayStore
    {
        private readonly Dictionary<string, (long Sequence, string Identity)> _highWater = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (HostUpdateReplayDisposition Disposition, string CorrelationId)> _identities = new(StringComparer.Ordinal);

        public Task<HostUpdateReplayDecision> DecideAsync(VerifiedHostUpdateCandidate candidate, HostUpdateReplayIntent intent, CancellationToken ct)
        {
            if (_identities.TryGetValue(candidate.Identity, out (HostUpdateReplayDisposition Disposition, string CorrelationId) existing))
            {
                return Task.FromResult(new HostUpdateReplayDecision(existing.Disposition, existing.CorrelationId, true));
            }

            string ns = candidate.TrustRoot + "::" + candidate.Channel;
            string correlationId = "decision:" + candidate.Identity;
            _highWater.TryGetValue(ns, out (long Sequence, string Identity) highWater);
            if (highWater.Identity is not null && (candidate.Sequence < highWater.Sequence || candidate.Sequence == highWater.Sequence && highWater.Identity != candidate.Identity))
            {
                _identities[candidate.Identity] = (HostUpdateReplayDisposition.Rejected, correlationId);
                return Task.FromResult(new HostUpdateReplayDecision(HostUpdateReplayDisposition.Rejected, correlationId, false));
            }

            HostUpdateReplayDisposition disposition = intent == HostUpdateReplayIntent.Admit ? HostUpdateReplayDisposition.Accepted : HostUpdateReplayDisposition.Rejected;
            _highWater[ns] = (candidate.Sequence, candidate.Identity);
            _identities[candidate.Identity] = (disposition, correlationId);
            return Task.FromResult(new HostUpdateReplayDecision(disposition, correlationId, false));
        }
    }
}
