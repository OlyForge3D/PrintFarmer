using System.Security;
using Farm.Infrastructure.Services.HostUpdates;
using Farm.Modules.Administration.Controllers.Admin;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Farm.Modules.Administration.Tests.Controllers;

/// <summary>
/// Item 3 coverage: corrupt/unavailable authorization state, replay, and journal I/O must
/// surface as 503 ProblemDetails from the host-update admin endpoints, never 409/500.
/// </summary>
public sealed class HostUpdateControllerAvailabilityTests
{
    [Fact]
    public async Task AuthorizeAsync_returns_503_when_authorization_state_is_corrupt()
    {
        HostUpdateController controller = new(
            new NoopExecutor(),
            new ThrowingResolver(new InvalidDataException("authorization_state_invalid")),
            new NoopJournal(),
            new NoopRecovery());

        ActionResult<HostUpdateManualAuthorizationResponse> result = await controller.AuthorizeAsync(null, default);

        ObjectResult unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
    }

    [Fact]
    public async Task AuthorizeAsync_returns_503_when_authorization_state_access_is_denied_by_security_policy()
    {
        HostUpdateController controller = new(
            new NoopExecutor(),
            new ThrowingResolver(new SecurityException("host_state_reparse_path_rejected")),
            new NoopJournal(),
            new NoopRecovery());

        ActionResult<HostUpdateManualAuthorizationResponse> result = await controller.AuthorizeAsync(null, default);

        ObjectResult unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
    }

    [Fact]
    public async Task ExecuteAsync_returns_503_when_replay_state_is_unavailable_during_resolution()
    {
        HostUpdateController controller = new(
            new NoopExecutor(),
            new ThrowingResolveManualResolver(new InvalidDataException("replay_unavailable")),
            new NoopJournal(),
            new NoopRecovery());

        ActionResult<HostUpdateStatusResponse> result = await controller.ExecuteAsync(null, default);

        ObjectResult unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
    }

    [Fact]
    public async Task ExecuteAsync_returns_503_when_resolution_reports_authorization_state_unavailable()
    {
        HostUpdateController controller = new(
            new NoopExecutor(),
            new FailingResolveManualResolver("authorization_state_unavailable"),
            new NoopJournal(),
            new NoopRecovery());

        ActionResult<HostUpdateStatusResponse> result = await controller.ExecuteAsync(null, default);

        ObjectResult unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
    }

    [Fact]
    public async Task ExecuteAsync_returns_503_when_journal_is_corrupt_during_execution()
    {
        HostUpdateController controller = new(
            new ThrowingExecutor(new InvalidDataException("journal_corrupt")),
            new SucceedingResolveManualResolver(),
            new NoopJournal(),
            new NoopRecovery());

        ActionResult<HostUpdateStatusResponse> result = await controller.ExecuteAsync(null, default);

        ObjectResult unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
    }

    [Fact]
    public async Task GetStatus_returns_503_when_journal_is_corrupt()
    {
        HostUpdateController controller = new(
            new NoopExecutor(),
            new NoopResolver(),
            new ThrowingJournal(new InvalidDataException("journal_corrupt")),
            new NoopRecovery());

        ActionResult<HostUpdateStatusResponse> result = controller.GetStatus("rel-1");

        ObjectResult unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
    }

    [Fact]
    public async Task GetStatus_returns_503_when_journal_access_is_denied_by_security_policy()
    {
        HostUpdateController controller = new(
            new NoopExecutor(),
            new NoopResolver(),
            new ThrowingJournal(new SecurityException("host_state_owner_mismatch")),
            new NoopRecovery());

        ActionResult<HostUpdateStatusResponse> result = controller.GetStatus("rel-1");

        ObjectResult unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
    }

    [Fact]
    public async Task RecoverAsync_returns_503_when_journal_is_unavailable()
    {
        HostUpdateController controller = new(
            new NoopExecutor(),
            new NoopResolver(),
            new ThrowingJournal(new IOException("journal_unavailable")),
            new NoopRecovery());

        ActionResult<HostUpdateRecoveryResult> result = await controller.RecoverAsync("rel-1", null, default);

        ObjectResult unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
    }

    [Fact]
    public async Task AuthorizeAsync_returns_503_when_production_resolver_has_unprovisioned_replay_store()
    {
        using ProductionResolverHarness harness = new(new UnavailableHostUpdateReplayStore());
        HostUpdateController controller = harness.CreateController();

        ActionResult<HostUpdateManualAuthorizationResponse> result = await controller.AuthorizeAsync(null, default);

        ObjectResult unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
        Assert.Equal("host_update_replay_store_not_available", Detail(unavailable));
    }

    [Fact]
    public async Task ExecuteAsync_returns_503_when_production_resolver_has_unprovisioned_replay_store()
    {
        using ProductionResolverHarness harness = new(new UnavailableHostUpdateReplayStore());
        HostUpdateController controller = harness.CreateController();

        ActionResult<HostUpdateStatusResponse> result = await controller.ExecuteAsync(null, default);

        ObjectResult unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
        Assert.Equal("host_update_replay_store_not_available", Detail(unavailable));
    }

    [Fact]
    public async Task ExecuteAsync_returns_503_when_production_resolver_cannot_read_replay_state()
    {
        using ProductionResolverHarness harness = new(new UnreadableReplayStore());
        HostUpdateController controller = harness.CreateController();

        ActionResult<HostUpdateStatusResponse> result = await controller.ExecuteAsync(null, default);

        ObjectResult unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
        Assert.Equal(HostUpdateAvailabilityCodes.ReplayUnavailable, Detail(unavailable));
    }

    [Fact]
    public async Task ExecuteAsync_returns_409_for_an_actual_replay_conflict()
    {
        using ProductionResolverHarness harness = new(new MemoryReplayStore());
        HostUpdateController controller = harness.CreateController();
        ActionResult<HostUpdateStatusResponse> first = await controller.ExecuteAsync(null, default);
        Assert.IsType<OkObjectResult>(first.Result);

        ActionResult<HostUpdateStatusResponse> replay = await controller.ExecuteAsync(null, default);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(replay.Result);
        Assert.Equal(409, conflict.StatusCode);
        Assert.Contains("candidate_replay_rejected", conflict.Value?.ToString(), StringComparison.Ordinal);
    }

    private static string? Detail(ObjectResult result) =>
        Assert.IsType<Microsoft.AspNetCore.Mvc.ProblemDetails>(result.Value, exactMatch: false).Detail;

    /// <summary>
    /// Wires the real <see cref="HostUpdateExecutionRequestResolver"/> so classification is proven
    /// on the production code path, not on a hand-written resolver stub.
    /// </summary>
    private sealed class ProductionResolverHarness(IHostUpdateReplayStore replayStore) : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("printfarmer-hostupdate-availability-").FullName;

        public HostUpdateController CreateController() => new(
            new CompletingExecutor(),
            new HostUpdateExecutionRequestResolver(
                new FixedSettings(),
                new FixedCandidateCache(Candidate),
                replayStore,
                new MemoryManifestBindingStore(),
                new FileHostUpdateManualAuthorizationStore(_root),
                new InactiveHostUpdateAdmissionFence(),
                new FixedClock()),
            new NoopJournal(),
            new NoopRecovery());

        public static VerifiedHostUpdateCandidate Candidate { get; } = new(
            "release-1",
            new string('a', 40),
            42,
            "sha256:" + new string('b', 64),
            Farm.Infrastructure.Settings.UpdateChannelSettings.StableChannel,
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

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Temp cleanup is best-effort.
            }
        }
    }

    private sealed class FixedSettings : IHostUpdateSchedulerSettings
    {
        public HostUpdateSchedulerSettings Current { get; } = new(
            Channel: Farm.Infrastructure.Settings.UpdateChannelSettings.StableChannel,
            PolicyRevision: 1);
    }

    private sealed class FixedCandidateCache(VerifiedHostUpdateCandidate candidate) : IHostUpdateSchedulerCandidateCache
    {
        public VerifiedHostUpdateCandidate? Current => candidate;

        public string? LastError => null;
    }

    private sealed class FixedClock : IHostUpdateClock
    {
        public DateTimeOffset UtcNow => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class MemoryManifestBindingStore : IVerifiedReleaseManifestBindingStore
    {
        private readonly Dictionary<string, string> _bindings = new(StringComparer.Ordinal);

        public Task EnsureBoundAsync(string releaseId, string manifestDigest, CancellationToken cancellationToken)
        {
            if (_bindings.TryGetValue(releaseId, out string? existing) && !string.Equals(existing, manifestDigest, StringComparison.Ordinal))
            {
                throw new InvalidDataException("manifest_binding_conflict");
            }

            _bindings[releaseId] = manifestDigest;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryReplayStore : IHostUpdateReplayStore
    {
        private readonly HashSet<string> _admitted = new(StringComparer.Ordinal);

        public Task<HostUpdateReplayDecision> DecideAsync(VerifiedHostUpdateCandidate candidate, HostUpdateReplayIntent intent, CancellationToken ct)
        {
            if (_admitted.Contains(candidate.Identity))
            {
                return Task.FromResult(new HostUpdateReplayDecision(HostUpdateReplayDisposition.Rejected, candidate.Identity, true));
            }

            if (intent == HostUpdateReplayIntent.Admit)
            {
                _admitted.Add(candidate.Identity);
            }

            return Task.FromResult(new HostUpdateReplayDecision(HostUpdateReplayDisposition.Accepted, candidate.Identity, false));
        }
    }

    private sealed class UnreadableReplayStore : IHostUpdateReplayStore
    {
        public Task<HostUpdateReplayDecision> DecideAsync(VerifiedHostUpdateCandidate candidate, HostUpdateReplayIntent intent, CancellationToken ct) =>
            throw new InvalidDataException("host_update_replay_state_corrupt");
    }

    private sealed class CompletingExecutor : IHostUpdateExecutor
    {
        public Task<HostUpdateExecutionResult> ExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HostUpdateExecutionResult(request.ReleaseId, HostUpdateExecutionState.Completed, null, []));
    }

    private sealed class ThrowingResolver(Exception exception) : IHostUpdateExecutionRequestResolver
    {
        public Task<HostUpdateManualAuthorizationResponse> AuthorizeCurrentAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) =>
            throw exception;

        public Task<HostUpdateExecutionResolutionResult> ResolveManualAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingResolveManualResolver(Exception exception) : IHostUpdateExecutionRequestResolver
    {
        public Task<HostUpdateManualAuthorizationResponse> AuthorizeCurrentAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<HostUpdateExecutionResolutionResult> ResolveManualAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) =>
            throw exception;
    }

    private sealed class FailingResolveManualResolver(string error) : IHostUpdateExecutionRequestResolver
    {
        public Task<HostUpdateManualAuthorizationResponse> AuthorizeCurrentAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<HostUpdateExecutionResolutionResult> ResolveManualAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) =>
            Task.FromResult(HostUpdateExecutionResolutionResult.Fail(error));
    }

    private sealed class SucceedingResolveManualResolver : IHostUpdateExecutionRequestResolver
    {
        public Task<HostUpdateManualAuthorizationResponse> AuthorizeCurrentAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<HostUpdateExecutionResolutionResult> ResolveManualAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) =>
            Task.FromResult(HostUpdateExecutionResolutionResult.Ok(Request(), Authorization()));

        private static HostUpdateExecutionRequest Request() => new("rel-1", 1, "sha256:" + new string('a', 64), new string('b', 40), HostUpdateExecutionChannel.Stable,
        [
            new("api", "linux-amd64", "sha256:" + new string('a', 64)),
        ])
        {
            RequestId = "request-1",
            TrustRoot = "trust-root",
            PolicyRevision = 1,
            PolicyFingerprint = "policy",
            HostPlatform = "linux-amd64",
            AuthorizationKind = HostUpdateAuthorizationKind.Manual,
        };

        private static HostUpdateManualAuthorizationRecord Authorization() => new(
            "auth-1",
            "candidate-fingerprint",
            "rel-1",
            1,
            "stable",
            "sha256:" + new string('a', 64),
            new string('b', 40),
            "trust-root",
            "linux-amd64",
            new HostUpdatePlatformDigests(
                "sha256:" + new string('a', 64),
                "sha256:" + new string('b', 64),
                "sha256:" + new string('c', 64),
                "sha256:" + new string('d', 64),
                "sha256:" + new string('e', 64),
                "sha256:" + new string('f', 64)),
            "replay-1",
            1,
            "policy",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            null,
            null,
            "binding-hash");
    }

    private sealed class ThrowingExecutor(Exception exception) : IHostUpdateExecutor
    {
        public Task<HostUpdateExecutionResult> ExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw exception;
    }

    private sealed class ThrowingJournal(Exception exception) : IHostUpdateExecutionJournal
    {
        public IReadOnlyList<HostUpdateExecutionActivity> Read(string releaseId) => throw exception;

        public IReadOnlyList<string> ListReleaseIds() => throw exception;

        public void Append(HostUpdateExecutionActivity activity) => throw new NotSupportedException();
    }

    private sealed class NoopJournal : IHostUpdateExecutionJournal
    {
        public IReadOnlyList<HostUpdateExecutionActivity> Read(string releaseId) => Array.Empty<HostUpdateExecutionActivity>();

        public IReadOnlyList<string> ListReleaseIds() => Array.Empty<string>();

        public void Append(HostUpdateExecutionActivity activity) => throw new NotSupportedException();
    }

    private sealed class NoopRecovery : IHostUpdateRecoveryCoordinator
    {
        public Task<HostUpdateRecoveryResult> RecoverAsync(HostUpdateExecutionRequest failedRequest, IReadOnlyList<HostUpdateExecutionActivity> activities, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NoopExecutor : IHostUpdateExecutor
    {
        public Task<HostUpdateExecutionResult> ExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoopResolver : IHostUpdateExecutionRequestResolver
    {
        public Task<HostUpdateManualAuthorizationResponse> AuthorizeCurrentAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) => throw new NotSupportedException();
        public Task<HostUpdateExecutionResolutionResult> ResolveManualAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) => throw new NotSupportedException();
    }
}
